using System.Text.Json;
using CsCode.Core.Languages;
using CsCode.Core.Lifecycle;
using CsCode.Core.Python;

namespace CsCode.Lsp.Manager;

/// <summary>
/// The selected Python interpreter for the Python type checkers (Pyright and basedpyright): <c>python.pythonPath</c> in their
/// <c>workspace/configuration</c> answers for the <c>python</c> section, per folder (the request's scope), as VS Code's Python extension provides
/// it. When the selection changes the servers are told (<c>workspace/didChangeConfiguration</c>) and ask again. Only what is known already is
/// given; until discovery has finished the servers use their own default (the <c>python</c> on PATH) and are told once it has.
/// </summary>
public sealed class PythonServerSettings : ILanguageServerSettingsContributor, IDisposable
{
    /// <summary>The servers that take <c>python.pythonPath</c>: the built-in Pyright definition ("python") and basedpyright.</summary>
    public static readonly IReadOnlySet<string> Servers = new HashSet<string>(StringComparer.Ordinal) { "python", "basedpyright" };

    private readonly IPythonEnvironmentService? _environments;
    private readonly IDisposable _subscription;
    private readonly Emitter<string?> _changed = new();

    /// <param name="environments">The interpreter service; null (a host without one) contributes nothing.</param>
    public PythonServerSettings(IPythonEnvironmentService? environments)
    {
        _environments = environments;
        _subscription = environments?.ActiveChanged.Subscribe(_ => { foreach (var id in Servers) _changed.Fire(id); }) ?? Disposable.Empty;
    }

    public IEventSource<string?> Changed => _changed;

    public IReadOnlyDictionary<string, JsonElement>? Contribute(string serverId, string section, Uri? scope)
    {
        if (section != "python" || !Servers.Contains(serverId) || _environments is null) return null;
        return _environments.GetActive(scope) is { } env
            ? new Dictionary<string, JsonElement>(StringComparer.Ordinal) { ["pythonPath"] = JsonSerializer.SerializeToElement(env.Executable) }
            : null;
    }

    public void Dispose()
    {
        _subscription.Dispose();
    }
}
