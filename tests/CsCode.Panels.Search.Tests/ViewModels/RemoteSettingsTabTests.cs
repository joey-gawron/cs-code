using System.Text.Json;
using System.Text.Json.Nodes;
using CsCode.Core.Debugging;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Remote;
using CsCode.Core.Text;
using CsCode.Panels.Search.Settings;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CsCode.Panels.Search.Tests.ViewModels;

/// <summary>
/// A remote host's machine settings file as the Remote module serves it, over a local file: <see cref="SettingsUri"/> is that file's URI (the real
/// one is an ssh: URI the routing file system reads), writes edit it the way the server does and raise <see cref="Changed"/>.
/// </summary>
internal sealed class FakeRemoteMachineSettings(string path, string label = "SSH: box") : IRemoteMachineSettings
{
    private readonly Emitter<Uri?> _changed = new();
    private volatile bool _connected = true;

    public List<(string Key, object? Value)> Writes { get; } = new();
    public Exception? FailWrites { get; set; }
    public string Path => path;

    public Uri? SettingsUri => _connected ? new Uri(path) : null;
    public string? HostLabel => _connected ? label : null;
    public IEventSource<Uri?> Changed => _changed;

    public ValueTask UpdateAsync(string key, object? value, CancellationToken ct = default)
    {
        lock (Writes) Writes.Add((key, value));
        if (FailWrites is { } fail) throw fail;
        var root = File.Exists(path) && File.ReadAllText(path) is { Length: > 0 } text ? JsonNode.Parse(text, documentOptions: new() { CommentHandling = JsonCommentHandling.Skip })!.AsObject() : new JsonObject();
        if (value is null) root.Remove(key);
        else root[key] = value is JsonElement e ? JsonNode.Parse(e.GetRawText()) : JsonSerializer.SerializeToNode(value);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, root.ToJsonString());
        _changed.Fire(SettingsUri);
        return ValueTask.CompletedTask;
    }

    public void SetConnected(bool connected)
    {
        _connected = connected;
        _changed.Fire(SettingsUri);
    }

    public void ChangedOnHost(string json)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
        _changed.Fire(SettingsUri);
    }
}

public class RemoteSettingsTabTests : IDisposable
{
    private readonly VmFixture _f = new();
    private readonly List<IDisposable> _owned = [];
    private readonly FakeRemoteMachineSettings _remote;

    public RemoteSettingsTabTests() => _remote = new FakeRemoteMachineSettings(_f.Harness.Dir.Combine("remote-data", "machine", "settings.json"));

    public void Dispose()
    {
        foreach (var d in _owned) d.Dispose();
        _f.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ConfigurationProperty P(string key, object? def, ConfigurationScope scope = ConfigurationScope.Window, string? schema = null) =>
        new(key, JsonSerializer.SerializeToElement(def), "Description of " + key, schema is null ? null : JsonDocument.Parse(schema).RootElement.Clone()) { Scope = scope };

    private SettingsViewModel Make(IRemoteMachineSettings? remote, int debounceMs = 10)
    {
        _owned.Add(_f.ConfigStore.RegisterProperties(
        [
            P("editor.tabSize", 4, schema: """{"type":"integer","minimum":1}"""),
            P("editor.insertSpaces", true, schema: """{"type":"boolean"}"""),
            P("cscode.debug.gdbPath", "gdb", ConfigurationScope.Machine, """{"type":"string"}"""),
            P("cscode.languageHost", "inProcess", ConfigurationScope.Application, """{"type":"string"}"""),
        ]));
        var vm = new SettingsViewModel(_f.With(remote: remote), debounceMs);
        _owned.Add(vm);
        return vm;
    }

    private static SettingItemViewModel Item(SettingsViewModel vm, string key) => vm.AllItems.Single(i => i.Key == key);

    [Fact]
    public async Task The_remote_tab_is_there_only_while_a_remote_host_is_connected()
    {
        var local = Make(remote: null);
        Assert.False(local.IsRemoteAvailable);
        local.SelectedScope = SettingsScope.Remote;
        Assert.Equal(SettingsScope.User, local.SelectedScope);

        var vm = Make(_remote);
        Assert.True(vm.IsRemoteAvailable);
        Assert.Equal("Remote [SSH: box]", vm.RemoteTabTitle);
        vm.IsRemoteScope = true;
        Assert.Equal(SettingsScope.Remote, vm.SelectedScope);

        _remote.SetConnected(false); // the host went away: the tab goes, and with it the scope
        await _f.Ui.PumpUntilAsync(() => vm.SelectedScope == SettingsScope.User);
        Assert.False(vm.IsRemoteAvailable);
        Assert.Equal("Remote", vm.RemoteTabTitle);
        _remote.SetConnected(true);
        await _f.Ui.PumpUntilAsync(() => vm.IsRemoteAvailable);
    }

    [Fact]
    public async Task The_remote_tab_shows_the_host_s_file_and_edits_go_to_the_host_not_the_user_settings()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_remote.Path)!);
        await File.WriteAllTextAsync(_remote.Path, "// the host's own\n{ \"editor.tabSize\": 8 }", Ct);
        var vm = Make(_remote);
        await vm.RefreshAsync();
        Assert.Equal("4", ((NumberSettingViewModel)Item(vm, "editor.tabSize")).Text);       // the User tab: the client's value
        Assert.Equal("Remote [SSH: box]", Item(vm, "editor.tabSize").AlsoModifiedIn);     // "(also modified in Remote [SSH: box])"

        vm.SelectedScope = SettingsScope.Remote;
        await vm.RefreshAsync();
        Assert.Equal("8", ((NumberSettingViewModel)Item(vm, "editor.tabSize")).Text);
        Assert.True(Item(vm, "editor.tabSize").IsModified);
        Assert.False(Item(vm, "editor.insertSpaces").IsModified);

        ((BoolSettingViewModel)Item(vm, "editor.insertSpaces")).Value = false;
        await vm.WaitIdleAsync();
        Assert.Contains(_remote.Writes, w => w.Key == "editor.insertSpaces" && w.Value is false);
        Assert.DoesNotContain(_f.Recording.Writes, w => w.Key == "editor.insertSpaces"); // never into the client's settings.json
        Assert.Contains("\"editor.insertSpaces\":false", await File.ReadAllTextAsync(_remote.Path, Ct));
    }

    [Fact]
    public async Task Machine_settings_show_the_host_s_value_or_the_default_and_application_settings_are_left_out()
    {
        var vm = Make(_remote);
        _f.ConfigStore.Set("cscode.debug.gdbPath", "/opt/homebrew/bin/gdb"); // the client's gdb: a program on the client's machine
        await vm.RefreshAsync();
        Assert.Equal("/opt/homebrew/bin/gdb", ((StringSettingViewModel)Item(vm, "cscode.debug.gdbPath")).Value);
        Assert.Contains(vm.Items, i => i.Key == "cscode.languageHost");

        vm.SelectedScope = SettingsScope.Remote;
        await vm.RefreshAsync();
        Assert.Equal("gdb", ((StringSettingViewModel)Item(vm, "cscode.debug.gdbPath")).Value);  // the remote host never takes the client's
        Assert.DoesNotContain(vm.Items, i => i.Key == "cscode.languageHost");                   // one value for the whole application
        vm.SearchText = "languageHost";
        Assert.Empty(vm.Items);

        _remote.ChangedOnHost("{ \"cscode.debug.gdbPath\": \"/opt/gdb-14/bin/gdb\" }");     // edited on the host (or in the editor)
        await _f.Ui.PumpUntilAsync(() => ((StringSettingViewModel)Item(vm, "cscode.debug.gdbPath")).Value == "/opt/gdb-14/bin/gdb");
        Assert.True(Item(vm, "cscode.debug.gdbPath").IsModified);
    }

    [Fact]
    public async Task Reset_on_the_remote_tab_removes_the_key_from_the_host_s_file()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_remote.Path)!);
        await File.WriteAllTextAsync(_remote.Path, "{ \"editor.tabSize\": 8 }", Ct);
        var vm = Make(_remote);
        vm.SelectedScope = SettingsScope.Remote;
        await vm.RefreshAsync();
        await vm.ResetAsync(Item(vm, "editor.tabSize"));
        Assert.Contains(_remote.Writes, w => w is { Key: "editor.tabSize", Value: null });
        Assert.False(Item(vm, "editor.tabSize").IsModified);
        Assert.Equal("4", ((NumberSettingViewModel)Item(vm, "editor.tabSize")).Text);
        Assert.Empty(_f.Recording.Writes);
    }

    [Fact]
    public async Task A_remote_write_that_fails_is_shown_on_the_setting()
    {
        var vm = Make(_remote);
        vm.SelectedScope = SettingsScope.Remote;
        _remote.FailWrites = new IOException("Not connected to box: connection lost");
        ((BoolSettingViewModel)Item(vm, "editor.insertSpaces")).Value = false;
        await vm.WaitIdleAsync();
        Assert.Equal("Could not save: Not connected to box: connection lost", Item(vm, "editor.insertSpaces").Error);
    }

    [Fact]
    public async Task Open_settings_json_on_the_remote_tab_opens_the_host_s_file_and_edit_in_json_adds_the_default_of_a_machine_setting()
    {
        var vm = Make(_remote);
        _f.ConfigStore.Set("cscode.debug.gdbPath", "/opt/homebrew/bin/gdb");
        vm.SelectedScope = SettingsScope.Remote;
        await vm.OpenSettingsJsonAsync();
        var (opened, _) = Assert.Single(_f.Navigation.Opened);
        Assert.Equal(_remote.SettingsUri, opened);
        Assert.True(File.Exists(_remote.Path)); // created with {} as the user file is

        await vm.EditInJsonAsync(Item(vm, "cscode.debug.gdbPath"));
        Assert.Contains(_remote.Writes, w => w.Key == "cscode.debug.gdbPath" && w.Value is JsonElement { ValueKind: JsonValueKind.String } e && e.GetString() == "gdb");
        Assert.Equal(2, _f.Navigation.Opened.Count);
        Assert.Equal(_remote.SettingsUri, _f.Navigation.Opened[1].Uri);
    }
}

/// <summary>The commands: VS Code's ids, shown only while connected (<c>remoteName</c>), the Settings page on its Remote tab, the host's file.</summary>
public class RemoteSettingsCommandTests : IDisposable
{
    private readonly VmFixture _f = new();
    private readonly List<IDisposable> _owned = [];
    private readonly FakeRemoteMachineSettings _remote;
    private readonly List<string> _shown = new();

    public RemoteSettingsCommandTests() => _remote = new FakeRemoteMachineSettings(_f.Harness.Dir.Combine("remote-data", "machine", "settings.json"));

    public void Dispose()
    {
        foreach (var d in _owned) d.Dispose();
        _f.Dispose();
    }

    private sealed class Views(List<string> shown) : IViewActivator
    {
        public ValueTask<bool> ShowViewAsync(string viewId, CancellationToken ct = default) { shown.Add(viewId); return ValueTask.FromResult(true); }
    }

    private SearchPanelsHost Host(bool remote = true)
    {
        var s = new ServiceCollection();
        s.AddSingleton(_f.Harness.Workspace);
        s.AddSingleton<IWorkspaceService>(_f.Harness.Workspace);
        s.AddSingleton<IDocumentManager>(_f.Harness.Documents);
        s.AddSingleton(_f.Configuration);
        s.AddSingleton<INavigationService>(_f.Navigation);
        s.AddSingleton<IWindowService>(_f.Windows);
        s.AddSingleton<ICommandService>(_f.Commands);
        s.AddSingleton<IContextKeyService>(_f.ContextKeys);
        s.AddSingleton<IViewActivator>(new Views(_shown));
        s.AddSingleton<CsCode.Ui.Threading.IUiDispatcher>(_f.Ui);
        if (remote) s.AddSingleton<IRemoteMachineSettings>(_remote);
        s.AddCsCodeSearchPanels();
        var sp = s.BuildServiceProvider();
        _owned.Add(sp);
        return sp.GetRequiredService<SearchPanelsHost>();
    }

    [Fact]
    public void The_commands_have_vs_code_s_ids_and_are_enabled_only_while_a_remote_host_is_connected()
    {
        Host();
        var open = _f.Commands.All.Single(c => c.Id == "workbench.action.openRemoteSettings");
        var json = _f.Commands.All.Single(c => c.Id == "workbench.action.openRemoteSettingsFile");
        Assert.Equal(("Preferences", "Open Remote Settings", "remoteName"), (open.Category, open.Title, open.Enablement));
        Assert.Equal(("Preferences", "Open Remote Settings (JSON)", "remoteName"), (json.Category, json.Title, json.Enablement));
    }

    [Fact]
    public async Task Open_remote_settings_shows_the_settings_page_on_its_remote_tab()
    {
        var host = Host();
        await _f.Commands.ExecuteAsync(SearchPanelsHost.OpenRemoteSettings, "tabSize"); // no page yet: the request waits for it
        Assert.Equal(["settings"], _shown);
        var vm = host.CreateSettingsViewModel();
        _owned.Add(vm);
        Assert.Equal(SettingsScope.Remote, vm.SelectedScope);
        Assert.Equal("tabSize", vm.SearchText);

        vm.SelectedScope = SettingsScope.User;
        await _f.Commands.ExecuteAsync(SearchPanelsHost.OpenRemoteSettings); // the open page switches
        Assert.Equal(SettingsScope.Remote, vm.SelectedScope);
    }

    [Fact]
    public async Task Open_remote_settings_json_opens_the_host_s_machine_settings_file()
    {
        Host();
        await _f.Commands.ExecuteAsync(SearchPanelsHost.OpenRemoteSettingsJson);
        var (opened, _) = Assert.Single(_f.Navigation.Opened);
        Assert.Equal(_remote.SettingsUri, opened);
        Assert.Equal("{\n}\n", await File.ReadAllTextAsync(_remote.Path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Without_a_connected_host_the_commands_say_so_and_open_nothing()
    {
        Host();
        _remote.SetConnected(false);
        await _f.Commands.ExecuteAsync(SearchPanelsHost.OpenRemoteSettings);
        await _f.Commands.ExecuteAsync(SearchPanelsHost.OpenRemoteSettingsJson);
        Assert.Empty(_shown);
        Assert.Empty(_f.Navigation.Opened);
        Assert.Equal(2, _f.Windows.Messages.Count(m => m.Message.StartsWith("No remote host is connected", StringComparison.Ordinal)));
    }
}
