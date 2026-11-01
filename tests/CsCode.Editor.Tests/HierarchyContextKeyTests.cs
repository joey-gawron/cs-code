using Avalonia.Headless.XUnit;
using CsCode.Core.Languages;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Editor.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CsCode.Editor.Tests;

/// <summary>The keys the Hierarchy window's right-click items and key bindings read (docs/NAVIGATION.md).</summary>
public class HierarchyContextKeyTests
{
    private sealed class Calls : ICallHierarchyProvider
    {
        public ValueTask<IReadOnlyList<HierarchyItem>?> PrepareCallHierarchyAsync(ITextDocument document, TextPosition position, CancellationToken ct) => new((IReadOnlyList<HierarchyItem>?)null);
        public ValueTask<IReadOnlyList<CallHierarchyIncomingCall>?> IncomingCallsAsync(HierarchyItem item, CancellationToken ct) => new((IReadOnlyList<CallHierarchyIncomingCall>?)null);
        public ValueTask<IReadOnlyList<CallHierarchyOutgoingCall>?> OutgoingCallsAsync(HierarchyItem item, CancellationToken ct) => new((IReadOnlyList<CallHierarchyOutgoingCall>?)null);
    }

    private sealed class Types : ITypeHierarchyProvider
    {
        public ValueTask<IReadOnlyList<HierarchyItem>?> PrepareTypeHierarchyAsync(ITextDocument document, TextPosition position, CancellationToken ct) => new((IReadOnlyList<HierarchyItem>?)null);
        public ValueTask<IReadOnlyList<HierarchyItem>?> SupertypesAsync(HierarchyItem item, CancellationToken ct) => new((IReadOnlyList<HierarchyItem>?)null);
        public ValueTask<IReadOnlyList<HierarchyItem>?> SubtypesAsync(HierarchyItem item, CancellationToken ct) => new((IReadOnlyList<HierarchyItem>?)null);
    }

    [AvaloniaFact]
    public async Task The_call_and_type_hierarchy_keys_follow_the_providers_of_the_focused_document()
    {
        using var host = ViewHost.Create();
        var keys = host.Provider.GetRequiredService<IContextKeyService>();
        await host.OpenAsync("class A {}\n");
        Assert.Equal(false, keys.Get("editorHasCallHierarchyProvider"));
        Assert.Equal(false, keys.Get("editorHasTypeHierarchyProvider"));
        using (host.Factory.Services.Features.Register<ICallHierarchyProvider>(DocumentSelector.All, new Calls()))
        {
            await ViewHost.WaitFor(() => keys.Get("editorHasCallHierarchyProvider") is true, "call hierarchy key");
            Assert.Equal(false, keys.Get("editorHasTypeHierarchyProvider"));
            using (host.Factory.Services.Features.Register<ITypeHierarchyProvider>(DocumentSelector.All, new Types()))
                await ViewHost.WaitFor(() => keys.Get("editorHasTypeHierarchyProvider") is true, "type hierarchy key");
            await ViewHost.WaitFor(() => keys.Get("editorHasTypeHierarchyProvider") is false, "type hierarchy provider gone");
        }
        await ViewHost.WaitFor(() => keys.Get("editorHasCallHierarchyProvider") is false, "call hierarchy provider gone");
    }
}
