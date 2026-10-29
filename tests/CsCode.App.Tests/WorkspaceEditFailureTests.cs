using System.Diagnostics;
using Avalonia.Headless.XUnit;
using CsCode.App.Tests.Infrastructure;
using CsCode.Core.Languages;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CsCode.App.Tests;

/// <summary>A workspace edit that fails reports it with an error toast and returns at once: callers such as Replace All apply files one by one.</summary>
public class WorkspaceEditFailureTests
{
    [AvaloniaFact(Timeout = 120_000)]
    public async Task A_failed_workspace_edit_returns_false_without_waiting_for_its_error_toast()
    {
        using var env = new RealAppHome();
        var file = env.Workspace.WriteFile("locked.cs", "old\n");
        File.SetAttributes(file, FileAttributes.ReadOnly);
        try
        {
            await using var run = new RealAppRun(env);
            await run.MainWindowAsync();
            var nav = run.Services.GetRequiredService<INavigationService>();

            // The error toast has no actions, so it only completes when it is dismissed or demoted (20 s), or never once newer toasts push it off
            // the stack. Waiting for it held up every following file of a Replace All.
            var clock = Stopwatch.StartNew();
            var applied = await nav.ApplyWorkspaceEditAsync(new WorkspaceEdit(new Dictionary<Uri, IReadOnlyList<TextEdit>>
            {
                [new Uri(file)] = [new TextEdit(TextRange.FromLines(0, 0, 0, 3), "new")],
            }));
            clock.Stop();

            Assert.False(applied);
            Assert.InRange(clock.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(5));
            Assert.Equal("old\n", File.ReadAllText(file));
        }
        finally
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
    }
}
