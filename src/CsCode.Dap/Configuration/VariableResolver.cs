using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CsCode.Dap.Configuration;

/// <summary>A <c>${…}</c> variable that cannot be resolved (no editor open for ${file}, undefined ${config:x}, unknown ${input:x}…).</summary>
public sealed class VariableResolutionException(string message) : Exception(message);

/// <summary>Everything variable substitution can consult. Delegates keep it trivially fakeable and free of service dependencies.</summary>
public sealed class VariableContext
{
    public string? WorkspaceFolder { get; init; }
    /// <summary>Other workspace folders by name for <c>${workspaceFolder:name}</c>.</summary>
    public Func<string, string?>? WorkspaceFolderByName { get; init; }
    public string? ActiveFile { get; init; }
    /// <summary>1-based, like VS Code's ${lineNumber}.</summary>
    public int? ActiveLine { get; init; }
    public Func<string?>? SelectedText { get; init; }
    public string? Cwd { get; init; }
    public string UserHome { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public Func<string, string?> Env { get; init; } = Environment.GetEnvironmentVariable;
    /// <summary>Setting value as text, or null when undefined.</summary>
    public Func<string, string?>? Config { get; init; }
    public Func<string, CancellationToken, ValueTask<string?>>? Command { get; init; }
    /// <summary>Resolves <c>${input:id}</c> (prompt / pick / command). Return null = the user cancelled.</summary>
    public Func<string, CancellationToken, ValueTask<string?>>? Input { get; init; }
    public Func<CancellationToken, ValueTask<string?>>? DefaultBuildTask { get; init; }
}

/// <summary>
/// VS Code-compatible variable substitution shared by launch.json and tasks.json. Applied recursively to every string
/// in a JSON tree. Semantics (documented in docs/DAP.md): known variable that cannot be resolved → <see cref="VariableResolutionException"/>
/// (a half-substituted launch would be worse than an error); unknown <c>${name}</c> → left verbatim (adapters have their own ${…});
/// <c>${env:UNSET}</c> → empty string; each <c>${input:id}</c> is asked once per resolution pass.
/// </summary>
public sealed class VariableResolver(VariableContext ctx)
{
    private const int MaxDepth = 6;
    private readonly Dictionary<string, string?> _inputs = new(StringComparer.Ordinal);

    public async ValueTask<JsonNode?> ResolveAsync(JsonNode? node, CancellationToken ct)
    {
        switch (node)
        {
            case null: return null;
            case JsonValue v when v.TryGetValue<string>(out var s):
                return JsonValue.Create(await ResolveAsync(s, ct));
            case JsonObject o:
            {
                var copy = new JsonObject();
                foreach (var (k, child) in o) copy[k] = await ResolveAsync(child, ct);
                return copy;
            }
            case JsonArray a:
            {
                var copy = new JsonArray();
                foreach (var child in a) copy.Add(await ResolveAsync(child, ct));
                return copy;
            }
            default: return node.DeepClone();
        }
    }

    public ValueTask<string> ResolveAsync(string text, CancellationToken ct) => ResolveAsync(text, 0, ct);

    private async ValueTask<string> ResolveAsync(string text, int depth, CancellationToken ct)
    {
        if (text.IndexOf("${", StringComparison.Ordinal) < 0) return text;
        var sb = new StringBuilder(text.Length + 16);
        var i = 0;
        while (i < text.Length)
        {
            var start = text.IndexOf("${", i, StringComparison.Ordinal);
            if (start < 0) { sb.Append(text, i, text.Length - i); break; }
            var end = text.IndexOf('}', start + 2);
            if (end < 0) { sb.Append(text, i, text.Length - i); break; } // unterminated: literal
            sb.Append(text, i, start - i);
            var expr = text.Substring(start + 2, end - start - 2);
            var value = await EvaluateAsync(expr, depth, ct);
            sb.Append(value ?? text.Substring(start, end - start + 1)); // null = not ours: keep verbatim
            i = end + 1;
        }
        return sb.ToString();
    }

    /// <summary>null = unknown variable (caller keeps the text).</summary>
    private async ValueTask<string?> EvaluateAsync(string expr, int depth, CancellationToken ct)
    {
        string name = expr, arg = "";
        var colon = expr.IndexOf(':');
        if (colon >= 0) { name = expr[..colon]; arg = expr[(colon + 1)..]; }

        switch (name)
        {
            case "workspaceFolder":
                if (arg.Length > 0)
                    return ctx.WorkspaceFolderByName?.Invoke(arg) ?? throw new VariableResolutionException($"Variable ${{{expr}}}: there is no workspace folder named '{arg}'.");
                return RequireFolder(expr);
            case "workspaceFolderBasename":
                return Path.GetFileName(RequireFolder(expr).TrimEnd('/', '\\'));
            case "cwd":
                return ctx.Cwd ?? ctx.WorkspaceFolder ?? Environment.CurrentDirectory;
            case "file":
                return RequireFile(expr);
            case "fileBasename":
                return Path.GetFileName(RequireFile(expr));
            case "fileBasenameNoExtension":
                return Path.GetFileNameWithoutExtension(RequireFile(expr));
            case "fileExtname":
                return Path.GetExtension(RequireFile(expr));
            case "fileDirname":
                return Path.GetDirectoryName(RequireFile(expr)) ?? "";
            case "fileDirnameBasename":
                return Path.GetFileName(Path.GetDirectoryName(RequireFile(expr)) ?? "");
            case "fileWorkspaceFolder":
                return RequireFolder(expr);
            case "relativeFile":
                return Relative(RequireFolder(expr), RequireFile(expr));
            case "relativeFileDirname":
                return Path.GetDirectoryName(Relative(RequireFolder(expr), RequireFile(expr))) ?? "";
            case "lineNumber":
                return ctx.ActiveLine is { } line ? line.ToString() : throw new VariableResolutionException($"Variable ${{{expr}}}: no editor is open.");
            case "selectedText":
                return ctx.SelectedText?.Invoke() ?? "";
            case "pathSeparator" or "/":
                return Path.DirectorySeparatorChar.ToString();
            case "userHome":
                return ctx.UserHome;
            case "defaultBuildTask":
                return (ctx.DefaultBuildTask is { } f ? await f(ct) : null)
                    ?? throw new VariableResolutionException("Variable ${defaultBuildTask}: no default build task is defined in tasks.json.");
            case "env" when arg.Length > 0:
                return ctx.Env(arg) ?? "";
            case "config" when arg.Length > 0:
            {
                var value = ctx.Config?.Invoke(arg)
                    ?? throw new VariableResolutionException($"Variable ${{{expr}}}: the setting '{arg}' is not defined.");
                // Settings may themselves use variables (e.g. "${workspaceFolder}/out"): resolve those too, with a depth limit.
                return depth < MaxDepth ? await ResolveAsync(value, depth + 1, ct) : value;
            }
            case "command" when arg.Length > 0:
            {
                if (ctx.Command is null) throw new VariableResolutionException($"Variable ${{{expr}}}: commands are not available.");
                try
                {
                    return await ctx.Command(arg, ct)
                        ?? throw new OperationCanceledException($"Command '{arg}' did not return a value");
                }
                catch (Exception ex) when (ex is not OperationCanceledException and not VariableResolutionException)
                {
                    throw new VariableResolutionException($"Variable ${{{expr}}}: {ex.Message}");
                }
            }
            case "input" when arg.Length > 0:
            {
                if (_inputs.TryGetValue(arg, out var cached)) return cached;
                if (ctx.Input is null) throw new VariableResolutionException($"Variable ${{{expr}}}: inputs are not available.");
                var value = await ctx.Input(arg, ct) ?? throw new OperationCanceledException($"Input '{arg}' was cancelled");
                _inputs[arg] = value;
                return value;
            }
            default:
                return null;
        }
    }

    private string RequireFolder(string expr) =>
        ctx.WorkspaceFolder ?? throw new VariableResolutionException($"Variable ${{{expr}}}: no folder is open.");

    private string RequireFile(string expr) =>
        ctx.ActiveFile ?? throw new VariableResolutionException($"Variable ${{{expr}}}: no file is open — open a file in the editor first.");

    private static string Relative(string folder, string file)
    {
        var rel = Path.GetRelativePath(folder, file);
        return rel.StartsWith("..", StringComparison.Ordinal) ? file : rel; // outside the workspace: VS Code yields the absolute path
    }

    /// <summary>Platform-specific blocks (<c>windows</c>/<c>linux</c>/<c>osx</c>) are merged over the base object (objects deep-merge, everything else replaces) and removed.</summary>
    public static JsonObject MergePlatformBlock(JsonObject config)
    {
        var platform = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "osx" : "linux";
        var result = new JsonObject();
        foreach (var (k, v) in config)
            if (k is not ("windows" or "linux" or "osx")) result[k] = v?.DeepClone();
        if (config[platform] is JsonObject block) DeepMerge(result, block);
        return result;
    }

    private static void DeepMerge(JsonObject target, JsonObject source)
    {
        foreach (var (k, v) in source)
        {
            if (v is JsonObject so && target[k] is JsonObject to) DeepMerge(to, so);
            else target[k] = v?.DeepClone();
        }
    }

    /// <summary>JsonElement → JsonNode helper for callers holding <see cref="JsonElement"/>s.</summary>
    public static JsonNode? ToNode(JsonElement e) => JsonNode.Parse(e.GetRawText());
}
