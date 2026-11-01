# Language navigation UIs (call hierarchy, type hierarchy, peek, linked editing)

The language client has served `callHierarchy/*`, `typeHierarchy/*` and `textDocument/linkedEditingRange` since the first LSP wave (docs/LSP.md
§6, locally and over remote: `CsCode.Remote` bridges the same Core interfaces), and Go to Definition / Find All References for longer. This
document is the map of the UIs over them; the editor's parts are described in docs/EDITOR.md.

| Feature | Where | Over | Doc |
|---|---|---|---|
| Call hierarchy, type hierarchy | `CsCode.Panels.Navigation` (the **Hierarchy** tool window, its commands and right-click items) | `ICallHierarchyProvider`, `ITypeHierarchyProvider` | below |
| Peek Definition / References / Declaration / Type Definition / Implementations | `CsCode.Editor/Features/PeekFeature.cs` | `IDefinitionProvider`, `IReferenceProvider` | docs/EDITOR.md §7c |
| Linked editing | `CsCode.Editor/Features/LinkedEditingFeature.cs` | `ILinkedEditingRangeProvider` | docs/EDITOR.md §7d |

`CsCode.Panels.Navigation` references Core and Ui only (like the other panels); the editor publishes the provider context keys its items read.

```csharp
services.AddCsCodeNavigationPanels();   // HierarchyHost + the "hierarchy" tool window (AppServices does this)
```

## Keys, commands, menus

| What | Visual Studio keymap (default) | VS Code keymap | Command | Menu |
|---|---|---|---|---|
| View Call Hierarchy (the direction used last, callers at first) | Ctrl+K, Ctrl+T (⌘K ⌘T) | Shift+Alt+H (⇧⌥H) | `editor.showCallHierarchy` | editor right-click: View Call Hierarchy |
| Callers / callees of the symbol at the caret | — | — | `editor.showIncomingCalls` / `editor.showOutgoingCalls` | Command Palette |
| View Type Hierarchy (subtypes at first) | — | — | `editor.showTypeHierarchy` | editor right-click: View Type Hierarchy |
| Supertypes / subtypes | — | — | `editor.showSupertypes` / `editor.showSubtypes` | Command Palette |
| Show the window | — | — | `cscode.hierarchy.focus` | View > Tool Windows > Hierarchy |
| Flip the direction, refresh, clear | — | — | `cscode.hierarchy.toggleDirection`, `cscode.hierarchy.refresh`, `cscode.hierarchy.clear` | the window's toolbar |
| Peek Definition | Alt+F12 (⌥F12) | Alt+F12 (⌥F12) | `editor.action.peekDefinition` | editor right-click: Peek Definition |
| Peek References | — (Shift+F12 is Find All References) | Shift+F12 (Find All References: Shift+Alt+F12) | `editor.action.referenceSearch.trigger` | Command Palette |
| Peek Declaration / Type Definition / Implementations | — | — | `editor.action.peekDeclaration` / `peekTypeDefinition` / `peekImplementation` | Command Palette |
| Start Linked Editing (ends it when on) | Ctrl+Shift+F2 (⇧⌘F2) | Ctrl+Shift+F2 (⇧⌘F2) | `editor.action.linkedEditing` | Command Palette |

The keys are in the keymap presets (`src/CsCode.Platform/Resources/Keymaps`), each Ctrl key with its Mac key. The call hierarchy key is bound with
`editorHasCallHierarchyProvider`, and the right-click items show while VS Code's `editorHasCallHierarchyProvider` / `editorHasTypeHierarchyProvider`
are true (the editor sets them with its other provider keys, from the registry, for the focused document; never for a large file). With Vim on
(the default) Ctrl+K, Alt+F12 and Ctrl+Shift+F2 are the shell's (VimKeyPolicy), and Vim's Esc closes a peek.

## The Hierarchy window

One tool window (`hierarchy`, docked at the bottom like Visual Studio's Call Hierarchy) for both hierarchies: a toolbar (direction toggle whose
tooltip says what it shows next, Refresh, Collapse All, Clear), a header in Visual Studio's wording ("Calls To 'Run'", "Calls From 'Run'") or VS
Code's for types ("Supertypes of 'Shape'", "Subtypes of 'Shape'"), and the tree as a virtualised list of its visible rows (chevron, symbol kind
icon, name, then the server's detail and `File.cs:line`, "(3 calls)" when a caller calls it several times).

* **Query.** A command takes the active editor's document and caret from `INavigationService`, asks the document's providers to prepare the
  symbol there — off the UI thread, 5 s each, the first provider that answers with items wins — and shows the items as roots, the first expanded
  and selected; the window is shown through `IViewActivator`. No editor, no provider for the file, no symbol at the caret: the status bar says
  which, and the window stays as it was.
* **Lazy tree.** A node asks for its children the first time it is expanded, and keeps them (collapse and expand again asks nothing). The
  children come from the SAME provider that prepared the roots, with the item exactly as the server made it (its opaque `data` goes back), as the
  protocol requires. Each request runs off the UI thread with a 10 s deadline; a node whose provider failed or timed out says so in its row and
  has no children (Refresh asks again). While a node loads its chevron is a spinner. At most 1000 children per node are kept.
* **Stale answers.** A new query, a direction change, a refresh or Clear starts a new generation; an answer for an older one is dropped, so a slow
  server can never put children under a tree that was replaced.
* **Call sites.** An incoming call's sites are in the caller (`fromRanges` in the caller's document); an outgoing call's sites are in the node being
  expanded (the caller), not in the callee — VS Code's reading of the protocol. Selecting a row (mouse or arrows) shows its first call site, else
  the symbol's name, in a preview tab with the keyboard left in the window; Enter or a double-click opens it and moves the keyboard to the editor.
  The row menu has Go To Call Site, Go To Definition (the symbol itself), Show Call Hierarchy / Show Type Hierarchy (makes that row the root, same
  provider and direction), Expand / Collapse, Copy (the name), Refresh. Right / Left expand and collapse; Left on a collapsed row goes to its parent.
* **Refresh** prepares again at the query's position while its document is open (the symbol may have changed), else asks the same roots again,
  and expands again every node that was expanded (by its path of names and places from the root).
* **Direction.** The toggle (and `cscode.hierarchy.toggleDirection`) shows the same roots the other way; `editor.showCallHierarchy` and
  `editor.showTypeHierarchy` reuse the direction last shown.
* **Lifetime.** The tree lives in `HierarchyHost.Model` (a singleton: a command fills it while the window is hidden); it holds the query's URI,
  language and position, not the document, and the provider until the next query or Clear. The view only subscribes; the shell disposes it when
  the window hides, and a closed view is collectable (`HierarchyViewTests`).

Threading: model and view on the UI thread; every provider call goes through `Task.Run` with a deadline (the LSP providers then pay one enqueue
on the language lane, docs/LSP.md §3).

## Tests

`dotnet test tests/CsCode.Panels.Navigation.Tests` (headless Avalonia, a call graph and a type graph served like a language server: opaque data
that must come back, recorded threads, gates for slow answers):

* `HierarchyHostTests`: the command prepares at the caret and shows the callers, lazy expansion asked once with the server's item, incoming and
  outgoing call sites, the direction used last, the type hierarchy both ways, the status messages, providers off the UI thread, a stale answer
  dropped, a provider that never answers, Refresh (open and closed document, expanded nodes kept), Collapse All / Clear / re-root, the commands
  and right-click items registered and withdrawn.
* `HierarchyViewTests`: selecting a row previews its call site keeping the keyboard and Enter opens it, Right / Left, the direction button, the
  row menu, a closed view collectable while the model keeps its tree.

Elsewhere: `HierarchyContextKeyTests` (Editor: the provider keys follow the registry), `RealApp/LanguageNavigationTests` (App: Ctrl+K Ctrl+T
through the real keyboard pipeline with Vim on, the window, a row selecting the call site), `RealAppContextMenuTests` (the item and its key),
`KeybindingServiceTests` (Platform: the keys of both presets on Linux and macOS).

## Not done

* Call sites are not highlighted in the editor while a call node is selected (VS Code paints every `fromRange`); the window shows the first one.
* No history of earlier queries (VS Code's references view keeps a list); Refresh and re-rooting replace the tree.
* Peek and linked editing: docs/EDITOR.md §8.
