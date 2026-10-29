using System.Collections.Concurrent;
using System.Diagnostics;
using CsCode.Core.Text;
using CsCode.Panels.Search.Engine;
using CsCode.Panels.Search.ViewModels;
using CsCode.TestDoubles;
using CsCode.Ui.Threading;

namespace CsCode.Panels.Search.Tests.Hostile;

/// <summary>
/// A stand-in for the Avalonia UI thread: one dedicated thread draining two queues, "input" (Input/Normal) before "background"
/// (Background/Idle), exactly the priority relationship the real dispatcher has. A callback that runs long delays everything behind it,
/// which is what <see cref="HeartbeatProbe"/> measures.
/// </summary>
public sealed class SingleThreadUi : UiDispatcherBase, IDisposable
{
    private readonly ConcurrentQueue<Action> _high = new(), _low = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly Thread _thread;
    private volatile bool _stop;

    public SingleThreadUi()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "test-ui-thread" };
        _thread.Start();
    }

    public override bool CheckAccess() => Thread.CurrentThread == _thread;

    protected override void PostCore(Action action, UiPriority priority)
    {
        (priority is UiPriority.Input or UiPriority.Normal ? _high : _low).Enqueue(action);
        _signal.Release();
    }

    private void Loop()
    {
        while (!_stop)
        {
            _signal.Wait();
            if (_stop) return;
            Action? a;
            while (!_high.TryDequeue(out a) && !_low.TryDequeue(out a))
            {
                if (_stop) return;
            }
            a();
        }
    }

    public Task<T> RunAsync<T>(Func<T> f) => InvokeAsync(f, UiPriority.Input);

    public void Dispose()
    {
        _stop = true;
        _signal.Release();
        _thread.Join(2000);
    }
}

/// <summary>Posts a timestamped no-op to the UI thread every millisecond; the time each waits before running is the "dispatcher latency".</summary>
public sealed class HeartbeatProbe : IDisposable
{
    private readonly SingleThreadUi _ui;
    private readonly Thread _thread;
    private readonly ConcurrentQueue<double> _latencies = new();
    private volatile bool _stop;

    public HeartbeatProbe(SingleThreadUi ui)
    {
        _ui = ui;
        _thread = new Thread(Loop) { IsBackground = true, Name = "test-heartbeat" };
        _thread.Start();
    }

    private void Loop()
    {
        while (!_stop)
        {
            var stamp = Stopwatch.GetTimestamp();
            _ui.Post(() => _latencies.Enqueue(Stopwatch.GetElapsedTime(stamp).TotalMilliseconds), UiPriority.Input);
            Thread.Sleep(1);
        }
    }

    public (int Samples, double P50, double P99, double Max) Stop()
    {
        _stop = true;
        _thread.Join();
        Thread.Sleep(30); // let the last beats run
        var all = _latencies.ToArray().OrderBy(x => x).ToArray();
        if (all.Length == 0) return (0, 0, 0, 0);
        return (all.Length, all[all.Length / 2], all[(int)(all.Length * 0.99)], all[^1]);
    }

    public void Dispose() => _stop = true;
}

/// <summary>A big synthetic workspace, created once per run: 200,000 small files plus the nasty individual files.</summary>
public sealed class HostileTree : IDisposable
{
    public const int Dirs = 2000, FilesPerDir = 100;
    public int FileCount => Dirs * FilesPerDir;
    public TempDirectory Root { get; } = new("cscode-hostile-");
    public string Many => Root.Combine("many");
    public string Specials => Root.Combine("special");
    public int NeedleFiles { get; private set; }

    public HostileTree()
    {
        Directory.CreateDirectory(Many);
        Directory.CreateDirectory(Specials);
        var needles = 0;
        Parallel.For(0, Dirs, new ParallelOptions { MaxDegreeOfParallelism = 8 }, d =>
        {
            var dir = Path.Combine(Many, $"d{d / 100:00}", $"s{d:0000}");
            Directory.CreateDirectory(dir);
            for (var i = 0; i < FilesPerDir; i++)
            {
                var n = d * FilesPerDir + i;
                var hasNeedle = n % 1000 == 7;
                if (hasNeedle) Interlocked.Increment(ref needles);
                File.WriteAllText(Path.Combine(dir, $"f{i:000}.txt"),
                    $"// generated file {n}\nvar value{n} = Compute({n});\n{(hasNeedle ? "found the NEEDLE here\n" : "")}return value{n};\n");
            }
        });
        NeedleFiles = needles;

        // 10 MB single line, needle at the start and the middle (the 2M-char line buffer splits the rest)
        File.WriteAllText(Path.Combine(Specials, "line10mb.txt"), "needle " + new string('x', 5_000_000) + " needle " + new string('y', 5_000_000));
        // a million matches in one file
        using (var w = new StreamWriter(Path.Combine(Specials, "matches1m.txt")))
            for (var i = 0; i < 1_000_000; i++) w.WriteLine("x needle");
        // catastrophic-backtracking bait: long runs of 'a' ending in 'c'
        File.WriteAllLines(Path.Combine(Specials, "evil.txt"), Enumerable.Range(0, 50).Select(_ => new string('a', 20_000) + "c"));
        // ~210 MB of text, a needle on every 100,000th line
        using (var w = new StreamWriter(Path.Combine(Specials, "big-text.txt"), false, new System.Text.UTF8Encoding(false), 1 << 20))
            for (var i = 0; i < 3_000_000; i++) w.WriteLine(i % 100_000 == 0 ? $"line {i}: the needle is here" : $"line {i}: filler filler filler filler filler");
        // 400 MB sparse binary (all NUL)
        using (var fs = new FileStream(Path.Combine(Specials, "big.bin"), FileMode.Create)) fs.SetLength(400L * 1024 * 1024);
    }

    public Uri Uri(string absolute) => new(absolute);
    public void Dispose() => Root.Dispose();
}

/// <summary>A workspace + engine over one directory, driven from <see cref="SingleThreadUi"/>.</summary>
public sealed class HostileRig : IDisposable
{
    public SingleThreadUi Ui { get; } = new();
    public TestWorkspace Workspace { get; }
    public SearchEngine Engine { get; }
    public ReplaceService Replace { get; }
    public SearchServices Services { get; }

    public HostileRig(string folder, SearchLimits? limits = null)
    {
        Workspace = new TestWorkspace(new Uri(folder.TrimEnd('/') + "/"));
        Engine = new SearchEngine(Workspace, new DocumentManager(), new TestConfigurationService(), null, limits);
        Replace = new ReplaceService(Workspace, null, limits);
        Services = new SearchServices { Workspace = Workspace, Ui = Ui, Engine = Engine, Replace = Replace };
    }

    public Task<FindInFilesViewModel> NewVmAsync() => Ui.RunAsync(() => new FindInFilesViewModel(Services));

    public void Dispose()
    {
        Engine.Dispose();
        Replace.Dispose();
        Ui.Dispose();
    }
}
