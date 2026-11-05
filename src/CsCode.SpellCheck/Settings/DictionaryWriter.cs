using System.Text;
using System.Text.Json;
using CsCode.Core.Platform;

namespace CsCode.SpellCheck.Settings;

/// <summary>Where a word was written (for the confirmation message and tests).</summary>
public sealed record DictionaryWrite(string Description, string? Path);

/// <summary>
/// Writes words to the user's and the project's dictionaries the way cspell does: the user dictionary is <c>cSpell.words</c> in the user
/// settings; the workspace dictionary is the project's cspell config file when it has a JSON one (its <c>words</c>), else <c>cSpell.words</c>
/// in the workspace settings (<c>.cscode/settings.json</c>, the file CsCode writes workspace settings to). The list written is that file's own
/// list plus the new words, never the merged list of every layer.
/// </summary>
public static class DictionaryWriter
{
    public static async Task<DictionaryWrite> AddToUserAsync(IConfigurationService config, string key, IReadOnlyList<string> words, CancellationToken ct = default)
    {
        var list = SpellSettingsLoader.StringList(ReadLocal(config.UserSettingsPath) is { } root ? SpellSettingsLoader.Find(root, key) : null).ToList();
        if (!Append(list, words)) return new DictionaryWrite("user settings", config.UserSettingsPath);
        await config.UpdateAsync(key, list, ConfigurationTarget.User, ct).ConfigureAwait(false);
        return new DictionaryWrite("user settings", config.UserSettingsPath);
    }

    /// <summary>Removes <paramref name="words"/> (any case) from <paramref name="key"/> in the user settings (Vim's <c>zw</c> on an added word).</summary>
    public static async Task RemoveFromUserAsync(IConfigurationService config, string key, IReadOnlyList<string> words, CancellationToken ct = default)
    {
        var list = SpellSettingsLoader.StringList(ReadLocal(config.UserSettingsPath) is { } root ? SpellSettingsLoader.Find(root, key) : null).ToList();
        if (list.RemoveAll(w => words.Contains(w, StringComparer.OrdinalIgnoreCase)) == 0) return;
        await config.UpdateAsync(key, list, ConfigurationTarget.User, ct).ConfigureAwait(false);
    }

    public static async Task<DictionaryWrite> AddToWorkspaceAsync(IConfigurationService config, IWorkspaceService? workspace, SpellSettings settings,
        IReadOnlyList<string> words, CancellationToken ct = default)
    {
        var fs = workspace?.FileSystem;
        if (settings.ProjectConfigFiles.FirstOrDefault() is { } project)
        {
            var uri = project.Contains("://", StringComparison.Ordinal) ? new Uri(project) : new Uri(Path.GetFullPath(project));
            var text = await ReadAsync(fs, uri, ct).ConfigureAwait(false) ?? "{}";
            var updated = JsonWordListEditor.AddWords(text, "words", words);
            if (!ReferenceEquals(updated, text)) await WriteAsync(fs, uri, updated, ct).ConfigureAwait(false);
            return new DictionaryWrite(Path.GetFileName(uri.LocalPath), uri.IsFile ? uri.LocalPath : uri.ToString());
        }
        if (workspace?.PrimaryFolder is not { } folder)
            throw new InvalidOperationException("No folder is open: there is no workspace dictionary to add to.");
        var settingsUri = new Uri(folder.ToString().TrimEnd('/') + "/.cscode/settings.json");
        var current = await ReadAsync(fs, settingsUri, ct).ConfigureAwait(false) is { } json && SpellSettingsLoader.ParseJsonc(json) is { } root
            ? SpellSettingsLoader.StringList(SpellSettingsLoader.Find(root, SpellSettingsLoader.Words)).ToList()
            : [];
        if (Append(current, words)) await config.UpdateAsync(SpellSettingsLoader.Words, current, ConfigurationTarget.Workspace, ct).ConfigureAwait(false);
        return new DictionaryWrite("workspace settings", settingsUri.IsFile ? settingsUri.LocalPath : settingsUri.ToString());
    }

    private static bool Append(List<string> list, IReadOnlyList<string> words)
    {
        var changed = false;
        foreach (var w in words)
        {
            if (string.IsNullOrWhiteSpace(w) || list.Contains(w, StringComparer.OrdinalIgnoreCase)) continue;
            list.Add(w);
            changed = true;
        }
        return changed;
    }

    private static JsonElement? ReadLocal(string path)
    {
        try { return File.Exists(path) ? SpellSettingsLoader.ParseJsonc(File.ReadAllText(path)) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static async Task<string?> ReadAsync(IFileSystem? fs, Uri uri, CancellationToken ct)
    {
        if (fs is null) return uri.IsFile && File.Exists(uri.LocalPath) ? await File.ReadAllTextAsync(uri.LocalPath, ct).ConfigureAwait(false) : null;
        if (await fs.StatAsync(uri, ct).ConfigureAwait(false) is null) return null;
        return Encoding.UTF8.GetString(await fs.ReadFileAsync(uri, ct).ConfigureAwait(false));
    }

    private static async Task WriteAsync(IFileSystem? fs, Uri uri, string text, CancellationToken ct)
    {
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text);
        if (fs is not null)
        {
            await fs.WriteFileAsync(uri, bytes, createParents: true, ct).ConfigureAwait(false);
            return;
        }
        var temp = uri.LocalPath + ".cscode-tmp";
        await File.WriteAllBytesAsync(temp, bytes, ct).ConfigureAwait(false);
        File.Move(temp, uri.LocalPath, overwrite: true);
    }
}
