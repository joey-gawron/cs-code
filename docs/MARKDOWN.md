# Markdown preview

A rendered, live view of a Markdown file next to (or in place of) its editor, as VS Code's built-in preview: CommonMark plus GitHub's extensions,
code blocks in the editor's own colours, scroll sync with the editor by source line, links into the editor or the browser. There is no web view
in the stack, so the preview is native: Markdig parses, and the syntax tree is rendered to Avalonia controls.

* `src/CsCode.Editor/Markdown/Preview`: the preview itself (Editor module; Avalonia, no shell types):
  `MarkdownPreviewParser` (Markdig → `PreviewDocument`, worker lane), `MarkdownBlockRenderer` (one block → controls, UI thread),
  `MarkdownBlockPanel` (the virtualised block list), `MarkdownPreviewView` (live updates, scrolling, theme, settings), `MarkdownImageLoader`,
  `MarkdownPreviewStyle`, `MarkdownPreviewSettings`, `MarkdownPreviewFactory` (registered by `AddCsCodeEditor`).
* `src/CsCode.App/Markdown`: the shell glue: `MarkdownPreviewIntegration` (commands, preview tabs, context key; started by
  `ShellController.Initialize`) and `MarkdownPreviewHost` (a tab's content: scroll coupling with the editor, links, double-click, clipboard).
* Hooks in other files (additive): `IEditorViewController.TopLine` / `Scrolled` / `ScrollToLine` (implemented by `EditorViewController`);
  `CodeHighlighter.HighlightCode`; `MarkdownView.AddColoredRuns` made internal for reuse; the three key bindings at the end of both keymap presets;
  Visual Studio's Cycle Clipboard Ring key (`EditorMenus`) yields Ctrl+Shift+V / Cmd+Shift+V in Markdown files and previews for the VS Code
  keymap; the Visual Studio keymap binds the ring in Markdown files itself, so there the key stays Visual Studio's everywhere.
* Tests: `tests/CsCode.Editor.Tests/MarkdownPreviewParserTests.cs` (parse, lines, anchors, keys, code colour reuse, limits),
  `MarkdownBlockRendererTests.cs` (one test per construct), `MarkdownPreviewViewTests.cs` (the view over the real editor module: live updates,
  incremental rendering, theme, a 6,000-block document, the memory budget, scrolling, anchors, images, a closed file, release on dispose, the
  editor's scroll API), `tests/CsCode.App.Tests/RealApp/MarkdownPreviewTests.cs` (the real app: keys, tabs, side group and focus, Show Source,
  scroll sync both ways, links, Edit > Copy, release when the tab closes).

## Using it

| Action | Command (palette: "Markdown: …") | Visual Studio keymap | VS Code keymap |
|---|---|---|---|
| Open Preview (in the editor's group, as a tab) | `markdown.showPreview` | palette (Ctrl+Shift+V stays Visual Studio's Cycle Clipboard Ring) | **Ctrl+Shift+V** (macOS **Cmd+Shift+V**) |
| Open Preview to the Side (group on the right; the editor keeps the keyboard) | `markdown.showPreviewToSide` | **Ctrl+K V** (macOS **Cmd+K V**) | same |
| Show Source (from a preview: its editor) | `markdown.showSource` | **Ctrl+Shift+V** in a preview | same |

The keys apply when the active editor's language is `markdown` (`editorLangId == 'markdown'`); the commands take an optional file URI argument
(`{ "key": "...", "command": "markdown.showPreview", "args": "file:///..." }`) and otherwise preview the active editor's file. A file that is
not Markdown is not previewed. There is **one preview tab per file** ("Preview README.md"): opening it again activates it. The preview opened in
place takes the keyboard (so the hidden editor no longer receives keys); the side preview leaves it in the editor, as VS Code does. While a
preview is the active tab the context key **`markdownPreviewFocus`** is true.

In the preview:

* **Scrolling** the editor scrolls the preview to the same source line, and scrolling the preview scrolls the editor (each can be turned off).
  The preview opens at the editor's position.
* **Links**: `#heading` scrolls the preview to that heading (GitHub's ids: `## Getting Started` is `#getting-started`); a relative path or a
  `file:` URI opens the file in the editor (`docs/setup.md#L12` at line 12; a `#heading` of the same file scrolls the preview); `http(s)` and
  `mailto` links open in the system browser / mail program; any other scheme asks first (like the terminal's links).
* **Double-click** shows the editor at that line.
* **Text is selectable** per block (paragraph, heading, cell, code block) by dragging; Edit > Copy copies it through the app's clipboard routing
  (`IClipboardTarget`), and Ctrl+C / Cmd+C, which no binding takes outside the editor, reaches the text block's own copy.

## What is rendered

| Markdown | Rendered as |
|---|---|
| Paragraphs, `**bold**`, `*italic*`, `***both***`, `~~strikethrough~~`, `` `code` ``, hard breaks (two spaces / `\`), entities | `SelectableTextBlock` with runs; inline code in the code font on the code background (`textPreformat.foreground`) |
| Headings `#`..`######` | 2 / 1.5 / 1.25 / 1 / 0.875 / 0.85 em, semibold; h1 and h2 with a rule under them (VS Code's sizes) |
| Links `[t](url)`, `<https://…>`, bare `www.` / `https://` text (linkify), reference links | `textLink.foreground` spans, clickable |
| Fenced code (```` ```lang ````) and indented code | code font on `textCodeBlock.background`, coloured by the editor's **TextMate grammar and theme** (the fence's language id, alias or extension: `csharp`, `cs`, `ts`, `py`, ...); scrolls sideways, no wrapping |
| Bullet / ordered / nested lists | `•` `◦` `▪` by depth; ordered lists keep their start number and `.`/`)` |
| Task lists `- [ ]` / `- [x]` | a read-only check box instead of the bullet |
| Block quotes | `textBlockQuote.border` bar on `textBlockQuote.background` |
| GFM tables | a grid with a bold header row, column alignment (`:--`, `:-:`, `--:`), cell borders; scrolls sideways |
| `---` | a rule in `textSeparator.foreground` |
| Images `![alt](src "title")` | local files relative to the Markdown file (through `IFileSystem`, so remote ssh: workspaces work), `data:` base64 URIs; PNG, JPEG, GIF (first frame), BMP, WebP, ICO; downscaled to 2048 px wide; the alt text while loading and when an image cannot be shown |
| Raw HTML blocks | shown as muted text (no HTML engine); inline `<br>` breaks the line, other inline tags are dropped |

**Remote images are off by default** (`cscode.markdown.preview.allowRemoteImages`): an image fetched from a server tells it the file was opened.
Blocked images show their alt text with a tooltip naming the setting. SVG images show their alt text (there is no SVG renderer).

The whole preview follows the colour theme (VS Code ids above, `editor.background` / `editor.foreground` for the page): light, dark,
high-contrast and user themes; a theme change re-renders it and re-colours its code.

## Settings

| Setting | Default | Meaning |
|---|---|---|
| `markdown.preview.fontFamily` | `""` | Text font; empty = the user interface font. Code uses `editor.fontFamily`. |
| `markdown.preview.fontSize` | 14 | Text size in pixels (code is 0.93 of it). |
| `markdown.preview.lineHeight` | 1.6 | Line height relative to the font size. |
| `markdown.preview.breaks` | false | Single line breaks inside paragraphs break the line. |
| `markdown.preview.linkify` | true | URL-like text becomes links. |
| `markdown.preview.scrollPreviewWithEditor` | true | The editor's scrolling scrolls the preview. |
| `markdown.preview.scrollEditorWithPreview` | true | The preview's scrolling scrolls the editor. |
| `markdown.preview.doubleClickToSwitchToEditor` | true | Double-click shows the editor at that line. |
| `cscode.markdown.preview.allowRemoteImages` | false | Fetch `http(s)` images (15 s timeout, 20 MB cap). |
| `cscode.markdown.preview.updateDelayMs` | 300 | Delay between the last change and the update. |

All of them apply live.

## Design

**Parse on the worker, render on the UI thread.** A change of the document restarts a debounce timer (300 ms); then the editor worker
(`EditorWorker`, Background priority, keyed by the view: latest wins, a newer change cancels the running parse) reads the document's immutable
snapshot (`EditorDocument.Snapshot()`), parses it with Markdig and colours the code blocks with `CodeHighlighter.HighlightCode` (the grammar
registry is confined to the worker). The UI thread receives a finished `PreviewDocument` and only builds controls. The Markdig tree is never
mutated after the parse, so handing it to the UI thread is safe.

**Markdig** (BSD-2-Clause, `Directory.Packages.props`, THIRD-PARTY-NOTICES.md) was chosen over extending the hover parser (`MarkdownParser`,
which stays for hovers and completion documentation: small, forgiving, no tables): it is a CommonMark-compliant parser with the GFM extensions
as options, precise source positions (needed for scroll sync), GitHub heading ids, no dependencies, and it parses a 300 KB file in a few
milliseconds. The pipeline: pipe tables, task lists, strikethrough, autolinks (setting), GitHub auto-identifiers, soft breaks as hard
(setting), precise source location.

**Blocks and reuse keys.** The document is a list of top-level blocks, each with the source lines it spans and a key (block type, length, 64-bit
FNV-1a hash of its source text; for a block with a `[` outside code, also a hash of the document's link reference definitions, since a
`[label]` link takes its target from one, so editing, adding or removing a definition rebuilds the blocks that may use it). After an edit, `MarkdownBlockPanel.SetBlocks` keeps the controls and measured heights of every block whose key is
unchanged, so typing in one paragraph rebuilds that paragraph only, and the code colours of an unchanged top-level code block are carried over
from the previous parse instead of re-tokenising it. A theme or settings change rebuilds everything (also when a later update, a keystroke's or
a refresh, supersedes its render before it is shown: the preview stays marked stale until a full rebuild has been applied).

**Virtualisation.** `MarkdownBlockPanel` is a `Panel` inside the preview's `ScrollViewer` that builds controls only for the blocks within one
viewport above and below the visible area (from `EffectiveViewportChanged`) and drops those more than two viewports away. A block never shown
takes an estimated height (its source lines × the line height, headings and code adjusted); once measured, its height is remembered (until the
width changes), so the extent converges as the user scrolls. When blocks above the top change height after being built, the panel reports the
shift and the view moves the scroll offset by it, so the content under the viewport's top stays put.

**Scroll sync** maps positions to source lines both ways: the block holding a line, then linear interpolation inside the block (and through the
blank-line gap between blocks). A scroll to a far-away line lands on estimated heights; the view pins the line to the top for the next layout
passes (until the user scrolls) so it ends exactly there once the blocks around it have their real sizes. Feedback loops are cut at both ends:
the preview's `Scrolled` event reports only scrolls it did not cause itself (its own offset changes are recognised, clamping included), and the
host ignores the editor's scroll event that echoes a scroll the preview requested. The editor side is three additive members of
`IEditorViewController`: `TopLine` (fractional, from AvaloniaEdit's visual lines, so folding and wrapping count), `Scrolled` (hooked to
`TextView.ScrollOffsetChanged` only when someone subscribes) and `ScrollToLine` (sets the scroll offset, leaves the caret).

**The source.** The preview follows the open document of its URI (`IDocumentManager`); when the last editor of the file closes, it reads the file
through `IFileSystem` once (no live updates) and binds to the document again when it reopens (`IDocumentManager.Opened`). The editor it couples
its scrolling with is found by URI in the document well and followed when the well's memory policy destroys and recreates its view.

**Shell.** A preview tab is a `ShellDocument` opened with `DocumentWell.OpenView("markdown.preview:<uri>", ...)`; its content is created by the
tab and disposed by it (the well's view budget may drop an inactive preview's view; it is rebuilt, re-parsed, when shown again). The host clears
Avalonia's `KeyboardNavigation.TabOnceActiveElement` memory that points into it when it is disposed: a Dock container above the tab remembers the
last focused element, which kept a closed preview reachable (found by the release test).

## Memory (docs/MEMORY.md)

Measured by `A_long_document_preview_stays_within_its_memory_budget` (macOS arm64, Debug): a 301 KB document of 8,000 blocks (2,000 sections with
a heading, a paragraph with bold, code and a link, a list and a C# code block) retains **23 MB** with its preview open: about **80 bytes per source
character**, almost all of it Markdig's syntax tree (≈ 13 MB for this file measured on its own: 40,000 nodes) plus the code spans; **20 blocks**
have controls. The test's budget is 120 bytes per character and 150 realised blocks. A typical README (10–50 KB) costs 1–4 MB. A source above
4,000,000 characters is cut there, with a note at the top of the preview. Decoded images are cached per preview (64 entries, downscaled to 2048 px
wide) and disposed with it; an image dropped from the cache is no longer held by the preview (a control still showing it keeps it, and
the GC frees it after that), so scrolling a README with hundreds of images stays within the cache's size. Everything a preview holds goes when its tab closes (release test) or its view is dropped by the well's view budget.

## Known limits

* No HTML rendering (raw HTML shows as text), no SVG images, no math, no Mermaid, no footnotes, YAML front matter is not recognised (it renders
  as CommonMark reads it: a rule and a heading), no emoji shortcodes.
* Text selection is per block; a selection cannot span two paragraphs.
* No find in the preview (Ctrl+F searches the editor).
* Preview tabs are not restored with the layout or a saved session.
* A link to a heading of another Markdown file opens that file in the editor (at its top), not its preview at the heading.
* Absolute paths in links and images (`/docs/x.md`) are file-system absolute, not workspace-root relative as in VS Code.
* The preview of a file whose editor closed shows the file as saved and does not watch it.
* The syntax tree costs about 40 bytes per source character beyond the controls; converting it to a compact model would roughly halve the
  memory of very large previews.

## Running the tests

```bash
export DOTNET_ROOT=$HOME/.dotnet PATH=$HOME/.dotnet:$PATH DOTNET_CLI_TELEMETRY_OPTOUT=1
dotnet test tests/CsCode.Editor.Tests --filter "FullyQualifiedName~MarkdownPreview|FullyQualifiedName~MarkdownBlock"
dotnet test tests/CsCode.App.Tests --filter "FullyQualifiedName~RealApp.MarkdownPreviewTests"
```
