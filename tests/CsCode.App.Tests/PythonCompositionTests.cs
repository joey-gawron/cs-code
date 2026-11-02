using System.Text.Json;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using CsCode.App.Services;
using CsCode.Core.Debugging;
using CsCode.Core.Languages;
using CsCode.Core.Platform;
using CsCode.Core.Python;
using CsCode.Core.Text;
using CsCode.Dap.Adapters;
using CsCode.Editor;
using CsCode.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CsCode.App.Tests;

/// <summary>
/// Language stage 1's "Done when", over the REAL composition root (every module, CSCODE_HOME in a temp dir) with the real tools: a project with a
/// <c>.venv</c> gets that interpreter; basedpyright (Python's default type checker) and Ruff both report on its file; a save runs Ruff's fix-all and organize imports and then Ruff's
/// formatting (<c>editor.defaultFormatter</c>, from the folder's .vscode/settings.json); debugpy, started from the debugger's "Current File"
/// configuration, stops at a breakpoint with the .venv's interpreter. The project's <c>.venv</c> is the tool venv of tools/fetch-debugpy.sh and
/// tools/fetch-lsp-tools.sh --with-python (it has debugpy, pytest and ruff); basedpyright comes from the tools directory. Skips without them.
/// </summary>
[Trait("Category", "RealTools")]
public class PythonCompositionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string RepoTools()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "CsCode.slnx"))) return Path.Combine(d.FullName, ".scratch", "tools");
        return "";
    }

    private static async Task Until(Func<bool> condition, string what, int seconds = 90)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("timed out waiting for: " + what);
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(50, Ct);
        }
    }

    [AvaloniaFact(Timeout = 240_000)]
    public async Task A_dot_venv_project_gets_its_interpreter_both_servers_save_actions_and_debugpy()
    {
        var tools = RepoTools();
        var venv = Path.Combine(tools, "python", "venv");
        if (OperatingSystem.IsWindows() || !File.Exists(Path.Combine(venv, "bin", "ruff")) || !File.Exists(Path.Combine(tools, "lsp", "node_modules", ".bin", "basedpyright-langserver")))
        {
            Assert.Skip("the Python tools are missing: run tools/fetch-lsp-tools.sh --with-python and tools/fetch-debugpy.sh");
            return;
        }
        using var home = new TempDirectory("cscode-python-e2e-");
        var previousHome = Environment.GetEnvironmentVariable("CSCODE_HOME");
        Environment.SetEnvironmentVariable("CSCODE_HOME", home.Path);
        try
        {
            Directory.CreateDirectory(Path.Combine(home.Path, "data", "tools"));
            Directory.CreateSymbolicLink(Path.Combine(home.Path, "data", "tools", "lsp"), Path.Combine(tools, "lsp")); // basedpyright, as an install would put it
            var project = Path.Combine(home.Path, "proj");
            Directory.CreateDirectory(project);
            Directory.CreateSymbolicLink(Path.Combine(project, ".venv"), venv);
            home.WriteFile("proj/pyproject.toml", "[project]\nname = \"proj\"\nversion = \"0\"\n");
            home.WriteFile("proj/.vscode/settings.json", """
                { "[python]": { "editor.defaultFormatter": "ruff", "editor.formatOnSave": true,
                  "editor.codeActionsOnSave": { "source.fixAll": "explicit", "source.organizeImports": "explicit" } } }
                """);
            var file = home.WriteFile("proj/calc.py", "import sys\nimport os\nimport json\n\n\ndef total(values: list[int]) -> int:\n    return sum(values)\n\n\ncount: int = \"oops\"\nprint(f\"total\", total([1, 2]), sys.argv, os.sep)\n");

            await using var sp = AppServices.Build([]);
            await sp.GetRequiredService<IWorkspaceService>().OpenFolderAsync(new Uri(project + "/"), Ct);
            _ = sp.GetRequiredService<ILanguageServerManager>(); // starts observing documents, as the shell does at startup

            // 1. the folder's .venv is its interpreter
            var python = await sp.GetRequiredService<IPythonEnvironmentService>().GetActiveAsync(new Uri(file), Ct);
            Assert.NotNull(python);
            Assert.Equal((PythonEnvironmentKind.Venv, ".venv"), (python.Kind, python.Name));
            Assert.Equal(Path.Combine(project, ".venv", "bin", "python"), python.Executable);

            // 2. basedpyright and Ruff both report on the file
            var factory = sp.GetRequiredService<IEditorViewFactory>();
            var doc = await factory.OpenDocumentAsync(new Uri(file));
            var diagnostics = sp.GetRequiredService<IDiagnosticService>();
            await Until(() => diagnostics.Get(doc.Uri).Any(d => d.Code == "F401"), "Ruff's unused import");
            await Until(() => diagnostics.Get(doc.Uri).Any(d => d.Message.Contains("\"int\"", StringComparison.Ordinal) && d.Message.Contains("oops", StringComparison.Ordinal)), "basedpyright's type error");
            var servers = sp.GetRequiredService<ILanguageServerManager>().Servers;
            Assert.Contains(servers, s => s.Id == "ruff" && s.State == LanguageServerState.Running);
            Assert.Contains(servers, s => s.Id == "basedpyright" && s.State == LanguageServerState.Running);
            Assert.DoesNotContain(servers, s => s.Id == "python"); // Pyright only when chosen

            // 3. the shell's save: code actions on save (fix all, organize imports), then Ruff's format on save
            await Until(() => sp.GetRequiredService<ILanguageFeatureRegistry>().GetProviders<IDocumentFormattingProvider>(doc).Any(p => LanguageProviderSource.IdOf(p) == "ruff"), "Ruff's formatter registered");
            doc.ApplyEdits([TextEdit.Insert(doc.PositionAt(doc.Length), "y=[1,2]\n")]);
            await sp.GetRequiredService<FileParticipants>().WillSaveAsync(doc, DocumentSaveReason.Manual, Ct);
            await doc.SaveAsync(Ct);
            var saved = await File.ReadAllTextAsync(file, Ct);
            Assert.True(saved.StartsWith("import os\nimport sys\n\n", StringComparison.Ordinal), "fix all removed json, organize imports sorted os before sys:\n" + saved);
            Assert.Contains("print(\"total\", total([1, 2]), sys.argv, os.sep)", saved, StringComparison.Ordinal); // fix all: the f-string without placeholders
            Assert.Contains("y = [1, 2]\n", saved, StringComparison.Ordinal); // Ruff's format on save

            // 4. debugpy: the "Current File" configuration with the .venv's interpreter stops at a breakpoint
            var debug = sp.GetRequiredService<IDebugService>();
            var current = sp.GetRequiredService<DebugpyAdapterFactory>().ForCurrentFile(new Uri(file), "python", new Uri(project + "/"))!;
            var printLine = saved.Split('\n').ToList().FindIndex(l => l.StartsWith("print(", StringComparison.Ordinal));
            debug.Breakpoints.Add(new Uri(file), printLine);
            var session = await debug.StartAsync(current with { Properties = new Dictionary<string, JsonElement>(current.Properties) { ["console"] = JsonSerializer.SerializeToElement("internalConsole") } }, Ct);
            Assert.NotNull(session);
            Assert.Equal(python.Executable, session.Configuration.GetString("python"));
            await Until(() => debug.StopLocation is { } l && l.Line == printLine, "the breakpoint on the print line");
            await session.StopAsync(Ct);
            await Until(() => debug.Sessions.Count == 0, "the session ended", 30);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CSCODE_HOME", previousHome);
        }
    }
}
