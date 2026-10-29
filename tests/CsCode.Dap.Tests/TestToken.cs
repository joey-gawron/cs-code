global using static CsCode.Dap.Tests.TestToken;
using Xunit;

namespace CsCode.Dap.Tests;

/// <summary>xUnit's per-test cancellation token (analyzer xUnit1051 wants it on every cancellable call).</summary>
public static class TestToken
{
    public static CancellationToken Ct => TestContext.Current.CancellationToken;
}
