using Xunit;

namespace CsCode.MemoryTests;

public class SmokeTests
{
    [Fact]
    public void Gc_is_workstation_concurrent() =>
        Assert.False(System.Runtime.GCSettings.IsServerGC);
}
