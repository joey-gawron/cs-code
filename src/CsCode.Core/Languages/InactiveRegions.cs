using CsCode.Core.Lifecycle;
using CsCode.Core.Text;

namespace CsCode.Core.Languages;

/// <summary>
/// Code the preprocessor leaves out (<c>#if 0</c>, an <c>#ifdef</c> whose macro is not defined), which the editor draws faded. A language server
/// pushes it per document after each parse (clangd's <c>textDocument/inactiveRegions</c>), so the provider keeps the latest regions of each
/// document and says when they change; the editor asks again then.
/// </summary>
public interface IInactiveRegionsProvider : ILanguageProvider
{
    /// <summary>The inactive regions of <paramref name="document"/> as last reported (empty when there are none or none were reported).</summary>
    IReadOnlyList<TextRange> GetInactiveRegions(Uri document);

    /// <summary>Fires, on any thread, with the document whose regions changed.</summary>
    IEventSource<Uri> Changed { get; }
}
