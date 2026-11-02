using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CsCode.ProjectTemplates.Engine;

/// <summary>
/// Writes a generator's files under the project folder: UTF-8 without a BOM, LF line ends (what the generated <c>.editorconfig</c> asks for, and
/// every tool reads them on Windows too), a final newline, parent folders created. Off the UI thread only.
/// </summary>
public sealed class ProjectWriter(string root)
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        IndentSize = 4,
        // Keep "$rustc", "<node_internals>" and non-ASCII names as written: the files are for people to read.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public string Root { get; } = root;

    /// <summary>The absolute path of <paramref name="relative"/> (forward slashes) under the project folder.</summary>
    public string PathOf(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));

    public bool Exists(string relative) => File.Exists(PathOf(relative)) || Directory.Exists(PathOf(relative));

    /// <summary>Writes <paramref name="content"/> (its line ends normalised to LF), replacing a file of the same name.</summary>
    public async Task WriteAsync(string relative, string content, CancellationToken ct)
    {
        var path = PathOf(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var text = content.ReplaceLineEndings("\n");
        if (!text.EndsWith('\n')) text += "\n";
        await File.WriteAllTextAsync(path, text, Utf8, ct).ConfigureAwait(false);
    }

    /// <summary>Writes the file only when the tool did not make one (cargo's .gitignore, dotnet's).</summary>
    public async Task<bool> WriteIfMissingAsync(string relative, string content, CancellationToken ct)
    {
        if (Exists(relative)) return false;
        await WriteAsync(relative, content, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>Writes <paramref name="json"/> indented by four spaces, as VS Code writes launch.json and tasks.json.</summary>
    public Task WriteJsonAsync(string relative, JsonNode json, CancellationToken ct) => WriteAsync(relative, json.ToJsonString(Indented), ct);

    /// <summary>Appends the lines of <paramref name="extra"/> that the file does not have yet (a tool's .gitignore completed with the language's).</summary>
    public async Task AppendMissingLinesAsync(string relative, string extra, CancellationToken ct)
    {
        var path = PathOf(relative);
        if (!File.Exists(path))
        {
            await WriteAsync(relative, extra, ct).ConfigureAwait(false);
            return;
        }
        var existing = (await File.ReadAllTextAsync(path, ct).ConfigureAwait(false)).ReplaceLineEndings("\n");
        var have = existing.Split('\n').Select(l => l.Trim()).ToHashSet(StringComparer.Ordinal);
        var missing = extra.ReplaceLineEndings("\n").Split('\n').Where(l => l.Trim().Length > 0 && !l.TrimStart().StartsWith('#') && !have.Contains(l.Trim())).ToList();
        if (missing.Count == 0) return;
        var text = existing.TrimEnd('\n') + "\n\n# Added by CsCode\n" + string.Join('\n', missing) + "\n";
        await File.WriteAllTextAsync(path, text, Utf8, ct).ConfigureAwait(false);
    }
}
