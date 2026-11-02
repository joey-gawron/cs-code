using CsCode.Core.Languages;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Terminal;
using CsCode.Core.Text;
using Microsoft.Extensions.DependencyInjection;

namespace CsCode.Build.CMake;

/// <summary>
/// C without CMake (docs/C-CPP.md §4): <b>Build and Run Active File</b> (<c>C_Cpp.BuildAndRunFile</c>, cpptools' id) compiles the active C file on
/// its own with <c>clang -g -O0 -Wall</c> (<c>cc</c> elsewhere) next to it, its problems in the Error List, and runs the program in the
/// integrated terminal (so it can read stdin); <b>Create compile_flags.txt</b> writes the flags clangd uses for every file of a folder without a
/// compilation database. Started by <see cref="CMakeHost"/>; the compiler runs like cmake does (tracked, below normal priority).
/// </summary>
internal sealed class CFileCommands(IServiceProvider services, ICMakeProcesses processes, Func<string, string?> environment) : IDisposable
{
    public const string BuildAndRunFile = "C_Cpp.BuildAndRunFile";
    public const string CreateCompileFlags = "cscode.c.createCompileFlags";
    public const string CompileFlagsFile = "compile_flags.txt";
    public const string OutputChannelName = "C";
    private const string Category = "C/C++";

    /// <summary>What Create compile_flags.txt writes: C17 with the usual warnings, the folder and its include/ on the include path (clangd reads them relative to compile_flags.txt's folder).</summary>
    internal static readonly string[] DefaultFlags = ["-xc", "-std=c17", "-Wall", "-Wextra", "-I.", "-Iinclude"];

    private readonly DisposableStore _store = new();
    private readonly SemaphoreSlim _building = new(1, 1);

    private INavigationService? Navigation => services.GetService<INavigationService>();
    private IWindowService? Window => services.GetService<IWindowService>();

    public IDisposable Register(ICommandService commands)
    {
        _store.Add(commands.Register(BuildAndRunFile, async _ => await BuildAndRunAsync().ConfigureAwait(false),
            new CommandMetadata(BuildAndRunFile, "Build and Run Active File", Category, Enablement: "editorLangId == c")));
        _store.Add(commands.Register(CreateCompileFlags, async _ => await CreateCompileFlagsAsync().ConfigureAwait(false),
            new CommandMetadata(CreateCompileFlags, "Create compile_flags.txt", Category)));
        return this;
    }

    /// <summary>The compiler: <c>clang</c> on macOS (Apple's, as the C launch template's task uses), else <c>cc</c>, else <c>gcc</c> or <c>clang</c>.</summary>
    internal string? Compiler() =>
        (OperatingSystem.IsMacOS() ? new[] { "clang", "cc" } : ["cc", "gcc", "clang"]).Select(n => CMakeTools.Find(n, null, environment)).FirstOrDefault(p => p is not null);

    /// <summary>The compile command for <paramref name="file"/>: the program goes next to it, named after it without the extension.</summary>
    internal static CMakeCommand CompileCommand(string compiler, string file)
    {
        var dir = Path.GetDirectoryName(file)!;
        return new CMakeCommand(compiler, ["-g", "-O0", "-Wall", file, "-o", Path.Combine(dir, Path.GetFileNameWithoutExtension(file))], dir);
    }

    /// <summary>Builds the active C file and runs it in a terminal; returns the program's path when it built, else null (the reason is shown).</summary>
    internal async Task<string?> BuildAndRunAsync(bool run = true)
    {
        if (Navigation?.ActiveDocument is not { Uri.IsFile: true } doc || doc.LanguageId != "c")
        {
            await Notify(MessageSeverity.Warning, "Build and Run Active File: open a saved C file first.").ConfigureAwait(false);
            return null;
        }
        if (Compiler() is not { } compiler)
        {
            await Notify(MessageSeverity.Error, "Build and Run Active File: no C compiler (clang, cc or gcc) was found.").ConfigureAwait(false);
            return null;
        }
        if (!await _building.WaitAsync(0).ConfigureAwait(false)) return null; // one at a time: a second click while building does nothing
        try
        {
            if (doc.IsDirty) await doc.SaveAsync().ConfigureAwait(false);
            var file = doc.Uri.LocalPath;
            var command = CompileCommand(compiler, file);
            var channel = services.GetService<IOutputService>()?.GetOrCreate(OutputChannelName);
            channel?.AppendLine("[c] " + command);
            var parser = new BuildOutputParser(services.GetService<IDiagnosticService>(), "c.buildFile:" + file, command.WorkingDirectory, command.WorkingDirectory,
                Path.GetFileName(compiler));
            parser.Start();
            var exit = await processes.RunAsync(command, lines =>
            {
                foreach (var line in lines)
                {
                    channel?.AppendLine(line);
                    parser.Line(line);
                }
                parser.Flush();
            }, CancellationToken.None).ConfigureAwait(false);
            parser.Complete();
            var program = command.Arguments[^1];
            if (exit != 0 || !File.Exists(program))
            {
                channel?.AppendLine($"[c] Build failed with exit code {exit}.");
                await Notify(MessageSeverity.Error, $"{Path.GetFileName(file)} did not build: {parser.Errors} error{(parser.Errors == 1 ? "" : "s")} (see the Problems list and the C output).").ConfigureAwait(false);
                return null;
            }
            channel?.AppendLine($"[c] Built {program}");
            if (run && services.GetService<ITerminalService>() is { } terminals)
            {
                var terminal = await terminals.CreateAsync(new TerminalCreateOptions
                {
                    Name = "C: " + Path.GetFileName(program),
                    ShellPath = program,
                    Cwd = command.WorkingDirectory,
                    IsTransient = true,
                    ReuseWhenIdle = true,
                }).ConfigureAwait(false);
                await terminals.ShowAsync(terminal).ConfigureAwait(false);
            }
            return program;
        }
        finally { _building.Release(); }
    }

    /// <summary>
    /// Writes <see cref="DefaultFlags"/> to compile_flags.txt in the active file's workspace folder (else the first folder) and opens it; an
    /// existing one is opened as it is. Says when the folder has a compile_commands.json, which clangd prefers.
    /// </summary>
    internal async Task<string?> CreateCompileFlagsAsync()
    {
        var folders = services.GetService<IWorkspaceService>()?.Folders.Where(f => f.IsFile).Select(f => f.LocalPath).ToList() ?? [];
        var active = Navigation?.ActiveDocument?.Uri is { IsFile: true } u ? u.LocalPath : null;
        var folder = folders.Where(f => active is not null && active.StartsWith(Path.TrimEndingDirectorySeparator(f) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .OrderByDescending(f => f.Length).FirstOrDefault() ?? folders.FirstOrDefault();
        if (folder is null)
        {
            await Notify(MessageSeverity.Warning, "Create compile_flags.txt: open a folder first.").ConfigureAwait(false);
            return null;
        }
        var path = Path.Combine(folder, CompileFlagsFile);
        var existed = File.Exists(path);
        if (!existed) await File.WriteAllTextAsync(path, string.Join('\n', DefaultFlags) + "\n").ConfigureAwait(false);
        if (Navigation is { } nav) await nav.OpenAsync(new Uri(path)).ConfigureAwait(false);
        if (File.Exists(Path.Combine(folder, "compile_commands.json")))
            await Notify(MessageSeverity.Info, "This folder has a compile_commands.json: clangd uses it rather than compile_flags.txt.").ConfigureAwait(false);
        else if (!existed && services.GetService<ILanguageServerManager>() is { } servers && servers.Servers.Any(s => s.Id == ClangdCompileCommands.ServerId && s.State == LanguageServerState.Running)
                 && Window is { } window
                 && await window.ShowMessageAsync(MessageSeverity.Info, "clangd reads compile_flags.txt for files it opens next. Restart it to apply the flags to the open files?",
                     [new("Restart clangd")]).ConfigureAwait(false) == "Restart clangd")
            await servers.RestartAsync(ClangdCompileCommands.ServerId).ConfigureAwait(false);
        return path;
    }

    private async Task Notify(MessageSeverity severity, string message)
    {
        if (Window is { } window) await window.ShowMessageAsync(severity, message).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _store.Dispose();
        _building.Dispose();
    }
}
