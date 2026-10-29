using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CsCode.Panels.Search.Tests;

public class SmokeTests
{
    [Fact]
    public void Module_registers_without_throwing()
    {
        var services = new ServiceCollection();
        CsCode.Panels.Search.ServiceCollectionExtensions.AddCsCodeSearchPanels(services);
    }
}
