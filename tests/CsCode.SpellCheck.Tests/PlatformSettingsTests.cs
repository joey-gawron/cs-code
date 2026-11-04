using System.Text.Json;
using CsCode.Core;
using CsCode.Core.Languages;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Platform;
using CsCode.SpellCheck.Settings;
using CsCode.TestDoubles;
using Microsoft.Extensions.DependencyInjection;

namespace CsCode.SpellCheck.Tests;

/// <summary>
/// The checker in a real composition: Platform's configuration service (settings files on disk, live reload), workspace, file system and
/// watcher, over a temp CSCODE_HOME and a temp project folder. What these prove: cspell's settings in every file are honoured and merged,
/// a project's cspell.json too, and "Add to ... dictionary" writes the right file.
/// </summary>
public sealed class PlatformSettingsTests : IAsyncLifetime
{
    private readonly TempDirectory _temp = new("cscode-spell-");
    private readonly FakeClassifier _classifier = new();
    private ServiceProvider? _provider;
    private IDisposable? _started;
    private readonly List<IDisposable> _registrations = [];

    private string Home => _temp.Combine("home");
    private string UserSettings => Path.Combine(Home, "config", "settings.json");
    private string Project => _temp.Combine("project");

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    private async Task<SpellCheckHost> StartAsync()
    {
        Directory.CreateDirectory(Project);
        var services = new ServiceCollection();
        var paths = new AppPaths(k => k == AppPaths.HomeEnvironmentVariable ? Home : null);
        services.AddSingleton<IAppPaths>(paths);
        services.AddSingleton(paths);
        services.AddCsCodeCore();
        services.AddCsCodePlatform();
        services.AddSingleton<ISyntaxClassificationService>(_classifier);
        services.AddSingleton<ILanguageService>(new FakeLanguages());
        services.AddCsCodeSpellCheck();
        _provider = services.BuildServiceProvider();
        await _provider.GetRequiredService<IWorkspaceService>().OpenFolderAsync(new Uri(Project + "/"));
        var config = _provider.GetRequiredService<IConfigurationService>();
        await config.UpdateAsync(SpellSettingsLoader.Delay, 0);
        var host = _provider.GetRequiredService<SpellCheckHost>();
        _started = host.Start();
        await host.Service.SettingsReady;
        await host.Service.ReloadSettingsAndWaitAsync();
        return host;
    }

    private InMemoryTextDocument Open(string text, string name = "a.cs", string language = "csharp")
    {
        var doc = new InMemoryTextDocument(new Uri(Path.Combine(Project, name)), language, text);
        _classifier.Set(doc.Uri, FakeClassification.SlashComments(text), notify: false);
        _registrations.Add(_provider!.GetRequiredService<IDocumentManager>().Register(doc));
        return doc;
    }

    private static async Task<SpellCheckResult> ResultAsync(SpellCheckHost host, ITextDocument doc, Func<SpellCheckResult, bool> until, int timeoutMs = 15_000)
    {
        var started = Environment.TickCount64;
        while (true)
        {
            if (host.Service.ResultFor(doc.Uri) is { } r && r.Version == doc.Version && until(r)) return r;
            if (Environment.TickCount64 - started > timeoutMs)
                throw new TimeoutException($"No matching result; last: {SpellHarness.Describe(host.Service.ResultFor(doc.Uri))}");
            await Task.Delay(20);
        }
    }

    private static string[] Words(SpellCheckResult r) => r.Issues.Select(i => i.Word).ToArray();

    private const string Text = "// alpha: zorblax quuxle frobnitz wibblet snarfle gronk\n";

    [Fact]
    public async Task Words_from_every_settings_file_and_the_cspell_config_are_merged()
    {
        _temp.WriteFile("home/config/settings.json", """{ "cSpell.words": ["zorblax"], "cSpell": { "userWords": ["snarfle"] } }""");
        _temp.WriteFile("project/.vscode/settings.json", """
            // cspell settings in the shape VS Code writes them
            { "cSpell.words": ["quuxle"], "cSpell.ignoreWords": ["wibblet"], }
            """);
        _temp.WriteFile("project/.cscode/settings.json", """{ "cSpell.words": ["frobnitz"] }""");
        _temp.WriteFile("project/cspell.json", """{ "version": "0.2", "words": ["gronk"], "dictionaries": ["project-words"], "dictionaryDefinitions": [{ "name": "project-words", "path": "./words.txt" }] }""");
        _temp.WriteFile("project/words.txt", "# project words\nalpha\n");
        var host = await StartAsync();
        var doc = Open(Text + "// one more: ploogle\n");
        var result = await ResultAsync(host, doc, r => r.State == SpellDocumentState.Checked);
        Assert.Equal(["ploogle"], Words(result));
        Assert.Equal([Path.Combine(Project, "cspell.json")], host.Service.Settings.ProjectConfigFiles);
    }

    [Fact]
    public async Task The_cspell_config_ignore_paths_patterns_and_flag_words_apply()
    {
        _temp.WriteFile("project/.cspell.json", """
            {
              "ignorePaths": ["generated/**"],
              "ignoreRegExpList": ["/TICKET-\\w+/g"],
              "flagWords": ["hte->the"],
              "minWordLength": 5
            }
            """);
        var host = await StartAsync();
        var ignored = Open("// zorblax\n", "generated/a.cs");
        var doc = Open("// see TICKET-ZORBLAX and hte word quuxle\n");
        Assert.Equal(SpellDocumentState.IgnoredPath, (await ResultAsync(host, ignored, _ => true)).State);
        var result = await ResultAsync(host, doc, r => r.Issues.Count > 0);
        Assert.Equal(["quuxle"], Words(result)); // hte is shorter than minWordLength 5, ...
        await host.Service.ReloadSettingsAndWaitAsync();
        Assert.Equal(["the"], host.Service.Settings.FlagWords["hte"]);
    }

    [Fact]
    public async Task A_yaml_config_is_reported_not_read()
    {
        _temp.WriteFile("project/cspell.yaml", "words:\n  - zorblax\n");
        var host = await StartAsync();
        Assert.Contains(host.Service.Settings.Problems, p => p.Contains("cspell.yaml", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Add_to_user_dictionary_writes_the_user_settings_and_keeps_the_workspace_list_apart()
    {
        _temp.WriteFile("home/config/settings.json", """
            {
                // mine
                "editor.fontSize": 13,
                "cSpell.words": ["zorblax"]
            }
            """);
        _temp.WriteFile("project/.vscode/settings.json", """{ "cSpell.words": ["quuxle"] }""");
        var host = await StartAsync();
        var doc = Open("// zorblax quuxle frobnitz\n");
        Assert.Equal(["frobnitz"], Words(await ResultAsync(host, doc, r => r.Issues.Count == 1)));

        await _provider!.GetRequiredService<ICommandService>().ExecuteAsync(SpellCheckHost.AddWordToUserDictionary, "frobnitz");
        await ResultAsync(host, doc, r => r.Issues.Count == 0);
        Assert.Empty(_provider!.GetRequiredService<IDiagnosticService>().Get(doc.Uri));

        var user = File.ReadAllText(UserSettings);
        Assert.Contains("// mine", user, StringComparison.Ordinal); // edited in place
        using var json = JsonDocument.Parse(user, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
        Assert.Equal(["zorblax", "frobnitz"], json.RootElement.GetProperty("cSpell.words").EnumerateArray().Select(e => e.GetString()));
        Assert.DoesNotContain("frobnitz", File.ReadAllText(Path.Combine(Project, ".vscode", "settings.json")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Add_to_workspace_dictionary_writes_the_cspell_config_when_there_is_one()
    {
        _temp.WriteFile("project/cspell.json", """
            {
                "version": "0.2",
                // project words
                "words": [
                    "zorblax"
                ]
            }
            """);
        var host = await StartAsync();
        var doc = Open("// zorblax quuxle\n");
        await ResultAsync(host, doc, r => r.Issues.Count == 1);
        await _provider!.GetRequiredService<ICommandService>().ExecuteAsync(SpellCheckHost.AddWordToWorkspaceDictionary, "quuxle");
        await ResultAsync(host, doc, r => r.Issues.Count == 0);
        Assert.Equal("""
            {
                "version": "0.2",
                // project words
                "words": [
                    "zorblax",
                    "quuxle"
                ]
            }
            """, File.ReadAllText(Path.Combine(Project, "cspell.json")));
        Assert.False(File.Exists(Path.Combine(Project, ".cscode", "settings.json")));
    }

    [Fact]
    public async Task Add_to_workspace_dictionary_writes_the_workspace_settings_without_a_cspell_config()
    {
        var host = await StartAsync();
        var doc = Open("// zorblax\n");
        await ResultAsync(host, doc, r => r.Issues.Count == 1);
        await _provider!.GetRequiredService<ICommandService>().ExecuteAsync(SpellCheckHost.AddWordToWorkspaceDictionary, "zorblax");
        await ResultAsync(host, doc, r => r.Issues.Count == 0);
        var written = SpellSettingsLoader.ParseJsonc(File.ReadAllText(Path.Combine(Project, ".cscode", "settings.json")))!.Value;
        Assert.Equal(["zorblax"], SpellSettingsLoader.StringList(SpellSettingsLoader.Find(written, SpellSettingsLoader.Words)));
    }

    [Fact]
    public async Task Editing_the_cspell_config_on_disk_reloads_it()
    {
        _temp.WriteFile("project/cspell.json", """{ "words": [] }""");
        var host = await StartAsync();
        var doc = Open("// zorblax\n");
        await ResultAsync(host, doc, r => r.Issues.Count == 1);
        File.WriteAllText(Path.Combine(Project, "cspell.json"), """{ "words": ["zorblax"] }""");
        await ResultAsync(host, doc, r => r.Issues.Count == 0, timeoutMs: 20_000);
    }

    [Fact]
    public async Task Dictionaries_dropped_into_the_user_folder_are_chosen_by_cSpell_language()
    {
        _temp.WriteFile("home/config/settings.json", """{ "cSpell.language": "en,xx" }""");
        var host = await StartAsync();
        var doc = Open("// zorblax receive\n");
        Assert.Equal(["zorblax"], Words(await ResultAsync(host, doc, r => r.Issues.Count == 1)));
        Assert.Equal(["xx"], (await host.Service.GetDictionariesAsync()).Missing);
        _temp.WriteFile("home/data/dictionaries/xx_YY.aff", "SET UTF-8\n");
        _temp.WriteFile("home/data/dictionaries/xx_YY.dic", "1\nzorblax\n");
        await ResultAsync(host, doc, r => r.Issues.Count == 0, timeoutMs: 20_000); // the folder is watched
        Assert.Equal(["en_US", "xx_YY"], (await host.Service.GetDictionariesAsync()).Sources.Select(s => s.Name));
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var r in _registrations) r.Dispose();
        _started?.Dispose();
        if (_provider is not null) await _provider.DisposeAsync();
        _temp.Dispose();
    }
}
