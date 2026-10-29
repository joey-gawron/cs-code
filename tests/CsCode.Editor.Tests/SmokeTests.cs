using Avalonia.Headless.XUnit;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CsCode.Editor.Tests;

public class SmokeTests
{
    [AvaloniaFact]
    public void Editor_module_registers_a_view_factory()
    {
        var services = new ServiceCollection();
        CsCode.Editor.ServiceCollectionExtensions.AddCsCodeEditor(services);
        using var sp = services.BuildServiceProvider();
        Assert.NotNull(sp.GetService<IEditorViewFactory>());
    }
}
