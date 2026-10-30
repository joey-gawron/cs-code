namespace CsCode.Editor.TreeSitter.Highlighting;

/// <summary>
/// Maps tree-sitter capture names (nvim-treesitter's standard set: <c>@keyword.conditional</c>, <c>@function.method.call</c>,
/// <c>@variable.member</c>, <c>@markup.heading.1</c>, …) onto TextMate scope names, so that EVERY existing colour theme (VS Code JSON
/// themes, the VS 2022 themes, the 21 bundled ones) colours tree-sitter output through the same <c>TokenColorResolver</c> as TextMate
/// output, with no per-theme work. The scopes chosen are the ones VS Code's own TextMate grammars emit for the same construct
/// (e.g. <c>@keyword.conditional</c> → <c>keyword.control.conditional</c>, which Dark+ paints purple like the TypeScript grammar's
/// <c>if</c>), plus the language suffix (<c>keyword.control.conditional.rust</c>) as TextMate grammars do.
///
/// Lookup is by longest dotted prefix: <c>@keyword.conditional.ternary</c> falls back to <c>@keyword.conditional</c>, then <c>@keyword</c>.
/// Captures starting with <c>_</c> (predicate helpers), and Neovim's <c>@spell</c>/<c>@nospell</c>/<c>@conceal</c> are not highlights
/// (<see cref="Skip"/>); <c>@none</c> resets to the layer's base (<see cref="None"/>).
/// </summary>
public static class CaptureScopes
{
    /// <summary>Not a highlight capture: ignored.</summary>
    public const string Skip = "";

    /// <summary>Clears outer highlights (Neovim's <c>@none</c>).</summary>
    public const string None = "\0none";

    private static readonly Dictionary<string, string> Default = new(StringComparer.Ordinal)
    {
        ["variable"] = "variable.other.readwrite",
        ["variable.builtin"] = "variable.language",
        ["variable.parameter"] = "variable.parameter",
        ["variable.parameter.builtin"] = "variable.parameter",
        ["variable.member"] = "variable.other.property",
        ["constant"] = "variable.other.constant",
        ["constant.builtin"] = "constant.language",
        ["constant.macro"] = "variable.other.constant",
        ["module"] = "entity.name.namespace",
        ["module.builtin"] = "entity.name.namespace",
        ["label"] = "entity.name.label",
        ["string"] = "string.quoted",
        ["string.documentation"] = "string.quoted.docstring",
        ["string.regexp"] = "string.regexp",
        ["string.escape"] = "constant.character.escape",
        ["string.special"] = "string.other",
        ["string.special.symbol"] = "constant.other.symbol",
        ["string.special.url"] = "markup.underline.link",
        ["string.special.path"] = "string.other.path",
        ["character"] = "string.quoted.single",
        ["character.special"] = "constant.character",
        ["boolean"] = "constant.language.boolean",
        ["number"] = "constant.numeric",
        ["number.float"] = "constant.numeric.float",
        ["type"] = "entity.name.type",
        ["type.builtin"] = "support.type.primitive",
        ["type.definition"] = "entity.name.type",
        ["attribute"] = "entity.other.attribute-name",
        ["attribute.builtin"] = "entity.other.attribute-name",
        ["property"] = "variable.other.property",
        ["function"] = "entity.name.function",
        ["function.builtin"] = "support.function",
        ["function.call"] = "entity.name.function",
        ["function.macro"] = "entity.name.function.macro",
        ["function.method"] = "entity.name.function.member",
        ["function.method.call"] = "entity.name.function.member",
        ["constructor"] = "entity.name.type.class",
        ["operator"] = "keyword.operator",
        ["keyword"] = "keyword.other",
        ["keyword.coroutine"] = "keyword.control.flow",
        ["keyword.function"] = "storage.type.function",
        ["keyword.operator"] = "keyword.operator.expression",
        ["keyword.import"] = "keyword.control.import",
        ["keyword.type"] = "storage.type",
        ["keyword.modifier"] = "storage.modifier",
        ["keyword.repeat"] = "keyword.control.loop",
        ["keyword.return"] = "keyword.control.flow",
        ["keyword.debug"] = "keyword.other.debugger",
        ["keyword.exception"] = "keyword.control.exception",
        ["keyword.conditional"] = "keyword.control.conditional",
        ["keyword.conditional.ternary"] = "keyword.operator.ternary",
        ["keyword.directive"] = "keyword.preprocessor",
        ["keyword.directive.define"] = "keyword.preprocessor.define",
        ["punctuation.delimiter"] = "punctuation.separator",
        ["punctuation.bracket"] = "punctuation.bracket",
        ["punctuation.special"] = "punctuation.special",
        ["comment"] = "comment.line",
        ["comment.documentation"] = "comment.block.documentation",
        ["comment.error"] = "comment.line",
        ["comment.warning"] = "comment.line",
        ["comment.todo"] = "comment.line",
        ["comment.note"] = "comment.line",
        ["markup.strong"] = "markup.bold",
        ["markup.italic"] = "markup.italic",
        ["markup.strikethrough"] = "markup.strikethrough",
        ["markup.underline"] = "markup.underline",
        ["markup.heading"] = "markup.heading",
        ["markup.quote"] = "markup.quote",
        ["markup.math"] = "markup.math",
        ["markup.link"] = "markup.underline.link",
        ["markup.link.label"] = "string.other.link.title",
        ["markup.link.url"] = "markup.underline.link",
        ["markup.raw"] = "markup.inline.raw.string",
        ["markup.raw.block"] = "markup.fenced_code.block",
        ["markup.list"] = "punctuation.definition.list.begin",
        ["markup.list.checked"] = "markup.checked",
        ["markup.list.unchecked"] = "markup.unchecked",
        ["diff.plus"] = "markup.inserted",
        ["diff.minus"] = "markup.deleted",
        ["diff.delta"] = "markup.changed",
        ["tag"] = "entity.name.tag",
        ["tag.builtin"] = "entity.name.tag",
        ["tag.attribute"] = "entity.other.attribute-name",
        ["tag.delimiter"] = "punctuation.definition.tag",
        ["error"] = "invalid.illegal",
    };

    /// <summary>
    /// Per-grammar deviations, where the language's own VS Code TextMate grammar uses a different scope for the construct, so that
    /// switching engines does not recolour it (C#'s <c>int</c> is <c>keyword.type</c>, Python decorators are
    /// <c>entity.name.function.decorator</c>, TypeScript's <c>${</c> is <c>punctuation.definition.template-expression</c>, …).
    /// </summary>
    private static readonly Dictionary<string, Dictionary<string, string>> Overrides = new(StringComparer.Ordinal)
    {
        ["c_sharp"] = new(StringComparer.Ordinal)
        {
            ["type.builtin"] = "keyword.type",
            ["attribute"] = "entity.name.type",
            ["keyword.modifier"] = "storage.modifier",
            ["keyword.type"] = "storage.type",
            ["constructor"] = "entity.name.function",
            ["variable.member"] = "entity.name.variable.field",
            ["module"] = "entity.name.type.namespace",
        },
        ["python"] = new(StringComparer.Ordinal)
        {
            ["attribute"] = "entity.name.function.decorator",
            ["attribute.builtin"] = "entity.name.function.decorator",
            ["constructor"] = "entity.name.type.class",
            ["string.documentation"] = "string.quoted.docstring.multi",
        },
        ["typescript"] = EcmaOverrides(),
        ["tsx"] = EcmaOverrides(),
        ["javascript"] = EcmaOverrides(),
        ["rust"] = new(StringComparer.Ordinal)
        {
            ["type.builtin"] = "entity.name.type.primitive",
            ["attribute"] = "meta.attribute",
            ["function.macro"] = "entity.name.function.macro",
            ["module"] = "entity.name.namespace",
            ["keyword.modifier"] = "storage.modifier",
        },
        ["cpp"] = new(StringComparer.Ordinal)
        {
            ["type.builtin"] = "storage.type.built-in.primitive",
        },
        ["c"] = new(StringComparer.Ordinal)
        {
            ["type.builtin"] = "storage.type.built-in.primitive",
        },
        ["css"] = new(StringComparer.Ordinal)
        {
            ["property"] = "support.type.property-name",
            ["tag"] = "entity.name.tag",
            ["type"] = "entity.other.attribute-name.class",
            ["number"] = "constant.numeric",
            ["string"] = "string.quoted",
        },
    };

    private static Dictionary<string, string> EcmaOverrides() => new(StringComparer.Ordinal)
    {
        ["punctuation.special"] = "punctuation.definition.template-expression",
        ["type.builtin"] = "support.type.primitive",
        ["keyword.function"] = "storage.type.function",
        ["keyword.type"] = "storage.type",
        ["constructor"] = "entity.name.type",
        ["variable.member"] = "variable.other.property",
        ["keyword"] = "storage.type",
    };

    /// <summary>
    /// The TextMate scope for <paramref name="capture"/> (no leading '@') in grammar <paramref name="grammar"/>, with <paramref name="suffix"/>
    /// appended (e.g. "rust"); <see cref="Skip"/> for non-highlight captures; null when no prefix of the name is known (left unstyled).
    /// </summary>
    public static string? ScopeFor(string capture, string grammar, string? suffix)
    {
        if (capture.Length == 0 || capture[0] == '_' || capture is "spell" or "nospell" or "conceal" or "conceal_lines") return Skip;
        if (capture == "none") return None;
        Overrides.TryGetValue(grammar, out var own);
        var name = capture;
        while (true)
        {
            if ((own is not null && own.TryGetValue(name, out var scope)) || Default.TryGetValue(name, out scope))
            {
                if (name.StartsWith("markup.heading", StringComparison.Ordinal) && capture.Length > name.Length)
                    scope += capture[name.Length..]; // markup.heading.1 → markup.heading.1.<suffix> (themes key off markup.heading)
                return string.IsNullOrEmpty(suffix) ? scope : scope + "." + suffix;
            }
            var dot = name.LastIndexOf('.');
            if (dot < 0) return null;
            name = name[..dot];
        }
    }

    /// <summary>Every capture name the default table knows (for the coverage test and the evaluation document).</summary>
    public static IReadOnlyCollection<string> KnownCaptures => Default.Keys;
}
