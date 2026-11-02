using System.Text.Json;
using CsCode.Core.Languages;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Editor.View;
using Microsoft.Extensions.Logging;

namespace CsCode.Editor.Features;

/// <summary>
/// What a click on a code lens runs. Servers name two kinds of command:
/// <list type="bullet">
/// <item>client-side commands that VS Code (or the server's VS Code extension) implements — the reference lenses' <c>editor.action.showReferences</c>
/// (and <c>editor.action.peekLocations</c> / <c>goToLocations</c>, rust-analyzer's <c>rust-analyzer.showReferences</c>, Roslyn's
/// <c>roslyn.client.peekReferences</c>, <c>editor.action.findReferences</c>): mapped here onto the editor's references UI — no location
/// says so in the status bar, one is opened, several go to <c>cscode.references.show</c> (the shell's Find Symbol Results) or a quick pick;
/// a command without locations asks the reference providers at its position;</item>
/// <item>everything else runs through <see cref="ICommandService"/> with the lens's arguments: the server's own commands are registered there by
/// the LSP client (<c>workspace/executeCommand</c>), CsCode's own (the Test Explorer's run/debug lenses) by their modules. A command nobody
/// registered says so in the status bar.</item>
/// </list>
/// </summary>
internal static class CodeLensCommands
{
    private static readonly HashSet<string> ReferenceCommands = new(StringComparer.Ordinal)
    {
        "editor.action.showReferences", "editor.action.peekLocations", "editor.action.goToLocations", "editor.action.findReferences",
        "rust-analyzer.showReferences", "roslyn.client.peekReferences",
    };

    /// <summary>
    /// Client-side commands of a server's VS Code extension that CsCode does not run and whose lens it hides. None today: rust-analyzer's
    /// "▶︎ Run" / "Debug" lenses run through its server extension (CsCode.Lsp's RustAnalyzerExtension), which drops those on tests itself, where
    /// the Test Explorer's own "Run Test | Debug Test" lenses replace them (lang-2).
    /// </summary>
    private static readonly HashSet<string> HiddenCommands = new(StringComparer.Ordinal);

    /// <summary>Whether a lens with this command is not shown (see <see cref="HiddenCommands"/>).</summary>
    internal static bool IsHidden(Command? command) => command is not null && HiddenCommands.Contains(command.CommandId);

    /// <summary>Whether <paramref name="commandId"/> is one of the client-side reference commands this class maps.</summary>
    internal static bool IsReferenceCommand(string commandId) => ReferenceCommands.Contains(commandId);

    public static async Task ExecuteAsync(CodeEditorControl view, Command command, TextRange lensRange)
    {
        try
        {
            if (ReferenceCommands.Contains(command.CommandId))
            {
                await ShowReferencesAsync(view, command, lensRange).ConfigureAwait(true);
                return;
            }
            if (view.Services.Commands is { } commands && commands.Has(command.CommandId))
            {
                var args = (command.Arguments ?? []).Select(a => (object?)a).ToArray();
                await commands.ExecuteAsync(command.CommandId, args).ConfigureAwait(true);
                return;
            }
            FeatureSupport.Status(view, $"'{CodeLensFeature.CleanTitle(command.Title)}': the command '{command.CommandId}' is not available");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            view.Services.LoggerFactory.CreateLogger("CsCode.Editor.CodeLens").LogWarning(ex, "Code lens command {Command} failed", command.CommandId);
            FeatureSupport.Status(view, $"'{CodeLensFeature.CleanTitle(command.Title)}' failed: {ex.Message}");
        }
    }

    private static async Task ShowReferencesAsync(CodeEditorControl view, Command command, TextRange lensRange)
    {
        var args = command.Arguments ?? [];
        var uri = args.Count > 0 && ParseUri(args[0]) is { } u ? u : view.Document.Uri;
        var position = args.Count > 1 && ParsePosition(args[1]) is { } p ? p : lensRange.Start;
        var locations = args.Count > 2 ? ParseLocations(args[2]) : null;
        locations ??= await FindReferencesAsync(view, uri, position).ConfigureAwait(true);
        if (view.IsDisposed) return;
        var word = FeatureSupport.SameFile(uri, view.Document.Uri) ? WordAt(view, position) : null;
        var title = word is null ? "References" : $"References to '{word}'";
        if (locations is null or { Count: 0 })
        {
            FeatureSupport.Status(view, word is null ? "No references found" : $"No references found for '{word}'");
            return;
        }
        if (locations.Count == 1)
        {
            await FeatureSupport.OpenLocationAsync(view, locations[0].Uri, locations[0].Range).ConfigureAwait(true);
            return;
        }
        if (view.Services.Commands is { } commands && commands.Has(NavigationFeature.ShowReferencesCommand))
        {
            await commands.ExecuteAsync(NavigationFeature.ShowReferencesCommand, locations, title).ConfigureAwait(true);
            return;
        }
        if (view.Services.Window is not { } window)
        {
            await FeatureSupport.OpenLocationAsync(view, locations[0].Uri, locations[0].Range).ConfigureAwait(true);
            return;
        }
        var items = locations.Select(l => new QuickPickItem(
            Path.GetFileName(l.Uri.IsFile ? l.Uri.LocalPath : l.Uri.AbsolutePath) + ":" + (l.Range.Start.Line + 1),
            l.Uri.IsFile ? l.Uri.LocalPath : l.Uri.ToString(), null, Tag: l)).ToList();
        var picked = await window.ShowQuickPickAsync(items, new QuickPickOptions(Title: title)).ConfigureAwait(true);
        if (picked?.Tag is Location location) await FeatureSupport.OpenLocationAsync(view, location.Uri, location.Range).ConfigureAwait(true);
    }

    /// <summary>References at a position of this view's document (another document's lens is not ours to answer).</summary>
    private static async Task<IReadOnlyList<Location>?> FindReferencesAsync(CodeEditorControl view, Uri uri, TextPosition position)
    {
        if (!FeatureSupport.SameFile(uri, view.Document.Uri)) return null;
        var doc = view.Document;
        var all = new List<Location>();
        foreach (var provider in view.Services.Features.GetProviders<IReferenceProvider>(doc))
            if (await FeatureSupport.CallAsync(view, c => provider.ProvideReferencesAsync(doc, position, true, c), CancellationToken.None, TimeSpan.FromSeconds(8)).ConfigureAwait(true) is { } list)
                all.AddRange(list);
        return all.Distinct().Take(5000).ToList();
    }

    private static string? WordAt(CodeEditorControl view, TextPosition position)
    {
        var doc = view.Document;
        if (doc.Validate(position) != position) return null;
        var (a, b) = FeatureSupport.WordAt(view.Editor.Document, doc.OffsetAt(position));
        return b > a ? view.Editor.Document.GetText(a, b - a) : null;
    }

    // ---- the LSP JSON shapes of the arguments: DocumentUri, Position, Location[] / LocationLink[] ----

    internal static Uri? ParseUri(JsonElement e) =>
        e.ValueKind == JsonValueKind.String && Uri.TryCreate(e.GetString(), UriKind.Absolute, out var u) ? u
        : e.ValueKind == JsonValueKind.Object && e.TryGetProperty("external", out var ext) ? ParseUri(ext) // a VS Code Uri object
        : null;

    internal static TextPosition? ParsePosition(JsonElement e) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty("line", out var l) && l.TryGetInt32(out var line)
        && e.TryGetProperty("character", out var c) && c.TryGetInt32(out var ch) && line >= 0 && ch >= 0
            ? new TextPosition(line, ch) : null;

    internal static TextRange? ParseRange(JsonElement e) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty("start", out var s) && e.TryGetProperty("end", out var en)
        && ParsePosition(s) is { } start && ParsePosition(en) is { } end ? new TextRange(start, end) : null;

    internal static IReadOnlyList<Location>? ParseLocations(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Array) return null;
        var list = new List<Location>();
        foreach (var item in e.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            if (item.TryGetProperty("uri", out var uri) && ParseUri(uri) is { } u && item.TryGetProperty("range", out var r) && ParseRange(r) is { } range)
                list.Add(new Location(u, range));
            else if (item.TryGetProperty("targetUri", out var tu) && ParseUri(tu) is { } t
                     && (item.TryGetProperty("targetSelectionRange", out var tr) || item.TryGetProperty("targetRange", out tr)) && ParseRange(tr) is { } target)
                list.Add(new Location(t, target));
        }
        return list;
    }
}
