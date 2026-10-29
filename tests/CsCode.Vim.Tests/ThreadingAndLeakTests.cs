using System.Runtime.CompilerServices;
using System.Text;
using CsCode.Core.Text;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.Vim.Tests;

/// <summary>Records the managed thread of every callback the engine makes into the host.</summary>
public sealed class ThreadRecordingHost(InMemoryVimHost inner) : IVimHost
{
    public HashSet<int> Threads { get; } = [];
    public int Calls { get; private set; }
    private void Note() { lock (Threads) { Threads.Add(Environment.CurrentManagedThreadId); Calls++; } }

    public IVimBuffer Buffer { get { Note(); return inner.Buffer; } }
    public IVimTimer Timer { get { Note(); return inner.Timer; } }
    public int WrapWidth { get { Note(); return inner.WrapWidth; } }
    public int WindowWidth { get { Note(); return inner.WindowWidth; } }
    public TextPosition GetCaret() { Note(); return inner.GetCaret(); }
    public void SetCaret(TextPosition position, bool insertLike) { Note(); inner.SetCaret(position, insertLike); }
    public void SetSelection(in VimSelection selection) { Note(); inner.SetSelection(selection); }
    public void OnModeChanged(VimMode mode, in VimModeInfo info) { Note(); inner.OnModeChanged(mode, info); }
    public void OnMessage(in VimMessage message) { Note(); inner.OnMessage(message); }
    public void OnShowCommand(string pendingKeys) { Note(); inner.OnShowCommand(pendingKeys); }
    public void OnRecordingChanged(char? register) { Note(); inner.OnRecordingChanged(register); }
    public void OnCommandLineChanged(in VimCommandLine commandLine) { Note(); inner.OnCommandLineChanged(commandLine); }
    public void OnSearchHighlight(VimSearchHighlight? highlight) { Note(); inner.OnSearchHighlight(highlight); }
    public void OnYankHighlight(TextPosition start, TextPosition end, bool linewise) { Note(); inner.OnYankHighlight(start, end, linewise); }
    public VimViewport GetViewport() { Note(); return inner.GetViewport(); }
    public void SetTopLine(int firstLine) { Note(); inner.SetTopLine(firstLine); }
    public int GetDisplayRows(int line) { Note(); return inner.GetDisplayRows(line); }
    public bool TryGetClosedFold(int line, out int startLine, out int endLine) { Note(); return inner.TryGetClosedFold(line, out startLine, out endLine); }
    public void FoldCommand(VimFoldCommand command, int startLine, int endLine) { Note(); inner.FoldCommand(command, startLine, endLine); }
    public string? ReadClipboard() { Note(); return inner.ReadClipboard(); }
    public void WriteClipboard(string text, VimRegisterType type) { Note(); inner.WriteClipboard(text, type); }
    public bool PerformAction(VimEditorAction action, in VimActionArgs args) { Note(); return inner.PerformAction(action, args); }
    public void ExecuteCommand(string commandId, IReadOnlyList<object?>? args) { Note(); inner.ExecuteCommand(commandId, args); }
    public VimCommentTokens GetCommentTokens() { Note(); return inner.GetCommentTokens(); }
    public bool FormatLines(int startLine, int endLine) { Note(); return inner.FormatLines(startLine, endLine); }
    public void RequestCompletion(VimCompletionKind kind) { Note(); inner.RequestCompletion(kind); }
}

/// <summary>The threading rules of the module: synchronous on the caller's thread, no I/O, no awaiting, isolated per session.</summary>
public class ThreadingTests
{
    private const string Keys1 = "ggdwjAhi<Esc>:%s/a/b/g<CR>qaxjq3@a/foo<CR>nyyPu<C-r>:g/b/normal Ax<CR>gcc:%norm 0<CR>ysiw)gUUvjd<C-v>jIz<Esc>.gd:sort<CR>";

    [Fact]
    public void Every_host_callback_happens_on_the_thread_that_called_the_engine()
    {
        int worker = 0;
        ThreadRecordingHost? host = null;
        string? text = null;
        var thread = new Thread(() =>
        {
            worker = Environment.CurrentManagedThreadId;
            var inner = new InMemoryVimHost(string.Join('\n', Enumerable.Range(0, 200).Select(i => $"line {i} foo bar a b")));
            host = new ThreadRecordingHost(inner);
            using var engine = new VimEngine(host, new VimSession());
            engine.TypeKeys(Keys1);
            foreach (var k in Keys.ParseNotation("jjkkw:s/x/y/<CR>")) engine.HandleKey(k);
            while (engine.HasPendingWork) engine.RunPendingWork(TimeSpan.FromMilliseconds(3));
            text = inner.Buffer.GetLine(0);
        });
        thread.Start();
        thread.Join();
        Assert.NotNull(host);
        Assert.True(host!.Calls > 100, "the engine should have talked to the host");
        Assert.Equal([worker], host.Threads.ToArray());
        Assert.NotNull(text);
    }

    [Fact]
    public void Handled_keys_have_their_effect_when_HandleKey_returns()
    {
        var host = new InMemoryVimHost("abc");
        using var engine = new VimEngine(host, new VimSession());
        Assert.Equal(VimKeyResult.Handled, engine.HandleKey('x'));
        Assert.Equal("bc", host.Buffer.GetLine(0));      // no continuation is pending: the edit is already in the buffer
        Assert.Equal(new TextPosition(0, 0), host.Caret);
    }

    [Fact]
    public void Application_commands_are_fire_and_forget()
    {
        var host = new InMemoryVimHost("foo bar");
        using var engine = new VimEngine(host, new VimSession());
        var before = Environment.TickCount64;
        engine.TypeKeys("gdgrK");
        Assert.True(Environment.TickCount64 - before < 1000);
        Assert.Contains(host.Commands, c => c.Id == "editor.action.revealDefinition");
        Assert.Contains(host.Commands, c => c.Id == "editor.action.goToReferences");
    }

    [Fact]
    public void The_engine_creates_no_timers_of_its_own()
    {
        var timer = new ManualVimTimer();
        var host = new InMemoryVimHost("abc def", timer);
        using var engine = new VimEngine(host, new VimSession());
        engine.ExecuteEx("nnoremap g x");
        engine.ExecuteEx("nnoremap gx $");
        engine.TypeKeys("g");
        Assert.Equal(1, timer.PendingCount);   // the only asynchrony: the host's timer for timeoutlen
        timer.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(0, timer.PendingCount);
        engine.TypeKeys("dwu:%s/a/b/<CR>");
        Assert.Equal(0, timer.PendingCount);
    }

    [Fact]
    public void Engines_on_different_threads_with_their_own_sessions_do_not_interfere()
    {
        string Run(int seed)
        {
            var r = new Random(seed);
            var text = string.Join('\n', Enumerable.Range(0, 30).Select(i => $"w{i} foo {r.Next(100)} (a b) \"c\" é😀"));
            var host = new InMemoryVimHost(text);
            using var engine = new VimEngine(host, new VimSession());
            string[] atoms = ["dw", "x", "ciwQ<Esc>", "yyp", "j", "k", "u", "<C-r>", ".", ">>", ":s/o/0/g<CR>", "qaxq", "@a", "/foo<CR>", "n", "J", "ddP", "gUU", "A!<Esc>", "ds)", "f(", "%"];
            for (var i = 0; i < 200; i++) engine.TypeKeys(atoms[r.Next(atoms.Length)]);
            return string.Join('\n', Enumerable.Range(0, host.Buffer.LineCount).Select(host.Buffer.GetLine)) + "|" + engine.Cursor;
        }

        var expected = Enumerable.Range(0, 16).Select(Run).ToArray();
        var actual = new string[16];
        Parallel.For(0, 16, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i => actual[i] = Run(i));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Only_the_documented_session_state_is_shared_between_engines()
    {
        var session = new VimSession();
        var h1 = new InMemoryVimHost("one two");
        var h2 = new InMemoryVimHost("three four");
        using var e1 = new VimEngine(h1, session);
        using var e2 = new VimEngine(h2, session);
        e1.TypeKeys("yw");
        e2.TypeKeys("P");                                           // registers are global
        Assert.Equal("one three four", h2.Buffer.GetLine(0));
        e1.TypeKeys("x");
        e2.TypeKeys("u");                                           // undo history is per buffer: e2 undoes its own put, not e1's x
        Assert.Equal("three four", h2.Buffer.GetLine(0));
        Assert.Equal("ne two", h1.Buffer.GetLine(0));
    }

    [Fact]
    public void A_cancelled_pending_work_item_leaves_the_buffer_unchanged()
    {
        var text = string.Join('\n', Enumerable.Range(0, 50_000).Select(i => "a" + i));
        var host = new InMemoryVimHost(new LineVimBuffer(text));
        using var engine = new VimEngine(host, new VimSession());
        foreach (var k in Keys.ParseNotation(":%s/a/b/<CR>")) engine.HandleKey(k);
        Assert.True(engine.HasPendingWork);
        engine.Reset();                                              // e.g. the user pressed Esc / switched buffers
        Assert.False(engine.HasPendingWork);
        Assert.Equal(text, string.Join('\n', Enumerable.Range(0, host.Buffer.LineCount).Select(host.Buffer.GetLine)));
    }
}

/// <summary>Engines, sessions and buffers must not keep each other alive after disposal.</summary>
public class LeakTests
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static VimEngine CreateEngineWithWeakBuffer(VimSession session, out WeakReference buffer, out WeakReference host)
    {
        var buf = new LineVimBuffer(string.Join('\n', Enumerable.Range(0, 500).Select(i => $"line {i} foo")));
        var h = new InMemoryVimHost(buf);
        var engine = new VimEngine(h, session);
        engine.TypeKeys("dd:%s/foo/bar/<CR>qaxjq5@a/bar<CR>nyyP:g/9/d<CR>uma'agg");
        buffer = new WeakReference(buf);
        host = new WeakReference(h);
        return engine;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateDisposedEngine(VimSession session)
    {
        var e = CreateEngineWithWeakBuffer(session, out _, out _);
        e.Dispose();
        return new WeakReference(e);
    }

    [Fact]
    public void Disposed_engines_and_their_buffers_are_collectable_while_the_session_lives()
    {
        var session = new VimSession();
        LeakAssert.NotRetained(() =>
        {
            var host = new InMemoryVimHost(string.Join('\n', Enumerable.Range(0, 200).Select(i => $"line {i} foo")));
            var engine = new VimEngine(host, session);
            engine.TypeKeys("dd:%s/foo/bar/<CR>qaxjq5@a/bar<CR>nyyPuma'a");
            engine.Dispose();
            return [engine, host, host.Buffer];
        });
        GC.KeepAlive(session);
    }

    [Fact]
    public void Engines_with_pending_work_and_mappings_are_collectable_after_dispose()
    {
        var session = new VimSession();
        LeakAssert.NotRetained(() =>
        {
            var host = new InMemoryVimHost(string.Join('\n', Enumerable.Range(0, 30_000).Select(i => $"line {i} foo")));
            var engine = new VimEngine(host, session);
            engine.ExecuteEx("nnoremap g x");
            engine.ExecuteEx("nnoremap gx $");
            foreach (var k in Keys.ParseNotation(":%s/foo/bar/<CR>g")) engine.HandleKey(k);   // unfinished work and a pending mapping prefix
            engine.Dispose();
            return [engine, host, host.Buffer];
        });
        GC.KeepAlive(session);
    }

    [Fact]
    public void The_engine_keeps_nothing_alive_after_Detach()
    {
        var session = new VimSession();
        var engine = CreateEngineWithWeakBuffer(session, out var buffer, out var host);
        engine.Detach();
        for (var i = 0; i < 10 && (buffer.IsAlive || host.IsAlive); i++)
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }
        Assert.False(buffer.IsAlive, "the buffer is still referenced by the detached engine or the session");
        Assert.False(host.IsAlive, "the host is still referenced by the detached engine or the session");
        GC.KeepAlive(engine);
        GC.KeepAlive(session);
    }

    [Fact]
    public void A_detached_engine_ignores_keys_and_does_not_throw()
    {
        var engine = CreateEngineWithWeakBuffer(new VimSession(), out _, out _);
        engine.Detach();
        Assert.Equal(VimKeyResult.PassThrough, engine.HandleKey('x'));
        engine.TypeKeys("dd");
        Assert.False(engine.HasPendingWork);
        engine.Dispose();
    }

    [Fact]
    public void Per_buffer_state_dies_with_the_buffer()
    {
        var session = new VimSession();
        var engines = new List<WeakReference>();
        for (var i = 0; i < 20; i++) engines.Add(CreateDisposedEngine(session));
        for (var i = 0; i < 10 && engines.Any(w => w.IsAlive); i++)
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }
        Assert.DoesNotContain(engines, w => w.IsAlive);
        GC.KeepAlive(session);
    }
}
