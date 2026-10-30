using System.Diagnostics;
using System.Text.RegularExpressions;
using CsCode.Core.Languages;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Lsp.Runtime;
using CsCode.Lsp.Tests.Harness;
using Xunit;
using P = CsCode.Lsp.Protocol;

namespace CsCode.Lsp.Tests.Hostile;

/// <summary>
/// The hostile-server suite. A node peer (Hostile/hostile-server.js) runs as a REAL child process and misbehaves: burns CPU in children,
/// answers after 5 s, floods 100 MB of notifications / stderr, returns 50k-item completion lists and 1M-token semantic results, never
/// answers, or crashes in a loop. For each, we assert: the caller-side cost of every call stays under 1 ms, memory and queue depth stay bounded,
/// a healthy second server and its features are unaffected, breaker / restart behaviour is correct, and shutdown leaves no process behind.
/// </summary>
public class HostileServerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static string Script => Path.Combine(AppContext.BaseDirectory, "Hostile", "hostile-server.js");
    private const string Channel = "Hostile (LSP)";

    private static bool RequireNode()
    {
        if (ToolResolver.FindOnPath("node", null) is null) { Assert.Skip("node is not on PATH (needed to run the hostile peer)"); return false; }
        if (!File.Exists(Script)) { Assert.Skip("Hostile/hostile-server.js was not copied to the output directory"); return false; }
        if (!OperatingSystem.IsLinux()) { Assert.Skip("process-tree and priority assertions read /proc"); return false; }
        return true;
    }

    private sealed class ThrowingSyncContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => throw new InvalidOperationException("posted to the caller's context");
        public override void Send(SendOrPostCallback d, object? state) => throw new InvalidOperationException("sent to the caller's context");
    }

    private sealed class Rig : IAsyncDisposable
    {
        public required LspTestEnv Env { get; init; }
        public required FakeConnectionFactory Good { get; init; }
        public required InMemoryTextDocument HostileDoc { get; init; }
        public required InMemoryTextDocument GoodDoc { get; init; }
        public required string Mode { get; init; }
        public int HostilePid => Env.Manager.Servers.First(s => s.Id == "hostile").ProcessId ?? -1;
        public HashSet<int> Everyone { get; } = new();

        public IHoverProvider Hover => Env.Registry.GetProviders<IHoverProvider>(HostileDoc)[0];
        public IHoverProvider GoodHover => Env.Registry.GetProviders<IHoverProvider>(GoodDoc)[0];

        public void Track()
        {
            var pid = HostilePid;
            if (pid > 0) { Everyone.Add(pid); foreach (var d in ProcessReaper.Descendants(pid)) Everyone.Add(d); }
        }

        public async ValueTask DisposeAsync()
        {
            Track();
            await Env.DisposeAsync();
            await TestHelpers.EventuallyAsync(() => Everyone.All(p => !Directory.Exists("/proc/" + p) || IsZombie(p)), () => "all processes started for the hostile server are gone: " + string.Join(",", Everyone.Where(p => Directory.Exists("/proc/" + p) && !IsZombie(p))), TimeSpan.FromSeconds(20));
        }

        private static bool IsZombie(int pid)
        {
            try { return File.ReadAllText($"/proc/{pid}/stat").Contains(") Z "); } catch (IOException) { return true; }
        }
    }

    private static async Task<Rig> StartAsync(string mode, Action<CsCode.TestDoubles.TestConfigurationService>? configure = null, bool waitRunning = true)
    {
        var good = new FakeConnectionFactory
        {
            OnConnect = (s, _) =>
            {
                s.Capabilities = new P.ServerCapabilities { TextDocumentSync = P.TextDocumentSyncKind.Incremental, HoverProvider = true };
                s.Requests[P.LspMethods.TextDocumentHover] = _ => Task.FromResult<object?>(new P.Hover { Contents = new P.MarkupContent { Kind = P.MarkupKind.PlainText, Value = "good" } });
            },
        };
        var routing = new RoutingConnectionFactory(r => r.Definition.Id == "hostile", new DefaultLspConnectionFactory(), good);
        var env = new LspTestEnv(c =>
        {
            c.Set(LspSettingKeys.InteractiveTimeoutMs, 3000);
            c.Set(LspSettingKeys.ShutdownTimeoutMs, 1000);
            configure?.Invoke(c);
        }, connectionsOverride: routing);

        env.Manager.AddDefinition(new LanguageServerDefinition("hostile", "Hostile", DocumentSelector.ForLanguage("hostile"), "node", [Script],
            Env: new Dictionary<string, string> { ["HOSTILE_MODE"] = mode }));
        env.Manager.AddDefinition(LspTestEnv.FakeDef("good", "good"));

        var goodDoc = env.Open("b.good", "good doc", "good");
        var hostileDoc = env.Open("a.hostile", "hostile doc text\nsecond line\n", "hostile");
        var rig = new Rig { Env = env, Good = good, HostileDoc = hostileDoc, GoodDoc = goodDoc, Mode = mode };
        if (waitRunning)
        {
            await env.WaitForStateAsync("hostile", LanguageServerState.Running, TimeSpan.FromSeconds(30));
            await env.WaitForStateAsync("good", LanguageServerState.Running, TimeSpan.FromSeconds(30));
            rig.Track();
        }
        return rig;
    }

    /// <summary>Synchronous cost of issuing a call on a thread whose synchronization context throws if anything is posted to it.</summary>
    private static (double AvgMs, double P99Ms, double MaxMs) SyncCost(Func<object> call, int n = 300)
    {
        var times = new double[n];
        var failure = (Exception?)null;
        var t = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new ThrowingSyncContext());
            try
            {
                for (var i = 0; i < 30; i++) _ = call();
                for (var i = 0; i < n; i++)
                {
                    var sw = Stopwatch.StartNew();
                    _ = call();
                    times[i] = sw.Elapsed.TotalMilliseconds;
                }
            }
            catch (Exception ex) { failure = ex; }
        });
        t.Start();
        Assert.True(t.Join(TimeSpan.FromSeconds(60)));
        Assert.Null(failure);
        Array.Sort(times);
        return (times.Average(), times[(int)(n * 0.99)], times[^1]);
    }

    private static long ManagedMb() => GC.GetTotalMemory(forceFullCollection: true) / (1024 * 1024);

    private sealed class PeakMemorySampler : IDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly Thread _thread;
        private long _peak;
        public PeakMemorySampler()
        {
            _peak = GC.GetTotalMemory(false);
            _thread = new Thread(() => { while (!_cts.IsCancellationRequested) { var m = GC.GetTotalMemory(false); if (m > Interlocked.Read(ref _peak)) Interlocked.Exchange(ref _peak, m); Thread.Sleep(10); } }) { IsBackground = true };
            _thread.Start();
        }
        public long PeakMb => Interlocked.Read(ref _peak) / (1024 * 1024);
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _cts.Cancel();
            _thread.Join();
            _cts.Dispose();
        }
    }

    private static async Task AssertGoodServerUnaffectedAsync(Rig rig, int budgetMs = 500)
    {
        var sw = Stopwatch.StartNew();
        var hover = await rig.GoodHover.ProvideHoverAsync(rig.GoodDoc, new TextPosition(0, 0), Ct).Within(TimeSpan.FromSeconds(10));
        Assert.Equal("good", hover!.Contents[0].Value);
        Assert.True(sw.ElapsedMilliseconds < budgetMs, $"the healthy server answered in {sw.ElapsedMilliseconds} ms while the hostile one misbehaved");
    }

    private static void AssertQueueBounded(Rig rig)
    {
        var q = rig.Env.Manager.Queue;
        Assert.True(q.MaxObservedDepth <= q.Capacity, $"queue depth reached {q.MaxObservedDepth} (cap {q.Capacity})");
    }

    // =================================================================================================================
    // (a) CPU burner
    // =================================================================================================================

    [Fact]
    public async Task Cpu_burning_children_run_at_low_priority_and_neither_typing_nor_other_servers_notice()
    {
        if (!RequireNode()) return;
        await using var rig = await StartAsync("cpu");
        await TestHelpers.EventuallyAsync(() => ProcessReaper.Descendants(rig.HostilePid).Count >= 2, "the burners started", TimeSpan.FromSeconds(10));
        rig.Track();

        // the server and everything it spawned run below normal priority (nice >= 10): the OS scheduler favours the editor
        foreach (var pid in rig.Everyone)
            Assert.True(NativePriority.GetProcessNice(pid) >= 10, $"process {pid} nice = {NativePriority.GetProcessNice(pid)}");

        // typing: the cost of an edit on the editing thread is unchanged while the children burn CPU
        var doc = rig.HostileDoc;
        var typing = new double[2000];
        var worker = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new ThrowingSyncContext());
            for (var i = 0; i < typing.Length; i++)
            {
                var sw = Stopwatch.StartNew();
                doc.ApplyEdits([TextEdit.Insert(new TextPosition(0, i % 8), "k")]);
                typing[i] = sw.Elapsed.TotalMilliseconds;
            }
        });
        worker.Start();
        Assert.True(worker.Join(TimeSpan.FromSeconds(30)));
        Array.Sort(typing);
        Assert.True(typing[(int)(typing.Length * 0.99)] < 2.0, $"typing p99 {typing[(int)(typing.Length * 0.99)]:F3} ms");

        var cost = SyncCost(() => rig.Hover.ProvideHoverAsync(doc, new TextPosition(0, 1), Ct));
        Assert.True(cost.P99Ms < 1.0, $"hover call p99 {cost.P99Ms:F3} ms");
        var answer = await rig.Hover.ProvideHoverAsync(doc, new TextPosition(0, 1), Ct).Within(TimeSpan.FromSeconds(10));
        Assert.Equal("ok", answer!.Contents[0].Value); // the server itself still answers
        await AssertGoodServerUnaffectedAsync(rig);
        AssertQueueBounded(rig);

        await rig.Env.Manager.StopAllAsync(Ct); // kills the whole tree, burners included
        Assert.All(rig.Everyone, pid => Assert.True(GoneOrZombie(pid), $"process {pid} survived StopAll"));
    }

    // A killed process can be reaped between the directory check and the read of its stat file: that is gone too (the old check threw then).
    private static bool GoneOrZombie(int pid)
    {
        try { return !Directory.Exists("/proc/" + pid) || File.ReadAllText($"/proc/{pid}/stat").Contains(") Z ", StringComparison.Ordinal); }
        catch (Exception ex) when (ex is DirectoryNotFoundException or FileNotFoundException) { return true; }
    }

    // STRETCH: optional Linux CPU cap through a transient systemd user scope (cscode.lsp.cpuQuotaPercent).
    [Fact]
    public async Task The_optional_cpu_quota_confines_the_server_and_its_children_to_a_capped_cgroup_scope()
    {
        if (!RequireNode()) return;
        await using var rig = await StartAsync("cpu", c => c.Set(LspSettingKeys.CpuQuotaPercent, 25));
        var log = rig.Env.Output.Text(Channel);
        if (log.Contains("systemd-run (user scopes) is not usable")) { Assert.Skip("systemd-run --user --scope is not usable on this host (the fallback path is what ran)"); return; }

        var pid = rig.HostilePid;
        await TestHelpers.EventuallyAsync(() => ProcessReaper.Descendants(pid).Count >= 2, "burners started", TimeSpan.FromSeconds(10));
        string CgroupOf(int p) => File.ReadAllLines($"/proc/{p}/cgroup").First(l => l.StartsWith("0::", StringComparison.Ordinal))[3..];

        var cgroup = CgroupOf(pid);
        Assert.Contains(".scope", cgroup);
        foreach (var child in ProcessReaper.Descendants(pid)) Assert.Equal(cgroup, CgroupOf(child)); // the burners are inside the same capped scope
        Assert.True(NativePriority.GetProcessNice(pid) >= 10); // priority wrapping still applies inside the scope

        var cpuMax = Path.Combine("/sys/fs/cgroup", cgroup.TrimStart('/'), "cpu.max");
        if (!File.Exists(cpuMax) || File.ReadAllText(cpuMax).StartsWith("max", StringComparison.Ordinal))
        {
            Assert.Skip("the cpu controller is not delegated to user scopes on this host, so the quota cannot be enforced (scope placement was verified)");
            return;
        }
        Assert.Equal("25000 100000", File.ReadAllText(cpuMax).Trim());
        rig.Track();
        await rig.Env.Manager.StopAllAsync(Ct);
    }

    // =================================================================================================================
    // (b) answers every request after 5 s
    // =================================================================================================================

    [Fact]
    public async Task A_server_that_answers_after_five_seconds_times_out_trips_the_breaker_and_is_killed_cleanly()
    {
        if (!RequireNode()) return;
        await using var rig = await StartAsync("slow", c => c.Set(LspSettingKeys.InteractiveTimeoutMs, 400));
        var cost = SyncCost(() => rig.Hover.ProvideHoverAsync(rig.HostileDoc, new TextPosition(0, 1), Ct));
        Assert.True(cost.P99Ms < 1.0, $"hover call p99 {cost.P99Ms:F3} ms");

        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 5; i++)
        {
            Assert.Null(await rig.Hover.ProvideHoverAsync(rig.HostileDoc, new TextPosition(0, 0), Ct).Within(TimeSpan.FromSeconds(10)));
            rig.HostileDoc.ApplyEdits([TextEdit.Insert(new TextPosition(0, 0), "x")]);
            if (i == 2) await AssertGoodServerUnaffectedAsync(rig); // mid-way, while the hostile server is stalling us
        }
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(8), $"five timeouts took {sw.Elapsed}: each request is bounded by the timeout, not by the 5 s answer");
        await rig.Env.WaitForStateAsync("hostile", LanguageServerState.Unresponsive);

        var fast = Stopwatch.StartNew();
        Assert.Null(await rig.Hover.ProvideHoverAsync(rig.HostileDoc, new TextPosition(0, 0), Ct).Within());
        Assert.True(fast.ElapsedMilliseconds < 100, "an open breaker answers immediately");
        AssertQueueBounded(rig);

        rig.Track();
        var stop = Stopwatch.StartNew();
        await rig.Env.Manager.StopAllAsync(Ct); // shutdown is answered after 5 s: the client gives up after ShutdownTimeout and kills
        Assert.True(stop.Elapsed < TimeSpan.FromSeconds(4), $"stop took {stop.Elapsed}");
    }

    // =================================================================================================================
    // (c) floods 100 MB of notifications
    // =================================================================================================================

    [Fact]
    public async Task A_hundred_megabyte_notification_flood_keeps_memory_and_queues_bounded()
    {
        if (!RequireNode()) return;
        var baseline = ManagedMb();
        using var sampler = new PeakMemorySampler();
        await using var rig = await StartAsync("flood");
        var cost = SyncCost(() => rig.Hover.ProvideHoverAsync(rig.HostileDoc, new TextPosition(0, 1), Ct), n: 200);
        Assert.True(cost.P99Ms < 1.0, $"hover call p99 {cost.P99Ms:F3} ms during the flood");
        await AssertGoodServerUnaffectedAsync(rig, budgetMs: 1500);

        await TestHelpers.EventuallyAsync(() => rig.Env.Output.Text(Channel).Contains("flood-done"), "the server finished writing 100 MB", TimeSpan.FromSeconds(120));
        // every byte was consumed without the reader ever waiting on the lane; afterwards the server is usable again
        var hover = await UntilAsync(() => rig.Hover.ProvideHoverAsync(rig.HostileDoc, new TextPosition(0, 1), Ct), 60);
        Assert.Equal("ok", hover!.Contents[0].Value);

        AssertQueueBounded(rig);
        Assert.True(rig.Env.Output.Text(Channel).Length <= 2_000_000, "the output channel stays within its cap");
        Assert.True(sampler.PeakMb - baseline < 400, $"managed memory peaked {sampler.PeakMb - baseline} MB above baseline during a 100 MB flood");
        Assert.Equal(LanguageServerState.Running, rig.Env.Manager.Servers.First(s => s.Id == "hostile").State);
        sampler.Dispose();
        await Task.Delay(500, Ct);
        Assert.True(ManagedMb() - baseline < 120, $"managed memory after the flood: +{ManagedMb() - baseline} MB");
    }

    [Fact]
    public async Task A_hundred_megabytes_of_stderr_is_drained_into_a_bounded_buffer()
    {
        if (!RequireNode()) return;
        var baseline = ManagedMb();
        await using var rig = await StartAsync("stderr");
        await TestHelpers.EventuallyAsync(() => rig.Env.Output.Text(Channel).Contains("stderr-done"), "stderr flood finished", TimeSpan.FromSeconds(120));
        var hover = await UntilAsync(() => rig.Hover.ProvideHoverAsync(rig.HostileDoc, new TextPosition(0, 1), Ct), 30);
        Assert.Equal("ok", hover!.Contents[0].Value);
        Assert.True(rig.Env.Output.Text(Channel).Length <= 2_000_000);
        AssertQueueBounded(rig);
        Assert.True(ManagedMb() - baseline < 80, $"managed memory +{ManagedMb() - baseline} MB");
    }

    // =================================================================================================================
    // (d) giant results
    // =================================================================================================================

    /// <summary>One round of giant requests in its own frame, so the caller (and its async state machine) never holds the results: only weak references escape.</summary>
    private static async Task<(WeakReference List, WeakReference Tokens)> GiantRoundAsync(ICompletionProvider completion, ISemanticTokensProvider tokens, ITextDocument doc, int column)
    {
        var list = await UntilAsync(() => completion.ProvideCompletionAsync(doc, new TextPosition(0, column), new CompletionContext(CompletionTriggerKind.Invoked), Ct), 40);
        Assert.Equal(5000, list!.Items.Count);   // capped at cscode.lsp.maxCompletionItems
        Assert.True(list.IsIncomplete);          // so the editor re-queries instead of receiving 50,000 items
        var data = await UntilAsync(() => tokens.ProvideSemanticTokensAsync(doc, Ct), 60);
        Assert.Equal(250_000 * 5, data!.Data.Count); // capped at cscode.lsp.maxSemanticTokens
        return (new WeakReference(list), new WeakReference(data));
    }

    [Fact]
    public async Task Fifty_thousand_completion_items_and_a_million_semantic_tokens_are_capped_and_not_retained()
    {
        if (!RequireNode()) return;
        await using var rig = await StartAsync("giant", c => c.Set(LspSettingKeys.InteractiveTimeoutMs, 20_000));
        var baseline = ManagedMb();
        var doc = rig.HostileDoc;
        var completion = rig.Env.Registry.GetProviders<ICompletionProvider>(doc)[0];
        var tokens = rig.Env.Registry.GetProviders<ISemanticTokensProvider>(doc)[0];

        var costC = SyncCost(() => completion.ProvideCompletionAsync(doc, new TextPosition(0, 1), new CompletionContext(CompletionTriggerKind.Invoked), Ct), n: 50);
        Assert.True(costC.P99Ms < 1.0, $"completion call p99 {costC.P99Ms:F3} ms with a 50k-item server");
        var costT = SyncCost(() => tokens.ProvideSemanticTokensAsync(doc, Ct), n: 50);
        Assert.True(costT.P99Ms < 1.0, $"semantic-token call p99 {costT.P99Ms:F3} ms with a 1M-token server");

        // a UI-like pump measures how long its thread is held up while 50k items and 1M tokens are decoded and mapped
        var gaps = new List<double>();
        using var pumpStop = new CancellationTokenSource();
        var pump = new Thread(() =>
        {
            var last = Stopwatch.GetTimestamp();
            while (!pumpStop.IsCancellationRequested)
            {
                Thread.Sleep(2);
                var now = Stopwatch.GetTimestamp();
                lock (gaps) gaps.Add(Stopwatch.GetElapsedTime(last, now).TotalMilliseconds);
                last = now;
            }
        }) { IsBackground = true };
        pump.Start();
        var round1 = await GiantRoundAsync(completion, tokens, doc, 1);
        pumpStop.Cancel();
        pump.Join();
        double worst;
        lock (gaps) worst = gaps.Count == 0 ? 0 : gaps.Max();
        Assert.True(worst < 400, $"the pump thread was held up for {worst:F0} ms at most while the giant results were decoded (they decode on the language lane, not on callers)");
        AssertQueueBounded(rig);

        // What WE retain must be nothing. (System.Text.Json / StreamJsonRpc rent large buffers from the shared ArrayPool while a ~25 MB message
        // is parsed and the pool keeps them for a while, so memory is judged BOUNDED — it must not grow with every round — not back at baseline.)
        var cache = rig.Env.Manager.Slots.First(s => s.Definition.Id == "hostile").Client!.TokenCache;
        long Settle() { for (var i = 0; i < 3; i++) { GC.Collect(2, GCCollectionMode.Forced, true, true); GC.WaitForPendingFinalizers(); } return ManagedMb() - baseline; }
        // (runtime per-thread caches may hold the last completed task for a moment: poll with GCs instead of asserting on the first collection)
        await TestHelpers.EventuallyAsync(() => { Settle(); return !round1.List.IsAlive && !round1.Tokens.IsAlive; },
            () => $"the giant results are released once the consumer drops them (list alive: {round1.List.IsAlive}, tokens alive: {round1.Tokens.IsAlive})", TimeSpan.FromSeconds(30));
        var plateau1 = Settle();
        Assert.Equal(0, cache.Count); // truncated token arrays are never cached as delta bases
        Assert.True(plateau1 < 900, $"retained memory after one giant round: +{plateau1} MB");

        var round2 = await GiantRoundAsync(completion, tokens, doc, 2); // a second identical round must not add to it
        await TestHelpers.EventuallyAsync(() => { Settle(); return !round2.List.IsAlive && !round2.Tokens.IsAlive; }, "second round released", TimeSpan.FromSeconds(30));
        var plateau2 = Settle();
        // Pooled buffers are the runtime's to trim, so this is a sanity bound (the ~25 MB messages cost ~9x while parsed), not a leak check:
        // the leak checks are the weak references above, and cscode.lsp.maxMessageBytes bounds the size of any single message.
        Assert.True(plateau2 < 900, $"memory after two giant rounds: +{plateau2} MB (first round +{plateau1} MB)");
        await AssertGoodServerUnaffectedAsync(rig);
    }

    [Fact]
    public async Task A_message_over_the_size_limit_drops_the_connection_and_the_server_is_restarted()
    {
        if (!RequireNode()) return;
        using var sampler = new PeakMemorySampler();
        var baseline = ManagedMb();
        await using var rig = await StartAsync("giant", c => { c.Set(LspSettingKeys.MaxMessageBytes, 2 * 1024 * 1024); c.Set(LspSettingKeys.InteractiveTimeoutMs, 20_000); });
        var oldPid = rig.HostilePid;
        var tokens = rig.Env.Registry.GetProviders<ISemanticTokensProvider>(rig.HostileDoc)[0];

        // the 1M-token answer is ~25 MB: far over the 2 MB limit, so the client refuses to buffer it
        var result = await tokens.ProvideSemanticTokensAsync(rig.HostileDoc, Ct).Within(TimeSpan.FromSeconds(30));
        Assert.Null(result);
        await TestHelpers.EventuallyAsync(() => rig.Env.Output.Text(Channel).Contains("announced"), () => "the refusal is logged\n" + rig.Env.Dump(), TimeSpan.FromSeconds(30));

        // the misbehaving session is replaced by a fresh one (crash handling), whose small requests work again
        var info = await rig.Env.WaitForAsync(() => rig.Env.Manager.Servers.FirstOrDefault(s => s.Id == "hostile" && s.State == LanguageServerState.Running && s.ProcessId is { } p && p != oldPid),
            () => "restarted after dropping the connection\n" + rig.Env.Dump(), TimeSpan.FromSeconds(30));
        var hover = await UntilAsync(() => rig.Env.Registry.GetProviders<IHoverProvider>(rig.HostileDoc)[0].ProvideHoverAsync(rig.HostileDoc, new TextPosition(0, 0), Ct), 30);
        Assert.Equal("ok", hover!.Contents[0].Value);
        Assert.True(sampler.PeakMb - baseline < 150, $"peak +{sampler.PeakMb - baseline} MB: the oversized message was never buffered");
        Assert.False(Directory.Exists("/proc/" + oldPid) && !File.ReadAllText($"/proc/{oldPid}/stat").Contains(") Z "), "the old process was killed");
        rig.Track();
        await AssertGoodServerUnaffectedAsync(rig);
    }

    // =================================================================================================================
    // (e) never answers
    // =================================================================================================================

    [Fact]
    public async Task A_server_that_never_answers_goes_Unresponsive_does_not_hurt_others_and_can_be_restarted()
    {
        if (!RequireNode()) return;
        await using var rig = await StartAsync("silent", c => c.Set(LspSettingKeys.InteractiveTimeoutMs, 300));
        for (var i = 0; i < 5; i++)
        {
            Assert.Null(await rig.Hover.ProvideHoverAsync(rig.HostileDoc, new TextPosition(0, 0), Ct).Within(TimeSpan.FromSeconds(10)));
            rig.HostileDoc.ApplyEdits([TextEdit.Insert(new TextPosition(0, 0), "x")]);
        }
        await rig.Env.WaitForStateAsync("hostile", LanguageServerState.Unresponsive);
        await AssertGoodServerUnaffectedAsync(rig);
        var cost = SyncCost(() => rig.Hover.ProvideHoverAsync(rig.HostileDoc, new TextPosition(0, 0), Ct));
        Assert.True(cost.P99Ms < 1.0, $"hover call p99 {cost.P99Ms:F3} ms");
        AssertQueueBounded(rig);

        var oldPid = rig.HostilePid;
        rig.Track();
        await rig.Env.Manager.RestartAsync("hostile", Ct); // shutdown is never answered: killed after ShutdownTimeout, then started again
        var info = await rig.Env.WaitForStateAsync("hostile", LanguageServerState.Running, TimeSpan.FromSeconds(30));
        Assert.NotEqual(oldPid, info.ProcessId);
        Assert.False(Directory.Exists("/proc/" + oldPid) && !File.ReadAllText($"/proc/{oldPid}/stat").Contains(") Z "), "the old process was killed");
        rig.Track();
    }

    // =================================================================================================================
    // (f) crashes in a loop
    // =================================================================================================================

    [Fact]
    public async Task A_crash_loop_backs_off_faults_with_one_message_and_reaps_every_orphaned_child()
    {
        if (!RequireNode()) return;
        await using var rig = await StartAsync("crash", c => { c.Set(LspSettingKeys.MaxRestarts, 3); c.Set(LspSettingKeys.RestartBackoffMs, new[] { 20, 40, 80 }); }, waitRunning: false);
        var info = await rig.Env.WaitForStateAsync("hostile", LanguageServerState.Faulted, TimeSpan.FromSeconds(60));
        Assert.NotNull(info.LastError);

        var log = rig.Env.Output.Text(Channel);
        // (the exit description of a crashed session embeds its stderr tail, so the same pid can appear twice: count distinct pids)
        var starts = Regex.Matches(log, @"hostile-server pid=(\d+)").Select(m => int.Parse(m.Groups[1].Value)).Distinct().ToArray();
        Assert.True(starts.Length == 4, "initial start + exactly 3 restarts, but the log shows " + starts.Length + " starts:\n" + rig.Env.Dump()); // initial start + exactly 3 restarts
        var grandchildren = Regex.Matches(log, @"grandchild (\d+)").Select(m => int.Parse(m.Groups[1].Value)).Distinct().ToArray();
        Assert.Equal(4, grandchildren.Length);

        await TestHelpers.EventuallyAsync(() => rig.Env.Window.Messages.Count(m => m.Message.Contains("keeps crashing")) == 1, "exactly one fault message");
        await Task.Delay(300, Ct);
        Assert.Single(rig.Env.Window.Messages, m => m.Message.Contains("keeps crashing"));

        // the re-parented grandchildren (their parent was SIGKILLed) are found by environment marker and killed
        await TestHelpers.EventuallyAsync(() => grandchildren.All(p => !Directory.Exists("/proc/" + p) || File.ReadAllText($"/proc/{p}/stat").Contains(") Z ")),
            () => "orphaned grandchildren reaped: " + string.Join(",", grandchildren.Where(p => Directory.Exists("/proc/" + p))), TimeSpan.FromSeconds(10));
        foreach (var s in starts) rig.Everyone.Add(s);
        AssertQueueBounded(rig);
        await AssertGoodServerUnaffectedAsync(rig);
    }

    // =================================================================================================================
    // across all modes: nothing is left behind
    // =================================================================================================================

    [Fact]
    public async Task Repeated_hostile_sessions_leave_no_threads_or_processes_behind()
    {
        if (!RequireNode()) return;
        GC.Collect(); GC.WaitForPendingFinalizers();
        var threadsBefore = Process.GetCurrentProcess().Threads.Count;
        await TestHelpers.EventuallyAsync(() => LaneThreads() == 0, "no lane threads left over from earlier tests", TimeSpan.FromSeconds(10));
        foreach (var mode in new[] { "cpu", "slow", "silent", "normal" })
        {
            await using var rig = await StartAsync(mode, c => { c.Set(LspSettingKeys.InteractiveTimeoutMs, 300); c.Set(LspSettingKeys.ShutdownTimeoutMs, 500); });
            await TestHelpers.EventuallyAsync(() => mode != "cpu" || ProcessReaper.Descendants(rig.HostilePid).Count >= 2, "burners");
            rig.Track();
            _ = await rig.Hover.ProvideHoverAsync(rig.HostileDoc, new TextPosition(0, 0), Ct).Within(TimeSpan.FromSeconds(10));
        }
        for (var i = 0; i < 5; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); await Task.Delay(100, Ct); }
        // Our own threads are identified by name (thread-pool size is the runtime's business and fluctuates with earlier tests).
        Assert.Equal(0, LaneThreads());
        var threadsAfter = Process.GetCurrentProcess().Threads.Count;
        Assert.True(threadsAfter - threadsBefore <= 24, $"threads {threadsBefore} -> {threadsAfter}");
    }

    /// <summary>Live threads named like the Language lane (Linux: native thread names in /proc).</summary>
    private static int LaneThreads() =>
        Directory.EnumerateDirectories("/proc/self/task").Count(t => { try { return File.ReadAllText(Path.Combine(t, "comm")).StartsWith("cscode-lsp-lane", StringComparison.Ordinal); } catch (IOException) { return false; } });

    private static async Task<T?> UntilAsync<T>(Func<ValueTask<T?>> call, int seconds) where T : class
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            var r = await call().AsTask().Within(TimeSpan.FromSeconds(60));
            if (r is not null) return r;
            await Task.Delay(200, Ct);
        }
        Assert.Fail("the server never produced an answer");
        return null;
    }
}
