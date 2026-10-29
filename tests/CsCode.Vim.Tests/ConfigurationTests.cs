using System.Text.Json;
using CsCode.Core.Platform;
using CsCode.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CsCode.Vim.Tests;

/// <summary>Dictionary-backed <see cref="IStateStorage"/> that round-trips values through JSON like the real one.</summary>
public sealed class InMemoryStateStorage : IStateStorage
{
    private readonly Dictionary<string, string> _values = new();
    public int Writes { get; private set; }

    public T? Get<T>(string key, StorageScope scope = StorageScope.Application) =>
        _values.TryGetValue(key, out var json) ? JsonSerializer.Deserialize<T>(json, CoreJson.Options) : default;

    public void Set<T>(string key, T? value, StorageScope scope = StorageScope.Application)
    {
        Writes++;
        _values[key] = JsonSerializer.Serialize(value, CoreJson.Options);
    }

    public void Remove(string key, StorageScope scope = StorageScope.Application) => _values.Remove(key);
    public Task FlushAsync() => Task.CompletedTask;
}

public class ConfigurationTests
{
    private static (VimSession Session, TestConfigurationService Config, VimConfiguration Vim) Create(IStateStorage? state = null, IAppPaths? paths = null)
    {
        var session = new VimSession();
        var config = new TestConfigurationService();
        var vim = new VimConfiguration(session, config, state, paths);
        return (session, config, vim);
    }

    private static (VimEngine Engine, InMemoryVimHost Host) NewEngine(VimSession session, string text)
    {
        var host = new InMemoryVimHost(text);
        return (new VimEngine(host, session), host);
    }

    [Fact]
    public void Registers_every_documented_property_with_a_default()
    {
        var (_, config, _) = Create();
        var keys = config.Properties.Select(p => p.Key).ToHashSet();
        foreach (var k in new[]
        {
            "vim.enabled", "vim.useSystemClipboard", "vim.leader", "vim.hlsearch", "vim.incsearch", "vim.ignorecase", "vim.smartcase", "vim.wrapscan",
            "vim.startInInsertMode", "vim.timeout", "vim.handleKeys", "vim.normalModeKeyBindings", "vim.normalModeKeyBindingsNonRecursive",
            "vim.insertModeKeyBindings", "vim.insertModeKeyBindingsNonRecursive", "vim.visualModeKeyBindings", "vim.visualModeKeyBindingsNonRecursive",
            "vim.operatorPendingModeKeyBindings", "vim.operatorPendingModeKeyBindingsNonRecursive", "vim.commandLineModeKeyBindings",
            "vim.commandLineModeKeyBindingsNonRecursive", "vim.cursorStylePerMode.normal", "vim.cursorStylePerMode.insert", "vim.autoSwitchInputMethod",
            "vim.persistRegisters", "vim.surround", "vim.commentary", "vim.sneak", "vim.easymotion", "vim.camelCaseMotion", "vim.argumentObjects",
            "vim.replaceWithRegister", "vim.highlightedyank", "vim.targets",
        })
            Assert.True(keys.Contains(k), "missing property " + k);
        Assert.All(config.Properties.Where(p => p.Key.StartsWith("vim.")), p => Assert.NotNull(p.Default));
    }

    [Fact]
    public void Vim_is_disabled_by_default_and_the_defaults_are_applied()
    {
        var (session, _, vim) = Create();
        Assert.False(vim.Enabled);
        Assert.False(session.Enabled);
        Assert.True(session.Options.IgnoreCase);
        Assert.True(session.Options.SmartCase);
        Assert.True(session.Options.IncSearch);
        Assert.True(session.Options.WrapScan);
        Assert.False(session.Options.HlSearch);
        Assert.False(session.UseSystemClipboard);
        Assert.Equal(1000, session.Options.TimeoutLen);
        Assert.Equal("\\", session.Leader);
        Assert.True(session.Plugins.Surround);
        Assert.True(session.Plugins.Commentary);
        Assert.False(session.Plugins.Sneak);
    }

    [Fact]
    public void Settings_changes_apply_live_and_only_touch_what_changed()
    {
        var (session, config, vim) = Create();
        config.Set("vim.enabled", true);
        Assert.True(vim.Enabled);
        config.Set("vim.hlsearch", true);
        Assert.True(session.Options.HlSearch);
        config.Set("vim.useSystemClipboard", true);
        Assert.True(session.UseSystemClipboard);
        config.Set("vim.timeout", 250);
        Assert.Equal(250, session.Options.TimeoutLen);
        // a runtime :set survives an unrelated settings change
        session.Options.ScrollOff = 7;
        session.Options.IgnoreCase = false;
        config.Set("vim.wrapscan", false);
        Assert.False(session.Options.WrapScan);
        Assert.False(session.Options.IgnoreCase);
        Assert.Equal(7, session.Options.ScrollOff);
        config.Set("vim.ignorecase", true);
        Assert.True(session.Options.IgnoreCase);
        // non-vim keys are ignored
        config.Set("editor.fontSize", 14);
        Assert.False(session.Options.WrapScan);
    }

    [Fact]
    public void Plugin_flags_accept_booleans_and_the_enable_object_form()
    {
        var (session, config, _) = Create();
        config.Set("vim.surround", false);
        Assert.False(session.Plugins.Surround);
        config.Set("vim.targets", new { enable = true });
        Assert.True(session.Plugins.Targets);
        config.Set("vim.camelCaseMotion", new { enable = true });
        Assert.True(session.Plugins.CamelCaseMotion);
        config.Set("vim.sneak", true);
        Assert.True(session.Plugins.Sneak);
        config.Set("vim.commentary", false);
        Assert.False(session.Plugins.Commentary);
    }

    [Fact]
    public void Disabled_plugins_are_not_commands()
    {
        var (session, config, _) = Create();
        config.Set("vim.enabled", true);
        config.Set("vim.surround", false);
        config.Set("vim.commentary", false);
        var (engine, host) = NewEngine(session, "(a b)\nfoo");
        engine.TypeKeys("ds)");
        Assert.Equal("(a b)", host.Buffer.GetLine(0));
        engine.TypeKeys("jgcc<Esc>");
        Assert.Equal("foo", host.Buffer.GetLine(1));
        config.Set("vim.surround", true);
        config.Set("vim.commentary", true);
        engine.TypeKeys("kds)");
        Assert.Equal("a b", host.Buffer.GetLine(0));
        engine.TypeKeys("jgcc");
        Assert.Equal("// foo", host.Buffer.GetLine(1));
    }

    [Fact]
    public void Key_bindings_with_after_keys_work_in_every_mode()
    {
        var (session, config, _) = Create();
        config.Set("vim.enabled", true);
        config.Set("vim.leader", " ");
        config.Set("vim.normalModeKeyBindingsNonRecursive", new[]
        {
            new { before = new[] { "<leader>", "w" }, after = new[] { "d", "d" } },
            new { before = new[] { "Y" }, after = new[] { "y", "$" } },
        });
        config.Set("vim.insertModeKeyBindings", new[] { new { before = new[] { "j", "j" }, after = new[] { "<Esc>" } } });
        config.Set("vim.visualModeKeyBindingsNonRecursive", new[] { new { before = new[] { ">" }, after = new[] { ">", "g", "v" } } });
        var (engine, host) = NewEngine(session, "one\ntwo\nthree");
        engine.TypeKeys(" w");
        Assert.Equal(["two", "three"], Lines(host));
        engine.TypeKeys("Yjp");
        Assert.Equal("two", host.Buffer.GetLine(0));
        Assert.Equal("ttwohree", host.Buffer.GetLine(1));
        engine.TypeKeys("Ahi");
        engine.TypeKeys("jj");
        Assert.Equal(VimMode.Normal, engine.Mode);
        Assert.Equal("ttwohreehi", host.Buffer.GetLine(1));
        engine.TypeKeys("ggVj>");
        Assert.StartsWith("\t", host.Buffer.GetLine(0));
        Assert.Equal(VimMode.VisualLine, engine.Mode);
    }

    [Fact]
    public void Recursive_and_non_recursive_bindings_differ()
    {
        var (session, config, _) = Create();
        config.Set("vim.enabled", true);
        config.Set("vim.normalModeKeyBindings", new[] { new { before = new[] { "a" }, after = new[] { "b" } }, new { before = new[] { "b" }, after = new[] { "x" } } });
        config.Set("vim.normalModeKeyBindingsNonRecursive", new[] { new { before = new[] { "c" }, after = new[] { "b" } } });
        var (engine, host) = NewEngine(session, "abc\ndef");
        engine.TypeKeys("a");   // a -> b -> x
        Assert.Equal("bc", host.Buffer.GetLine(0));
        engine.TypeKeys("c");   // c -> b (not remapped): b moves back a word
        Assert.Equal("bc", host.Buffer.GetLine(0));
    }

    [Fact]
    public void Key_bindings_can_run_application_commands_and_ex_commands()
    {
        var (session, config, _) = Create();
        config.Set("vim.enabled", true);
        config.Set("vim.normalModeKeyBindingsNonRecursive", JsonDocument.Parse("""
            [
              { "before": ["g", "h"], "commands": [ { "command": "editor.action.showHover", "args": [1, "two"] } ] },
              { "before": ["<C-n>"], "commands": [ ":nohl" ] },
              { "before": ["<leader>", "d"], "commands": [ "editor.action.revealDefinition" ], "after": ["j"] }
            ]
            """).RootElement.Clone());
        var (engine, host) = NewEngine(session, "a\nb");
        engine.TypeKeys("gh");
        Assert.Equal("editor.action.showHover", host.Commands[^1].Id);
        Assert.Equal(2, host.Commands[^1].Args!.Count);
        engine.TypeKeys("\\d");
        Assert.Equal("editor.action.revealDefinition", host.Commands[^1].Id);
        Assert.Equal(1, engine.Cursor.Line);
        engine.TypeKeys("<C-n>");
        Assert.Null(engine.LastError);
    }

    [Fact]
    public void Changing_bindings_replaces_only_settings_mappings()
    {
        var (session, config, vim) = Create();
        config.Set("vim.enabled", true);
        vim.RunScript("nnoremap Q dd", "vimrc");
        config.Set("vim.normalModeKeyBindingsNonRecursive", new[] { new { before = new[] { "Z" }, after = new[] { "x" } } });
        Assert.Contains(session.Mappings.Items, m => m.LhsText == "Z");
        config.Set("vim.normalModeKeyBindingsNonRecursive", new[] { new { before = new[] { "W" }, after = new[] { "x" } } });
        Assert.DoesNotContain(session.Mappings.Items, m => m.LhsText == "Z");
        Assert.Contains(session.Mappings.Items, m => m.LhsText == "W");
        Assert.Contains(session.Mappings.Items, m => m.Origin == "vimrc");
    }

    [Fact]
    public void Malformed_bindings_are_ignored_not_fatal()
    {
        var (session, config, _) = Create();
        config.Set("vim.normalModeKeyBindings", JsonDocument.Parse("""[ 5, { "after": ["x"] }, { "before": [] }, { "before": "zz", "after": "x" }, "bad" ]""").RootElement.Clone());
        Assert.Single(session.Mappings.Items, m => m.Origin == "settings");
    }

    [Fact]
    public void Handle_keys_false_passes_the_key_to_the_editor()
    {
        var (session, config, _) = Create();
        config.Set("vim.enabled", true);
        config.Set("vim.handleKeys", new Dictionary<string, bool> { ["<C-c>"] = false, ["<C-f>"] = false });
        var (engine, _) = NewEngine(session, "abc");
        Assert.Equal(VimKeyResult.PassThrough, engine.HandleKey(3));
        Assert.Equal(VimKeyResult.PassThrough, engine.HandleKey(6));
        Assert.Equal(VimKeyResult.Handled, engine.HandleKey(4));  // Ctrl-D is still Vim's
        config.Set("vim.handleKeys", new Dictionary<string, bool>());
        Assert.Equal(VimKeyResult.Handled, engine.HandleKey(3));
    }

    [Fact]
    public void Cursor_style_per_mode_reaches_the_host()
    {
        var (session, config, _) = Create();
        config.Set("vim.enabled", true);
        config.Set("vim.cursorStylePerMode.insert", "block-outline");
        config.Set("vim.cursorStylePerMode.normal", "underline");
        config.Set("vim.cursorStylePerMode.visual", "nonsense");
        var (engine, host) = NewEngine(session, "abc");
        engine.TypeKeys("i");
        Assert.Equal(VimCursorShape.BlockOutline, host.ModeInfo.Shape);
        engine.TypeKeys("<Esc>");
        Assert.Equal(VimCursorShape.Underline, host.ModeInfo.Shape);
        engine.TypeKeys("v");
        Assert.Equal(VimCursorShape.Block, host.ModeInfo.Shape);   // invalid value falls back to the default
    }

    [Fact]
    public void Start_in_insert_mode_applies_to_new_engines()
    {
        var (session, config, _) = Create();
        config.Set("vim.enabled", true);
        config.Set("vim.startInInsertMode", true);
        var (engine, host) = NewEngine(session, "abc");
        Assert.Equal(VimMode.Insert, engine.Mode);
        engine.TypeKeys("X<Esc>");
        Assert.Equal("Xabc", host.Buffer.GetLine(0));
    }

    [Fact]
    public void Timeout_setting_drives_ambiguous_mappings()
    {
        var (session, config, _) = Create();
        config.Set("vim.enabled", true);
        config.Set("vim.timeout", 300);
        config.Set("vim.normalModeKeyBindingsNonRecursive", new[]
        {
            new { before = new[] { "g", "x" }, after = new[] { "x" } },
            new { before = new[] { "g" }, after = new[] { "$" } },
        });
        var (engine, host) = NewEngine(session, "abcd");
        engine.TypeKeys("g");
        Assert.Equal(0, engine.Cursor.Character);
        host.ManualTimer!.Advance(TimeSpan.FromMilliseconds(299));
        Assert.Equal(0, engine.Cursor.Character);
        host.ManualTimer.Advance(TimeSpan.FromMilliseconds(2));
        Assert.Equal(3, engine.Cursor.Character);
    }

    // ------------------------------------------------------------------------------------------------------------
    // vimrc
    // ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Vimrc_is_loaded_from_the_user_data_dir_and_errors_are_reported_not_fatal()
    {
        using var paths = new TestAppPaths();
        File.WriteAllText(Path.Combine(paths.UserDataDir, "vimrc"), string.Join('\n',
            "\" a comment",
            "set shiftwidth=2 expandtab",
            "let mapleader=\",\"",
            "nnoremap <leader>x dd",
            "frobnicate",
            "nmap Q @q",
            "set nosuchoption",
            "inoremap jk <Esc>"));
        var (session, config, vim) = Create(null, paths);
        config.Set("vim.enabled", true);
        var errors = await vim.ReloadVimrcAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, session.Options.ShiftWidth);
        Assert.True(session.Options.ExpandTab);
        Assert.Equal(2, errors.Count);
        Assert.StartsWith("line 5:", errors[0]);
        Assert.Contains("E492", errors[0]);
        Assert.StartsWith("line 7:", errors[1]);
        Assert.Equal(errors, vim.VimrcErrors);
        var (engine, host) = NewEngine(session, "one\ntwo");
        engine.TypeKeys(",x");
        Assert.Equal(["two"], Lines(host));
        engine.TypeKeys("Aab");
        engine.TypeKeys("jk");
        Assert.Equal(VimMode.Normal, engine.Mode);
        Assert.Equal("twoab", host.Buffer.GetLine(0));
    }

    [Fact]
    public async Task Vimrc_reload_replaces_vimrc_mappings_and_respects_the_enable_setting()
    {
        using var paths = new TestAppPaths();
        var path = Path.Combine(paths.UserDataDir, "vimrc");
        File.WriteAllText(path, "nnoremap A dd\nnnoremap B x\n");
        var (session, config, vim) = Create(null, paths);
        await vim.ReloadVimrcAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, session.Mappings.Items.Count(m => m.Origin == "vimrc"));
        File.WriteAllText(path, "nnoremap C dw\n");
        await vim.ReloadVimrcAsync(TestContext.Current.CancellationToken);
        Assert.Equal(["C"], session.Mappings.Items.Where(m => m.Origin == "vimrc").Select(m => m.LhsText).ToArray());
        config.Set("vim.vimrc.enable", false);
        await vim.ReloadVimrcAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain(session.Mappings.Items, m => m.Origin == "vimrc");
    }

    [Fact]
    public async Task Vimrc_path_setting_overrides_the_default_location()
    {
        using var paths = new TestAppPaths();
        var custom = Path.Combine(paths.CacheDir, "my.vim");
        File.WriteAllText(custom, "set tabstop=3\n");
        var (session, config, vim) = Create(null, paths);
        config.Set("vim.vimrc.path", custom);
        await vim.ReloadVimrcAsync(TestContext.Current.CancellationToken);
        Assert.Equal(3, session.Options.TabStop);
        Assert.Equal(custom, vim.VimrcPath);
    }

    [Fact]
    public async Task Missing_vimrc_is_not_an_error()
    {
        using var paths = new TestAppPaths();
        var (_, _, vim) = Create(null, paths);
        var errors = await vim.ReloadVimrcAsync(TestContext.Current.CancellationToken);
        Assert.Empty(errors);
    }

    [Fact]
    public void Source_command_runs_a_script_through_the_session_reader()
    {
        var session = new VimSession { ReadFile = p => p == "/x.vim" ? "set sw=3\nnnoremap Q dd\n" : null };
        var (engine, host) = NewEngine(session, "a\nb");
        Assert.True(engine.ExecuteEx("source /x.vim"));
        Assert.Equal(3, session.Options.ShiftWidth);
        engine.TypeKeys("Q");
        Assert.Equal("b", host.Buffer.GetLine(0));
        Assert.False(engine.ExecuteEx("source /missing.vim"));
        Assert.Contains("E484", engine.LastError);
    }

    // ------------------------------------------------------------------------------------------------------------
    // registers persistence
    // ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Named_registers_and_macros_persist_across_sessions()
    {
        var state = new InMemoryStateStorage();
        var (session, config, _) = Create(state);
        config.Set("vim.enabled", true);
        var (engine, host) = NewEngine(session, "foo\nbar");
        engine.TypeKeys("qaA!<Esc>jq");
        engine.TypeKeys("\"byy");
        engine.TypeKeys("yy");
        Assert.True(state.Writes >= 2);

        var (session2, config2, _) = Create(state);
        config2.Set("vim.enabled", true);
        var a = session2.Registers.ForRead('a')!;
        Assert.Equal("A!\u001bj", string.Join("\n", a.Lines));
        Assert.Equal("bar", string.Join("\n", session2.Registers.ForRead('b')!.Lines));
        Assert.Equal(VimRegisterType.Line, session2.Registers.ForRead('b')!.Type);
        // the unnamed register and the numbered ones are not persisted
        Assert.True(session2.Registers.ForRead('0') is null or { IsEmpty: true });
        var (engine2, host2) = NewEngine(session2, "x\ny");
        engine2.TypeKeys("@a");
        Assert.Equal("x!", host2.Buffer.GetLine(0));
        Assert.Equal(1, engine2.Cursor.Line);
        _ = host;
    }

    [Fact]
    public void Persistence_can_be_switched_off_and_a_corrupt_state_is_ignored()
    {
        var state = new InMemoryStateStorage();
        state.Set("vim.registers", "garbage");
        var (session, config, _) = Create(state);
        Assert.True(session.Registers.ForRead('a') is null or { IsEmpty: true });
        config.Set("vim.persistRegisters", false);
        config.Set("vim.enabled", true);
        var before = state.Writes;
        var (engine, _) = NewEngine(session, "foo");
        engine.TypeKeys("\"ayy");
        Assert.Equal(before, state.Writes);
    }

    [Fact]
    public void Persisted_registers_are_capped()
    {
        var state = new InMemoryStateStorage();
        var (session, config, _) = Create(state);
        config.Set("vim.enabled", true);
        var (engine, _) = NewEngine(session, new string('x', VimConfiguration.MaxPersistedRegisterChars + 10) + "\nsmall");
        engine.TypeKeys("\"ayy");
        engine.TypeKeys("j\"byy");
        var saved = state.Get<Dictionary<string, PersistedRegister>>(VimConfiguration.RegistersStateKey)!;
        Assert.False(saved.ContainsKey("a"));
        Assert.True(saved.ContainsKey("b"));
    }

    // ------------------------------------------------------------------------------------------------------------
    // service, commands and DI
    // ------------------------------------------------------------------------------------------------------------

    private static ServiceProvider BuildProvider(TestConfigurationService config, TestCommandService commands, InMemoryStateStorage? state = null, TestAppPaths? paths = null, InMemoryOutputService? output = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfigurationService>(config);
        services.AddSingleton<ICommandService>(commands);
        if (state is not null) services.AddSingleton<IStateStorage>(state);
        if (paths is not null) services.AddSingleton<IAppPaths>(paths);
        if (output is not null) services.AddSingleton<IOutputService>(output);
        services.AddCsCodeVim();
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Resolving_the_service_registers_commands_and_settings()
    {
        var config = new TestConfigurationService();
        var commands = new TestCommandService();
        var output = new InMemoryOutputService();
        using var provider = BuildProvider(config, commands, output: output);
        var vim = provider.GetRequiredService<IVimService>();
        Assert.Same(vim.Session, provider.GetRequiredService<VimSession>());
        Assert.Same(vim, provider.GetRequiredService<IVimService>());
        foreach (var id in new[] { "vim.toggle", "vim.enable", "vim.disable", "vim.showCheatSheet" })
            Assert.True(commands.Has(id), id);
        Assert.Contains(config.Properties, p => p.Key == "vim.enabled");
        await vim.VimrcLoaded;

        Assert.False(vim.Enabled);
        var changes = new List<bool>();
        using var sub = vim.EnabledChanged.Subscribe(changes.Add);
        await commands.ExecuteAsync("vim.toggle");
        Assert.True(vim.Enabled);
        await commands.ExecuteAsync("vim.disable");
        Assert.False(vim.Enabled);
        await commands.ExecuteAsync("vim.enable");
        await commands.ExecuteAsync("vim.enable");
        Assert.True(vim.Enabled);
        Assert.Equal([true, false, true], changes);

        await commands.ExecuteAsync("vim.showCheatSheet");
        Assert.Contains("Vim cheat sheet", output.GetOrCreate(VimService.CheatSheetChannel).GetText());
    }

    [Fact]
    public async Task Toggling_changes_whether_engines_handle_keys()
    {
        var config = new TestConfigurationService();
        var commands = new TestCommandService();
        using var provider = BuildProvider(config, commands);
        var vim = provider.GetRequiredService<IVimService>();
        var host = new InMemoryVimHost("abc");
        using var engine = vim.CreateEngine(host);
        Assert.Equal(VimKeyResult.PassThrough, engine.HandleKey('x'));
        await commands.ExecuteAsync("vim.enable");
        Assert.Equal(VimKeyResult.Handled, engine.HandleKey('x'));
        Assert.Equal("bc", host.Buffer.GetLine(0));
        await commands.ExecuteAsync("vim.toggle");
        Assert.Equal(VimKeyResult.PassThrough, engine.HandleKey('x'));
        Assert.Equal("bc", host.Buffer.GetLine(0));
    }

    [Fact]
    public void Enabling_through_the_setting_works_too()
    {
        var config = new TestConfigurationService();
        using var provider = BuildProvider(config, new TestCommandService());
        var vim = provider.GetRequiredService<IVimService>();
        config.Set("vim.enabled", true);
        Assert.True(vim.Enabled);
        config.Set("vim.enabled", false);
        Assert.False(vim.Enabled);
    }

    [Fact]
    public async Task The_service_loads_the_vimrc_and_the_saved_registers_on_activation()
    {
        using var paths = new TestAppPaths();
        File.WriteAllText(Path.Combine(paths.UserDataDir, "vimrc"), "nnoremap Q dd\n");
        var state = new InMemoryStateStorage();
        state.Set("vim.registers", new Dictionary<string, PersistedRegister> { ["q"] = new("c", "ix\u001b", 0) });
        var config = new TestConfigurationService();
        config.Set("vim.enabled", true);
        using var provider = BuildProvider(config, new TestCommandService(), state, paths);
        var vim = provider.GetRequiredService<IVimService>();
        await vim.VimrcLoaded;
        Assert.Empty(vim.VimrcErrors);
        var host = new InMemoryVimHost("one\ntwo");
        using var engine = vim.CreateEngine(host);
        engine.TypeKeys("Q");
        Assert.Equal("two", host.Buffer.GetLine(0));
        engine.TypeKeys("@q");
        Assert.Equal("xtwo", host.Buffer.GetLine(0));
    }

    [Fact]
    public void Disposing_the_container_detaches_everything()
    {
        var config = new TestConfigurationService();
        var commands = new TestCommandService();
        var provider = BuildProvider(config, commands);
        var vim = provider.GetRequiredService<IVimService>();
        var session = vim.Session;
        config.Set("vim.normalModeKeyBindingsNonRecursive", new[] { new { before = new[] { "Z" }, after = new[] { "x" } } });
        Assert.NotEmpty(session.Mappings.Items);
        provider.Dispose();
        Assert.DoesNotContain(session.Mappings.Items, m => m.Origin == "settings");
        Assert.False(commands.Has("vim.toggle"));
        config.Set("vim.hlsearch", true);
        Assert.False(session.Options.HlSearch);
    }

    private static string[] Lines(InMemoryVimHost host) => Enumerable.Range(0, host.Buffer.LineCount).Select(host.Buffer.GetLine).ToArray();
}
