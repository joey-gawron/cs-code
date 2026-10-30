using Xunit;

namespace CsCode.Vim.Tests;

/// <summary>Tests that assert time budgets must not share the process with other running tests.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SerialCollection
{
    public const string Name = "Serial";
}
