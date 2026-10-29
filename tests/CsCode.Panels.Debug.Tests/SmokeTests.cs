using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CsCode.Panels.Debug.Tests;

public class SmokeTests
{
    [Fact]
    public void Module_registers_without_throwing()
    {
        var services = new ServiceCollection();
        CsCode.Panels.Debug.ServiceCollectionExtensions.AddCsCodeDebugPanels(services);
    }
}
