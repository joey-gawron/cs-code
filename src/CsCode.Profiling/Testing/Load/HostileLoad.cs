using System.Diagnostics;
using CsCode.Core.Languages;
using CsCode.Core.Platform;
using CsCode.Core.Text;

namespace CsCode.Profiling.Testing.Load;

/// <summary>
/// Burns CPU (MUST 9 hostile load): in-process threads at normal priority, or child processes (a busy <c>sh</c> loop each, like a language server
/// pegging cores). Disposing stops the threads and kills exactly the child processes it started (by pid, never by name).
/// </summary>
public sealed class CpuBurner : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Thread> _threads = new();
    private readonly List<Process> _children = new();

    private CpuBurner() { }

    /// <summary>Pids of the burner processes this instance started.</summary>
    public IReadOnlyList<int> ChildPids => _children.Select(p => p.Id).ToArray();

    public static CpuBurner InProcess(int threads)
    {
        var burner = new CpuBurner();
        for (var i = 0; i < threads; i++)
        {
            var t = new Thread(() =>
            {
                var x = 0UL;
                while (!burner._stop.IsCancellationRequested) for (var k = 0; k < 100_000; k++) x = x * 6364136223846793005UL + 1442695040888963407UL;
                GC.KeepAlive(x);
            }) { IsBackground = true, Name = "cpu-burner-" + i };
            burner._threads.Add(t);
            t.Start();
        }
        return burner;
    }

    /// <summary>Starts <paramref name="processes"/> busy child processes (<c>/bin/sh -c 'while :; do :; done'</c>). Unix only.</summary>
    public static CpuBurner ChildProcesses(int processes)
    {
        var burner = new CpuBurner();
        for (var i = 0; i < processes; i++)
        {
            var psi = new ProcessStartInfo("/bin/sh") { UseShellExecute = false, RedirectStandardInput = true };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("while :; do :; done");
            burner._children.Add(Process.Start(psi) ?? throw new InvalidOperationException("sh did not start"));
        }
        return burner;
    }

    public void Dispose()
    {
        _stop.Cancel();
        foreach (var t in _threads) t.Join();
        foreach (var p in _children)
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); p.WaitForExit(5000); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            p.Dispose();
        }
        _stop.Dispose();
    }
}

/// <summary>Providers that answer late (MUST 9): an awaited delay honouring cancellation, or — hostile — a call that blocks its calling thread.</summary>
public static class SlowResponder
{
    /// <summary>Returns <paramref name="value"/> after <paramref name="delay"/> (cancellation ends the wait with an <see cref="OperationCanceledException"/>).</summary>
    public static async ValueTask<T> RespondAsync<T>(T value, TimeSpan delay, CancellationToken ct)
    {
        await Task.Delay(delay, ct).ConfigureAwait(false);
        return value;
    }

    /// <summary>Blocks the CALLING thread for <paramref name="delay"/> before answering: a provider that would freeze a UI caller.</summary>
    public static ValueTask<T> BlockThenRespond<T>(T value, TimeSpan delay)
    {
        Thread.Sleep(delay);
        return ValueTask.FromResult(value);
    }

    /// <summary>A completion provider that answers after <paramref name="delay"/> (or blocks its caller with <paramref name="blockCaller"/>).</summary>
    public static ICompletionProvider Completion(TimeSpan delay, IReadOnlyList<CompletionItem>? items = null, bool blockCaller = false) =>
        new DelayedCompletion(delay, items ?? GiantPayload.CompletionItems(20), blockCaller);

    private sealed class DelayedCompletion(TimeSpan delay, IReadOnlyList<CompletionItem> items, bool blockCaller) : ICompletionProvider
    {
        public IReadOnlyList<string> TriggerCharacters => ["."];
        public ValueTask<CompletionList?> ProvideCompletionAsync(ITextDocument document, TextPosition position, CompletionContext context, CancellationToken ct) =>
            blockCaller ? BlockThenRespond<CompletionList?>(new CompletionList(items), delay) : RespondAsync<CompletionList?>(new CompletionList(items), delay, ct);
    }
}

/// <summary>Never answers (MUST 9): a hung server or provider. Only cancellation (or <see cref="Release"/>) ends the wait.</summary>
public sealed class Hanger
{
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _waiting;

    /// <summary>Calls currently hanging.</summary>
    public int Waiting => Volatile.Read(ref _waiting);

    public async ValueTask<T> HangAsync<T>(CancellationToken ct)
    {
        Interlocked.Increment(ref _waiting);
        try
        {
            await _release.Task.WaitAsync(ct).ConfigureAwait(false);
            throw new OperationCanceledException("released");
        }
        finally { Interlocked.Decrement(ref _waiting); }
    }

    /// <summary>Ends every hanging call (with an <see cref="OperationCanceledException"/>).</summary>
    public void Release() => _release.TrySetResult();

    /// <summary>A completion provider that never answers.</summary>
    public ICompletionProvider Completion() => new HangingCompletion(this);

    private sealed class HangingCompletion(Hanger hanger) : ICompletionProvider
    {
        public IReadOnlyList<string> TriggerCharacters => ["."];
        public ValueTask<CompletionList?> ProvideCompletionAsync(ITextDocument document, TextPosition position, CompletionContext context, CancellationToken ct) =>
            hanger.HangAsync<CompletionList?>(ct);
    }
}

/// <summary>Fails on every call (MUST 9): a crashing provider in-process, or a child process that dies at once with a given exit code.</summary>
public static class CrashLooper
{
    public static ICompletionProvider Completion() => new ThrowingCompletion();

    /// <summary>A command line (<c>/bin/sh -c 'exit N'</c> after an optional delay) for a server definition that crashes on every start.</summary>
    public static (string FileName, string[] Arguments) Command(int exitCode = 3, TimeSpan? after = null) =>
        ("/bin/sh", ["-c", after is { } d ? $"sleep {d.TotalSeconds.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}; exit {exitCode}" : $"exit {exitCode}"]);

    /// <summary>Starts <paramref name="start"/> <paramref name="times"/> times in a row (each attempt is awaited; failures are counted, never thrown).</summary>
    public static async Task<int> RunAsync(Func<Task> start, int times)
    {
        var failures = 0;
        for (var i = 0; i < times; i++)
        {
            try { await start().ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { failures++; }
        }
        return failures;
    }

    private sealed class ThrowingCompletion : ICompletionProvider
    {
        public IReadOnlyList<string> TriggerCharacters => ["."];
        public ValueTask<CompletionList?> ProvideCompletionAsync(ITextDocument document, TextPosition position, CompletionContext context, CancellationToken ct) =>
            throw new InvalidOperationException("hostile provider: crashed");
    }
}

/// <summary>Huge answers (MUST 9): a 50k-item completion list, 1M semantic tokens, a 100 MB log, thousands of diagnostics.</summary>
public static class GiantPayload
{
    public static IReadOnlyList<CompletionItem> CompletionItems(int count = 50_000)
    {
        var items = new CompletionItem[count];
        for (var i = 0; i < count; i++)
            items[i] = new CompletionItem { Label = "item" + i.ToString("D6", System.Globalization.CultureInfo.InvariantCulture), Kind = (CompletionItemKind)(i % 20 + 1), Detail = "detail of item " + i };
        return items;
    }

    public static ICompletionProvider Completion(int count = 50_000) => new FixedCompletion(CompletionItems(count));

    /// <summary>LSP-encoded semantic tokens (5 integers per token: delta line, delta start, length, type, modifiers).</summary>
    public static int[] SemanticTokens(int tokens = 1_000_000)
    {
        var data = new int[tokens * 5];
        for (var i = 0; i < tokens; i++)
        {
            data[i * 5] = i % 8 == 0 ? 1 : 0;
            data[i * 5 + 1] = i % 8 == 0 ? 0 : 5;
            data[i * 5 + 2] = 4;
            data[i * 5 + 3] = i % 12;
            data[i * 5 + 4] = 0;
        }
        return data;
    }

    /// <summary>Log lines totalling about <paramref name="totalBytes"/> (default 100 MB), produced lazily (never all in memory).</summary>
    public static IEnumerable<string> LogLines(long totalBytes = 100L * 1024 * 1024, int lineLength = 120)
    {
        var body = new string('x', Math.Max(0, lineLength - 24));
        for (long written = 0, n = 0; written < totalBytes; n++)
        {
            var line = $"{n:D10} [info] {body}";
            written += line.Length + 1;
            yield return line;
        }
    }

    public static IReadOnlyList<Diagnostic> Diagnostics(int count, string source = "hostile")
    {
        var list = new Diagnostic[count];
        for (var i = 0; i < count; i++)
            list[i] = new Diagnostic(TextRange.At(i, 0), "hostile diagnostic " + i, (DiagnosticSeverity)(i % 4 + 1), Source: source);
        return list;
    }

    private sealed class FixedCompletion(IReadOnlyList<CompletionItem> items) : ICompletionProvider
    {
        public IReadOnlyList<string> TriggerCharacters => ["."];
        public ValueTask<CompletionList?> ProvideCompletionAsync(ITextDocument document, TextPosition position, CompletionContext context, CancellationToken ct) =>
            ValueTask.FromResult<CompletionList?>(new CompletionList(items));
    }
}

/// <summary>What a <see cref="Flooder"/> run produced.</summary>
public sealed record FloodResult(long Messages, TimeSpan Elapsed)
{
    public double PerSecond => Elapsed.TotalSeconds <= 0 ? 0 : Messages / Elapsed.TotalSeconds;
}

/// <summary>
/// Floods (MUST 9): calls an emitter at a configured rate on its own thread for a duration — diagnostics publishes, output lines, any event.
/// Rate 0 = as fast as possible. The flooding thread is the producer's lane, as a language server's reader thread would be.
/// </summary>
public static class Flooder
{
    public static Task<FloodResult> RunAsync(Action<long> emit, int ratePerSecond, TimeSpan duration, CancellationToken ct = default)
    {
        var done = new TaskCompletionSource<FloodResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var sw = Stopwatch.StartNew();
            long n = 0;
            try
            {
                while (sw.Elapsed < duration && !ct.IsCancellationRequested)
                {
                    emit(n++);
                    if (ratePerSecond <= 0) continue;
                    var due = TimeSpan.FromSeconds((double)n / ratePerSecond);
                    var wait = due - sw.Elapsed;
                    if (wait > TimeSpan.FromMilliseconds(1)) Thread.Sleep(wait);
                }
                done.TrySetResult(new FloodResult(n, sw.Elapsed));
            }
            catch (Exception ex) { done.TrySetException(ex); }
        }) { IsBackground = true, Name = "flooder" };
        thread.Start();
        return done.Task;
    }

    /// <summary>Publishes <paramref name="perMessage"/> diagnostics for one of <paramref name="files"/> per message (round robin).</summary>
    public static Task<FloodResult> DiagnosticsAsync(IDiagnosticService diagnostics, IReadOnlyList<Uri> files, int perMessage, int ratePerSecond, TimeSpan duration,
        string owner = "flooder", CancellationToken ct = default)
    {
        var payload = GiantPayload.Diagnostics(perMessage, owner);
        return RunAsync(n => diagnostics.Set(files[(int)(n % files.Count)], owner, payload), ratePerSecond, duration, ct);
    }

    /// <summary>Appends lines of <paramref name="lineLength"/> characters to an output channel.</summary>
    public static Task<FloodResult> OutputAsync(IOutputChannel channel, int lineLength, int ratePerSecond, TimeSpan duration, CancellationToken ct = default)
    {
        var line = new string('o', lineLength);
        return RunAsync(_ => channel.AppendLine(line), ratePerSecond, duration, ct);
    }
}
