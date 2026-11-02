using CsCode.App.DotNet;
using CsCode.App.Tests.Infrastructure;
using CsCode.Core.Platform;
using CsCode.Lsp.Manager;
using CsCode.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CsCode.App.Tests.DotNet;

/// <summary>C#: Select Language Server… (<c>csharp.selectLanguageServer</c>) and <c>cscode.lsp.serverChoice</c>, which other groups share.</summary>
public class DotNetServerChoiceTests
{
    [Fact]
    public async Task Select_Language_Server_writes_the_csharp_choice_and_keeps_the_other_groups_choices()
    {
        var config = new TestConfigurationService();
        config.Set("cscode.lsp.serverChoice", new Dictionary<string, string> { ["python"] = "python" });
        var window = new RecordingWindowService();
        var commands = new TestCommandService();
        await using var sp = new ServiceCollection()
            .AddSingleton<ICommandService>(commands)
            .AddSingleton<IWindowService>(window)
            .AddSingleton<IConfigurationService>(config)
            .BuildServiceProvider();
        using var dotnet = DotNetCommands.TryCreate(sp)!;
        dotnet.Start();

        window.QuickPickResponses.Enqueue(1); // csharp-ls (Roslyn, the default, is first)
        await commands.ExecuteAsync(DotNetCommands.SelectLanguageServer);
        var choice = BuiltinLanguageServers.ReadChoice(config.GetRaw("cscode.lsp.serverChoice"));
        Assert.Equal(("csharp", "python"), (choice["csharp"], choice["python"]));
        Assert.Contains(window.StatusMessages, m => m.Contains("csharp-ls", StringComparison.Ordinal));

        window.QuickPickResponses.Enqueue(0); // back to Roslyn
        await commands.ExecuteAsync(DotNetCommands.SelectLanguageServer);
        choice = BuiltinLanguageServers.ReadChoice(config.GetRaw("cscode.lsp.serverChoice"));
        Assert.Equal(("roslyn", "python"), (choice["csharp"], choice["python"]));
    }
}
