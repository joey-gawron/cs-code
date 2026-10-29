using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CsCode.Platform.Tests;

public class SmokeTests
{
    [Fact]
    public void Module_registers_without_throwing()
    {
        var services = new ServiceCollection();
        CsCode.Platform.ServiceCollectionExtensions.AddCsCodePlatform(services);
    }
}
