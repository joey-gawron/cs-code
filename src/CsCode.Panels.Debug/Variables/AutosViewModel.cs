using System.Text.RegularExpressions;
using CsCode.Core.Debugging;
using CsCode.Core.Text;
using CsCode.Panels.Debug.Infrastructure;

namespace CsCode.Panels.Debug.Variables;

/// <summary>
/// Autos window. Visual Studio shows the variables used in the current and the preceding statement; the debug adapter protocol has no such
/// query, so this is a documented approximation:
/// <list type="number">
/// <item>Take the frame's Locals/Arguments variables (same scopes as the Locals window).</item>
/// <item>If the editor can give us the source text of the stop location (an open <see cref="ITextDocument"/>), keep the variables whose
/// name occurs as an identifier on the current or the preceding line.</item>
/// <item>Otherwise (no document, or nothing on those lines matches) show all of them minus <c>this</c> and the static-members groups.</item>
/// </list>
/// </summary>
public sealed partial class AutosViewModel : FrameVariablesViewModel
{
    /// <summary>Upper bound on the variables considered per scope (Autos never pages: it is a filtered view).</summary>
    public const int MaxCandidates = 500;

    [GeneratedRegex(@"[\p{L}_][\p{L}\p{N}_]*", RegexOptions.CultureInvariant)]
    private static partial Regex Identifier();

    public AutosViewModel(DebugPanelServices services) : base(services) => StartTracking();

    protected override async Task LoadFrameAsync(IDebugSession session, DebugFocus focus, LoadToken token)
    {
        var ct = token.Cancellation;
        var scopes = await Services.Ui.OnUi(session.GetScopesAsync(focus.FrameId!.Value, ct), ct);
        if (token.Generation != Tree.Generation) return;
        var candidates = new List<(DebugVariable Variable, int ParentReference)>();
        foreach (var scope in ScopeSelector.ForLocals(scopes))
        {
            var page = await Services.Ui.OnUi(session.GetVariablesAsync(scope.VariablesReference, 0, MaxCandidates, ct), ct);
            if (token.Generation != Tree.Generation) return;
            foreach (var v in page.Take(MaxCandidates)) candidates.Add((v, scope.VariablesReference));
        }

        var referenced = ReferencedIdentifiers();
        var shown = referenced is null ? [] : candidates.Where(c => referenced.Contains(c.Variable.Name)).ToList();
        if (shown.Count == 0) shown = candidates.Where(c => !IsThisOrStatics(c.Variable.Name)).ToList();
        // The unfiltered list keys the change highlighting: the subset changes from line to line, the frame does not.
        await Tree.ShowRootsAsync(token, shown, candidates.ConvertAll(c => c.Variable));
    }

    internal static bool IsThisOrStatics(string name) =>
        name == "this" || name.StartsWith("Static", StringComparison.OrdinalIgnoreCase) || name.StartsWith("[Static", StringComparison.OrdinalIgnoreCase);

    /// <summary>Identifiers on the stop line and the line before it, or null when the editor cannot tell.</summary>
    private HashSet<string>? ReferencedIdentifiers()
    {
        if (Services.Debug.StopLocation is not { Uri: { } uri } location) return null;
        try
        {
            ITextDocument? document = Services.Documents?.Get(uri)
                ?? Services.Navigation?.OpenDocuments.FirstOrDefault(d => d.Uri == uri);
            if (document is null || location.Line < 0 || location.Line >= document.LineCount) return null;
            var names = new HashSet<string>(StringComparer.Ordinal);
            for (var line = Math.Max(0, location.Line - 1); line <= location.Line; line++)
            {
                var text = document.GetLineText(line);
                if (text.Length > 4_000) text = text[..4_000];
                foreach (Match m in Identifier().Matches(text)) names.Add(m.Value);
            }
            return names;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or ObjectDisposedException)
        {
            return null; // the document went away while we looked: fall back to the unfiltered list
        }
    }
}
