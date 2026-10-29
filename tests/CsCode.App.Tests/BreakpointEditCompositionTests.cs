using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using CsCode.Core.Debugging;
using CsCode.Core.Text;
using CsCode.Editor;
using CsCode.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CsCode.App.Tests;

/// <summary>
/// Editor and debugger over the REAL composition root (every real module, CSCODE_HOME in a temp dir): breakpoints kept by the real DAP
/// BreakpointManager must follow edits made in a real editor view. The editor computes each shift (BreakpointShift) and the DAP module applies
/// it (ApplyLineShift); each module's own tests use a fake of the other side, so only this test proves that the two agree.
/// </summary>
public class BreakpointEditCompositionTests
{
    [AvaloniaFact(Timeout = 60_000)]
    public async Task Breakpoints_stay_on_their_code_while_lines_are_inserted_and_deleted_in_a_real_editor_view()
    {
        using var home = new TempDirectory("cscode-bp-edits-");
        var previousHome = Environment.GetEnvironmentVariable("CSCODE_HOME");
        Environment.SetEnvironmentVariable("CSCODE_HOME", home.Path); // never the user's real config/state directories
        try
        {
            await using var sp = AppServices.Build([]);
            var factory = sp.GetRequiredService<IEditorViewFactory>();
            var breakpoints = sp.GetRequiredService<IDebugService>().Breakpoints;
            var path = home.WriteFile("work/Program.cs", string.Join('\n', Enumerable.Range(0, 10).Select(i => "code" + i)) + "\n");
            var doc = await factory.OpenDocumentAsync(new Uri(path));
            var view = factory.CreateView(doc);
            var window = new Window { Width = 600, Height = 400, Content = view };
            using var closeWindow = CsCode.Core.Lifecycle.Disposable.Create(() => { window.Close(); factory.DisposeView(view); });
            window.Show();
            Dispatcher.UIThread.RunJobs();

            // A breakpoint on every line: each must stay with the text it was set on, or vanish with it.
            var setOn = Enumerable.Range(0, 10).ToDictionary(line => breakpoints.Add(doc.Uri, line).Id, line => "code" + line);
            Dispatcher.UIThread.RunJobs(); // the view learns about them on its next turn

            doc.ApplyEdits([TextEdit.Insert(new TextPosition(2, 0), "inserted\n")]); // a whole line above code2: code2.. move down
            Dispatcher.UIThread.RunJobs();
            doc.ApplyEdits([TextEdit.Insert(new TextPosition(6, 5), "\n")]);         // Enter at the end of code5: code6.. move down
            Dispatcher.UIThread.RunJobs();
            doc.ApplyEdits([TextEdit.Delete(TextRange.FromLines(8, 5, 10, 5))]);     // from the end of code6 to the end of code8: code7, code8 vanish
            Dispatcher.UIThread.RunJobs();
            doc.ApplyEdits([TextEdit.Delete(TextRange.FromLines(0, 0, 1, 0))]);      // the whole first line: code0 vanishes, everything moves up
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(["code1", "inserted", "code2", "code3", "code4", "code5", "", "code6", "code9"], Enumerable.Range(0, 9).Select(doc.GetLineText));
            var placed = breakpoints.GetForUri(doc.Uri).Select(b => (SetOn: setOn[b.Id], Now: doc.GetLineText(b.Line))).OrderBy(p => p.SetOn).ToArray();
            Assert.Equal(["code1", "code2", "code3", "code4", "code5", "code6", "code9"], placed.Select(p => p.SetOn));
            Assert.All(placed, p => Assert.Equal(p.SetOn, p.Now));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CSCODE_HOME", previousHome);
        }
    }
}
