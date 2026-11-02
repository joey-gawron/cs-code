using CsCode.Core.Languages;
using CsCode.Core.Text;
using CsCode.Lsp.Extensions;

namespace CsCode.Lsp.CSharp;

/// <summary>
/// What CsCode adds for Microsoft's Roslyn language server (the per-server extension pattern, docs/LSP.md §7a): Roslyn's own notifications
/// (<c>workspace/projectInitializationComplete</c>, <c>window/_roslyn_showToast</c>), opening the solution after <c>initialized</c>
/// (<see cref="RoslynSession"/>, the per-server state), the client-side commands its answers name (<see cref="RoslynCommands"/>), and the
/// source actions it lacks, organize usings and fix all, built from its own actions (<see cref="RoslynSourceActions"/>). Library and
/// generated documents are not an extension concern: <see cref="ExternalDocuments"/> maps every server's locations.
/// </summary>
public sealed class RoslynExtension : LanguageServerExtension
{
    public override string Name => "Roslyn";

    public override bool AppliesTo(LanguageServerDefinition definition) => definition.Id == CSharpServers.RoslynId;

    public override void Register(ServerExtensionHost host)
    {
        var sourceActions = new RoslynSourceActions(host, this);
        if (RoslynCommands.Register(host, this, sourceActions))
            host.Disposables.Add(host.Services.Registry.Register<ICodeActionProvider>(DocumentSelector.ForLanguages("csharp"), sourceActions));
    }

    public override void Attach(ServerExtensionSession session)
    {
        var roslyn = new RoslynSession(session);
        session.State = roslyn;
        session.OnNotification(RoslynSession.ProjectInitializationComplete, _ => roslyn.OnProjectInitializationComplete());
        session.OnNotification(RoslynSession.ShowToast, roslyn.OnShowToast);
    }

    public override void OnInitialized(ServerExtensionSession session)
    {
        if (session.State is RoslynSession roslyn) roslyn.OnInitialized();
    }
}
