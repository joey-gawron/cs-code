using CsCode.App.Python;
using Xunit;

namespace CsCode.App.Tests;

/// <summary>Language stage 1: what Run Selection sends to the Python REPL and how Run Python File quotes its command line.</summary>
public class PythonIntegrationTests
{
    [Fact]
    public void Code_for_the_REPL_is_dedented_has_no_blank_lines_inside_and_ends_an_indented_block()
    {
        Assert.Equal("x = 1", PythonIntegration.ForRepl("    x = 1\n"));
        Assert.Equal("for i in range(3):\n    print(i)\n", PythonIntegration.ForRepl("    for i in range(3):\n\n        print(i)"));
        Assert.Equal("a = 1\nb = 2", PythonIntegration.ForRepl("a = 1\r\n\r\nb = 2\r\n"));
    }

    [Fact]
    public void The_command_line_quotes_only_what_needs_it()
    {
        if (OperatingSystem.IsWindows()) return;
        Assert.Equal("/v/bin/python /p/main.py", PythonIntegration.CommandLine("/v/bin/python", "/p/main.py"));
        Assert.Equal("/v/bin/python '/my proj/it'\\''s.py'", PythonIntegration.CommandLine("/v/bin/python", "/my proj/it's.py"));
    }
}
