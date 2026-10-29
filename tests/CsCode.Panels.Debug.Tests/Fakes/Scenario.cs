using Avalonia.Controls;
using CsCode.Core.Debugging;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Ui;
using CsCode.Ui.Threading;
using Microsoft.Extensions.DependencyInjection;

namespace CsCode.Panels.Debug.Tests.Fakes;

/// <summary>A realistic stopped debuggee for screenshots and UI tests, wired through the real DI registration of the panels.</summary>
public sealed class Scenario : IDisposable
{
    public PanelRig Rig { get; }
    public ServiceProvider Provider { get; }
    public IReadOnlyDictionary<string, IToolWindow> Tools { get; }
    public FakeDebugSession Session => Rig.Session;

    public Scenario(bool stopped = true, int frame = 0, Action<IServiceCollection>? configure = null)
    {
        var session = new FakeDebugSession("s1", "Sample (netcoredbg)");
        ConfigureSession(session);
        Rig = new PanelRig(withSession: true, session: session);
        var services = new ServiceCollection();
        services.AddSingleton<IDebugService>(Rig.Debug);
        services.AddSingleton<IDebugFrameFocus>(Rig.Debug);
        services.AddSingleton<IDebugOutputHistory>(Rig.Debug);
        services.AddSingleton<INavigationService>(Rig.Navigation);
        services.AddSingleton<IWindowService>(Rig.Window);
        services.AddSingleton<IUiDispatcher>(Rig.Ui);
        services.AddSingleton<IStateStorage>(Rig.State);
        services.AddSingleton<IDocumentManager>(Rig.Documents);
        services.AddSingleton<IContextKeyService>(Rig.ContextKeys);
        services.AddSingleton<ICommandService, CsCode.TestDoubles.TestCommandService>();
        CsCode.Panels.Debug.ServiceCollectionExtensions.AddCsCodeDebugPanels(services);
        configure?.Invoke(services);
        Provider = services.BuildServiceProvider();
        Tools = Provider.GetServices<IToolWindow>().ToDictionary(t => t.Id);

        Rig.Debug.FakeBreakpoints.SetExceptionFilters([new ExceptionFilterSetting("all", "All Exceptions", false), new ExceptionFilterSetting("user-unhandled", "User-Unhandled Exceptions", true)]);
        var program = new Uri("file:///work/Sample/Program.cs");
        var verified = Rig.Debug.FakeBreakpoints.Add(program, 9);
        var conditional = Rig.Debug.FakeBreakpoints.Add(program, 16, condition: "n > 3");
        var moved = Rig.Debug.FakeBreakpoints.Add(program, 21, hitCondition: ">= 5");
        var failed = Rig.Debug.FakeBreakpoints.Add(new Uri("file:///work/Sample/Util/Parser.cs"), 41, logMessage: "parsed {text}");
        var unverified = Rig.Debug.FakeBreakpoints.Add(new Uri("file:///work/Sample/Util/Parser.cs"), 58);
        var disabled = Rig.Debug.FakeBreakpoints.Add(new Uri("file:///work/Sample/Util/Parser.cs"), 70);
        Rig.Debug.FakeBreakpoints.Update(disabled with { Enabled = false });
        Rig.Debug.FakeBreakpoints.AddFunction("Sample.Program.Outer", "n > 0");
        Rig.Debug.FakeBreakpoints.ReportStatus(new DebugBreakpointStatus(session, verified.Id, true, 9, null));
        Rig.Debug.FakeBreakpoints.ReportStatus(new DebugBreakpointStatus(session, conditional.Id, true, 16, null));
        Rig.Debug.FakeBreakpoints.ReportStatus(new DebugBreakpointStatus(session, moved.Id, true, 23, null));
        Rig.Debug.FakeBreakpoints.ReportStatus(new DebugBreakpointStatus(session, failed.Id, false, null, "No executable code at this line"));
        Rig.Debug.FakeBreakpoints.ReportStatus(new DebugBreakpointStatus(session, unverified.Id, false, null, null));

        if (stopped) Rig.StopAt(frame);
    }

    public Control View(string id) => Tools[id].CreateView(Provider);

    public static void ConfigureSession(FakeDebugSession s)
    {
        s.Threads.Clear();
        s.Threads.AddRange([new DebugThread(1, "Main Thread"), new DebugThread(2, ".NET ThreadPool Worker"), new DebugThread(3, ".NET Timer")]);
        var program = new DebugSource("Program.cs", "/work/Sample/Program.cs");
        s.Frames[1] =
        [
            new DebugStackFrame(100, "Sample.dll!Sample.Program.Add(int a, int b)", program, 9, 0),
            new DebugStackFrame(101, "Sample.dll!Sample.Program.Outer(int n)", program, 15, 0),
            new DebugStackFrame(102, "Sample.dll!Sample.Program.Main()", program, 24, 0),
            new DebugStackFrame(103, "System.Private.CoreLib.dll!System.Threading.ExecutionContext.RunInternal", null, 0, 0),
            new DebugStackFrame(104, "System.Private.CoreLib.dll!System.Threading.Thread.StartCallback", null, 0, 0, PresentationHint: "subtle"),
        ];
        s.Frames[2] = [new DebugStackFrame(200, "Sample.dll!Sample.Worker.Run()", new DebugSource("Worker.cs", "/work/Sample/Worker.cs"), 31, 0)];
        s.Frames[3] = [new DebugStackFrame(300, "System.Private.CoreLib.dll!System.Threading.TimerQueue.FireNextTimers", null, 0, 0)];
        s.Scopes[100] = [new DebugScope("Locals", 1000, false)];
        s.Scopes[101] = [new DebugScope("Locals", 1100, false)];
        s.Scopes[102] = [new DebugScope("Locals", 1200, false)];
        s.Variables[1000] =
        [
            new DebugVariable("a", "41", "int", EvaluateName: "a"),
            new DebugVariable("b", "1", "int", EvaluateName: "b"),
            new DebugVariable("sum", "42", "int", EvaluateName: "sum"),
        ];
        s.Variables[1100] = [new DebugVariable("n", "41", "int", EvaluateName: "n"), new DebugVariable("inner", "42", "int", EvaluateName: "inner")];
        s.Variables[1200] =
        [
            new DebugVariable("args", "{string[0]}", "string[]", 3000, "args", null, 0),
            new DebugVariable("x", "41", "int", EvaluateName: "x"),
            new DebugVariable("s", "\"hello world\"", "string", EvaluateName: "s"),
            new DebugVariable("pi", "3.14159", "double", EvaluateName: "pi"),
            new DebugVariable("done", "false", "bool", EvaluateName: "done"),
            new DebugVariable("nothing", "null", "string", EvaluateName: "nothing"),
            new DebugVariable("item", "{Sample.Item}", "Sample.Item", 2000, "item", 3, 0),
            new DebugVariable("numbers", "Count = 250", "System.Collections.Generic.List<int>", 3100, "numbers", 2, 250),
        ];
        s.Variables[2000] =
        [
            new DebugVariable("Name", "\"widget\"", "string", EvaluateName: "item.Name"),
            new DebugVariable("Count", "3", "int", EvaluateName: "item.Count"),
            new DebugVariable("Tags", "{string[2]}", "string[]", 3200, "item.Tags", null, 2),
        ];
        s.Variables[3000] = [];
        s.Variables[3100] = Enumerable.Range(0, 250).Select(i => new DebugVariable($"[{i}]", (i * 3).ToString(), "int", EvaluateName: $"numbers[{i}]")).ToList();
        s.Variables[3200] = [new DebugVariable("[0]", "\"red\"", "string"), new DebugVariable("[1]", "\"round\"", "string")];
        s.Evaluator = (e, _, ctx) => e switch
        {
            "x + 1" => new DebugEvaluateResult("42", "int"),
            "item" => new DebugEvaluateResult("{Sample.Item}", "Sample.Item", 2000, 3, 0),
            "s.Length" => new DebugEvaluateResult("11", "int"),
            "numbers.Count * 2" => new DebugEvaluateResult("500", "int"),
            "nope" => throw new InvalidOperationException("error CS0103: The name 'nope' does not exist in the current context"),
            _ => new DebugEvaluateResult(ctx == "repl" ? "42" : "?", "int"),
        };
        s.CompletionHandler = (text, column) => [new DebugCompletionItem("Name", "Name", "property", 5, 0), new DebugCompletionItem("Count", "Count", "property", 5, 0), new DebugCompletionItem("Tags", "Tags", "property", 5, 0)];
    }

    public void Dispose() => Provider.Dispose();
}
