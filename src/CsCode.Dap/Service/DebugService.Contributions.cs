using System.Text.Json;
using CsCode.Core.Contributions;
using CsCode.Core.Debugging;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Dap.Adapters;
using CsCode.Dap.Configuration;
using CsCode.Dap.Session;

namespace CsCode.Dap.Service;

// Native customisation (there is NO extension system): debug adapters come from the built-ins and from the user's
// `cscode.debuggers` setting, which this module owns and publishes into IContributionRegistry (ContributionSources.User).
public sealed partial class DebugService
{
    public const string DebuggersSetting = "cscode.debuggers";

    private readonly DisposableStore _contributionTokens = new();
    private DisposableStore _userContributions = new();
    private ContributedAdapterFactory? _contributed;

    internal ContributedAdapterFactory ContributedFactory => _contributed ??= new ContributedAdapterFactory(_p);

    private void RegisterSettings()
    {
        if (_p.Configuration is not { } cfg) return;
        static JsonElement? Json(object? v) => JsonSerializer.SerializeToElement(v);
        _contributionTokens.Add(cfg.RegisterProperties(
        [
            new ConfigurationProperty(DebuggersSetting, Json(Array.Empty<object>()),
                "Debug adapters you configured (array of {type, label, program, runtime, args, languages, windows/linux/osx: {program}, initialConfigurations}). Replaces VS Code's debugger extensions."),
            new ConfigurationProperty(NetCoreDbgAdapterFactory.SettingPath, Json(""),
                "Path of the netcoredbg executable used for .NET debugging (empty: tools directory, then PATH, then an offer to download it)."),
            new ConfigurationProperty("cscode.debug.requestTimeoutSeconds", Json(10),
                "Seconds a debug adapter may take to answer one request before the session reports it unresponsive."),
        ]));
    }

    private void InitializeContributions()
    {
        RegisterSettings();
        // Gutter breakpoints are enabled for the languages of the built-in debuggers.
        if (_p.Contributions is { } registry)
        {
            var languages = _builtins.Values.SelectMany(b => b.Languages).Distinct().Select(l => new BreakpointLanguageContribution(l)).ToArray();
            if (languages.Length > 0)
                _contributionTokens.Add(registry.Contribute(ContributionSources.Builtin, _p.Paths?.InstallDir ?? "", languages));
            _contributionTokens.Add(registry.Of<DebuggerContribution>().Changed.Subscribe(_ => _lane.Post(() => { RebuildDebuggerTypes(); UpdateContextKeys(); })));
        }
        LoadUserDebuggers();
        if (_p.Configuration is { } cfg)
            _contributionTokens.Add(cfg.Changed.Subscribe(e => { if (e.Affects(DebuggersSetting)) LoadUserDebuggers(); }));
    }

    /// <summary>(Re)reads the <c>cscode.debuggers</c> array and replaces this module's user contributions.</summary>
    public void LoadUserDebuggers()
    {
        var old = Interlocked.Exchange(ref _userContributions, new DisposableStore());
        old.Dispose();
        if (_p.Contributions is not { } registry || _p.Configuration?.GetRaw(DebuggersSetting) is not { ValueKind: JsonValueKind.Array } array) { _lane.Post(RebuildDebuggerTypes); return; }

        var debuggers = new List<DebuggerContribution>();
        foreach (var e in array.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object))
            if (Parse(e) is { } d) debuggers.Add(d);
        var dir = _p.Paths?.UserDataDir ?? "";
        _userContributions.Add(registry.Contribute(ContributionSources.User, dir, debuggers));
        var languages = debuggers.SelectMany(d => d.Languages ?? []).Distinct().Select(l => new BreakpointLanguageContribution(l)).ToArray();
        if (languages.Length > 0) _userContributions.Add(registry.Contribute(ContributionSources.User, dir, languages));
        _lane.Post(() => { RebuildDebuggerTypes(); UpdateContextKeys(); });
    }

    private static DebuggerContribution? Parse(JsonElement e)
    {
        static string? Str(JsonElement o, string name) => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        static IReadOnlyList<string>? Strings(JsonElement o, string name) =>
            o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? [.. v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!)] : null;
        static JsonElement? Raw(JsonElement o, string name) => o.TryGetProperty(name, out var v) ? v.Clone() : null;

        var type = Str(e, "type");
        if (string.IsNullOrWhiteSpace(type)) return null; // an entry without a type cannot be selected
        var platforms = new Dictionary<string, string>();
        foreach (var os in new[] { "windows", "linux", "osx" })
        {
            if (!e.TryGetProperty(os, out var block)) continue;
            var program = block.ValueKind == JsonValueKind.String ? block.GetString() : block.ValueKind == JsonValueKind.Object ? Str(block, "program") : null;
            if (program is not null) platforms[os] = program;
        }
        return new DebuggerContribution(type, Str(e, "label") ?? type, Str(e, "program"), Str(e, "runtime"), Strings(e, "args"),
            platforms.Count > 0 ? platforms : null, Strings(e, "languages"), Raw(e, "configurationAttributes"),
            Raw(e, "initialConfigurations"), Raw(e, "configurationSnippets"), Raw(e, "variables"));
    }

    private void RebuildDebuggerTypes()
    {
        var types = new Dictionary<string, DebuggerType>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in _builtins.Values) types[b.Type] = new DebuggerType(b.Type, b.Label, b.Languages, null);
        string[] registered;
        lock (_gate) registered = [.. _factories.Where(kv => kv.Value.Count > 0).Select(kv => kv.Key)];
        foreach (var t in registered) types.TryAdd(t, new DebuggerType(t, t, [], null));
        if (_p.Contributions is { } registry)
            foreach (var c in registry.Of<DebuggerContribution>().Items)
                types[c.Value.Type] = new DebuggerType(c.Value.Type, c.Value.Label, c.Value.Languages ?? [], null);
        Volatile.Write(ref _debuggerTypes, [.. types.Values.OrderBy(t => t.Label, StringComparer.CurrentCultureIgnoreCase)]);
    }
}

/// <summary>Builds an adapter launch command from a user-configured <see cref="DebuggerContribution"/> (<c>program</c>/<c>runtime</c>/<c>args</c>/platform entries).</summary>
public sealed class ContributedAdapterFactory(DapPlatform platform) : IDebugAdapterFactory
{
    public async ValueTask<DebugAdapterDescriptor?> CreateDescriptorAsync(DebugConfiguration configuration, CancellationToken ct)
    {
        if (platform.Contributions is not { } registry) return null;
        var contribution = registry.Of<DebuggerContribution>().Items.LastOrDefault(i => string.Equals(i.Value.Type, configuration.Type, StringComparison.OrdinalIgnoreCase))?.Value;
        if (contribution is null) return null;

        var os = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "osx" : "linux";
        var program = contribution.PlatformPrograms is { } p && p.TryGetValue(os, out var specific) ? specific : contribution.Program;
        if (string.IsNullOrWhiteSpace(program) && string.IsNullOrWhiteSpace(contribution.Runtime)) return null;

        var resolver = new VariableResolver(platform.CreateVariableContext(configuration.WorkspaceFolder, new Dictionary<string, JsonElement>(), null));
        var args = new List<string>();
        string command;
        if (!string.IsNullOrWhiteSpace(contribution.Runtime))
        {
            command = await resolver.ResolveAsync(contribution.Runtime, ct);
            if (!string.IsNullOrWhiteSpace(program)) args.Add(await resolver.ResolveAsync(program, ct));
        }
        else command = await resolver.ResolveAsync(program!, ct);
        foreach (var a in contribution.Args ?? []) args.Add(await resolver.ResolveAsync(a, ct));

        var cwd = configuration.WorkspaceFolder is { IsFile: true } f ? f.LocalPath.TrimEnd('/') : null;
        return new DebugAdapterExecutable(command, args, null, cwd is not null && Directory.Exists(cwd) ? cwd : null);
    }
}
