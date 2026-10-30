using System.Diagnostics;
using Avalonia.Headless.XUnit;
using CsCode.Core.Platform;
using CsCode.Editor.Tests.Support;
using CsCode.Editor.TextMate;
using CsCode.Editor.TreeSitter;
using CsCode.Editor.TreeSitter.Grammars;
using CsCode.Editor.TreeSitter.Highlighting;
using CsCode.Editor.TreeSitter.Isolation;
using CsCode.TestDoubles;
using Xunit;
using static CsCode.Editor.Tests.TreeSitter.TreeSitterTestSupport;
using static CsCode.Editor.Tests.TreeSitter.TreeSitterViewTests;

namespace CsCode.Editor.Tests.TreeSitter;

/// <summary>
/// Crash and hang isolation (docs/TREE-SITTER.md, "Isolation"): the flight recorder that survives a crash, the quarantine that keeps a grammar
/// that took the process down out of the next one, the watchdog that sees a grammar call that never returns and moves tree-sitter to a fresh
/// lane, and the query guard. Serial: the thresholds and the test hook are process-wide.
/// </summary>
[Collection(TreeSitterBenchCollection.Name)]
public class TreeSitterIsolationTests
{
    /// <summary>A pid no process has right now (a process that just ended).</summary>
    private static int DeadPid()
    {
        using var p = Process.Start(new ProcessStartInfo("true") { UseShellExecute = false })!;
        p.WaitForExit();
        return p.Id;
    }

    /// <summary>Leaves behind what a process that died inside <paramref name="grammar"/> leaves: its flight file, state "inside".</summary>
    private static void LeaveCrashRecord(string flightDir, string grammar, string identity)
    {
        using var scratch = new TempDirectory("cscode-ts-flight-");
        var recorder = FlightRecorder.Open(scratch.Path);
        var scope = recorder.Enter(FlightRecorder.RecordFor(grammar, identity));
        var bytes = File.ReadAllBytes(recorder.FilePath!); // what the kernel holds for the mapping when the process dies here
        _ = scope;
        recorder.Dispose();
        BitConverter.TryWriteBytes(bytes.AsSpan(8), DeadPid()); // the owner: a process that is gone
        BitConverter.TryWriteBytes(bytes.AsSpan(24), 0L);
        Directory.CreateDirectory(flightDir);
        File.WriteAllBytes(Path.Combine(flightDir, Guid.NewGuid().ToString("N") + ".flight"), bytes);
    }

    [Fact]
    public void The_flight_recorder_says_which_grammar_the_lane_is_in_and_restores_the_outer_one()
    {
        using var dir = new TempDirectory("cscode-ts-flight-");
        using var recorder = FlightRecorder.Open(dir.Path);
        Assert.NotNull(recorder.FilePath);
        Assert.False(recorder.Current.Inside);
        using (recorder.Enter(FlightRecorder.RecordFor("markdown", "aaaa")))
        {
            Assert.Equal(("markdown", "aaaa"), (recorder.Current.Grammar, recorder.Current.Identity));
            using (recorder.Enter(FlightRecorder.RecordFor("rust", "bbbb")))
                Assert.Equal("rust", recorder.Current.Grammar); // an injected grammar inside the host's highlight
            Assert.Equal("markdown", recorder.Current.Grammar);
            Assert.True(recorder.Current.Inside);
        }
        Assert.False(recorder.Current.Inside);
        // a live process's file is never collected; a clean exit removes it
        Assert.Empty(FlightRecorder.CollectCrashes(dir.Path));
        var path = recorder.FilePath!;
        recorder.Dispose();
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void A_process_that_died_inside_a_grammar_is_found_once_and_a_clean_one_is_not()
    {
        using var dir = new TempDirectory("cscode-ts-flight-");
        LeaveCrashRecord(dir.Path, "toml", "1234567890abcdef");
        // a process that ended outside native code: its file says "not inside"
        using (var scratch = new TempDirectory("cscode-ts-flight-"))
        {
            var clean = FlightRecorder.Open(scratch.Path);
            var bytes = File.ReadAllBytes(clean.FilePath!);
            clean.Dispose();
            BitConverter.TryWriteBytes(bytes.AsSpan(8), DeadPid());
            File.WriteAllBytes(Path.Combine(dir.Path, "clean.flight"), bytes);
        }
        var crashes = FlightRecorder.CollectCrashes(dir.Path);
        Assert.Equal([("toml", "1234567890abcdef")], crashes);
        Assert.Empty(Directory.GetFiles(dir.Path)); // both collected
        Assert.Empty(FlightRecorder.CollectCrashes(dir.Path)); // reported once
    }

    /// <summary>
    /// editor-2: another CsCode process (background mode runs several on one user data folder) must never collect a LIVE process's flight
    /// file. It used to decide by comparing the pid's start time to the tick, which .NET computes per process on Linux (1.7 ms apart in the
    /// review's probe): a live process looked dead, its file was deleted and, while it was inside a grammar, a crash was counted against it.
    /// </summary>
    [Fact]
    public void A_live_process_s_flight_file_is_never_collected_whatever_its_start_time_looks_like_from_here()
    {
        using var dir = new TempDirectory("cscode-ts-flight-");
        using var other = Process.Start(new ProcessStartInfo("sleep", "30") { UseShellExecute = false })!; // stands in for the other process
        try
        {
            using var recorder = FlightRecorder.Open(dir.Path);
            using (recorder.Enter(FlightRecorder.RecordFor("toml", "probe-identity")))
            {
                // the file as another process sees it: owned by a live pid, with a start time this process computes slightly differently
                using (var fs = new FileStream(recorder.FilePath!, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
                {
                    fs.Position = 8;
                    fs.Write(BitConverter.GetBytes(other.Id));
                    fs.Position = 24;
                    fs.Write(BitConverter.GetBytes(other.StartTime.ToUniversalTime().Ticks + 17_000));
                }
                Assert.Empty(FlightRecorder.CollectCrashes(dir.Path));
                Assert.True(File.Exists(recorder.FilePath), "a live process's flight file was taken away");
            }

            // a file nobody holds is an owner that is gone, even when its pid now belongs to another (live) process
            var orphan = Path.Combine(dir.Path, "orphan.flight");
            var bytes = File.ReadAllBytes(recorder.FilePath!);
            BitConverter.TryWriteBytes(bytes.AsSpan(12), 1); // "inside"
            File.WriteAllBytes(orphan, bytes);
            Assert.Equal([("toml", "probe-identity")], FlightRecorder.CollectCrashes(dir.Path));
            Assert.False(File.Exists(orphan));
            Assert.True(File.Exists(recorder.FilePath));
        }
        finally
        {
            other.Kill();
            other.WaitForExit();
        }
    }

    /// <summary>
    /// editor-9: two processes share quarantine.json. A clear in one of them used to be undone by the other's next record (it merged its stale
    /// entries back into the file), and the other one kept refusing the grammar anyway.
    /// </summary>
    [Fact]
    public void A_clear_in_one_process_is_not_undone_by_another_and_is_honoured_there()
    {
        using var dir = new TempDirectory("cscode-ts-quarantine-");
        var file = Path.Combine(dir.Path, "quarantine.json");
        var a = new GrammarQuarantine(file); // process A
        Assert.True(a.RecordHang("erlang", "id1", "test"));
        var b = new GrammarQuarantine(file); // process B
        Assert.NotNull(b.Reason("erlang", "id1"));
        b.Clear();
        Thread.Sleep(20); // a distinct modification time on coarse clocks
        Assert.Null(a.Reason("erlang", "id1")); // A honours B's clear
        a.RecordCrash("toml", "id2"); // and A's next record does not bring erlang back
        var next = new GrammarQuarantine(file); // a new process
        Assert.Null(next.Reason("erlang", "id1"));
        Assert.Single(next.Entries, e => e.Grammar == "toml");
        Assert.Null(b.Reason("erlang", "id1"));
    }

    /// <summary>
    /// editor-9 (review): the registry consulted the quarantine only on a grammar's first lookup and kept the refusal, so a clear in another
    /// process (or a clear the registry was not told about) left the grammar refused here until restart.
    /// </summary>
    [Fact]
    public void The_registry_takes_a_grammar_again_once_another_process_cleared_the_quarantine()
    {
        RequireGrammar("toml");
        using var dir = new TempDirectory("cscode-ts-quarantine-");
        var file = Path.Combine(dir.Path, "quarantine.json");
        var locator = GrammarLocator.CreateDefault(null);
        var identity = GrammarIdentity.Of(locator.Find("toml")!);
        var mine = new GrammarQuarantine(file); // process A
        mine.RecordHang("toml", identity, "A hung in it");
        using var registry = new GrammarRegistry(locator, TreeSitterLanguages.RootScopeFor) { Quarantine = mine };
        Assert.Null(registry.Get("toml", out var error));
        Assert.Contains("quarantined", error, StringComparison.Ordinal);
        Assert.Null(registry.Get("toml", out _)); // (remembered)

        Thread.Sleep(20); // a distinct modification time on coarse clocks
        new GrammarQuarantine(file).Clear(); // process B: Clear Quarantined Tree-sitter Grammars
        Assert.NotNull(registry.Get("toml", out error));
        Assert.Null(error);
    }

    /// <summary>
    /// ...and the documents of process A that went to TextMate come back without A running the command: its watchdog sees the clear (the
    /// quarantine's generation moved on), and new documents of the language get tree-sitter as well.
    /// </summary>
    [AvaloniaFact]
    public async Task A_clear_in_another_process_brings_tree_sitter_back_for_open_and_new_documents()
    {
        RequireGrammar("toml");
        using var dir = new TempDirectory("cscode-ts-isolation-");
        var file = Path.Combine(dir.Path, "quarantine.json");
        var identity = GrammarIdentity.Of(GrammarLocator.CreateDefault(null).Find("toml")!);
        new GrammarQuarantine(file).RecordHang("toml", identity, "process A hung in it earlier");
        using var host = ViewHost.Create();
        host.Factory.Tokens.TreeSitter.IsolationDirectoryOverride = dir.Path;
        var (_, open, _) = await host.OpenAsync("[a]\nb = 1\n", "a.toml");
        await ViewHost.WaitFor(() => open.Tokens is { TreeSitterWanted: false, TreeSitter: null }, "a.toml handed back (quarantined)");

        Thread.Sleep(20);
        new GrammarQuarantine(file).Clear(); // process B
        await WaitForTreeSitter(open, 1); // within a watchdog tick
        var (_, later, _) = await host.OpenAsync("[c]\nd = 2\n", "b.toml");
        await WaitForTreeSitter(later, 1);
    }

    [Fact]
    public void A_quarantine_file_of_the_first_format_is_still_read()
    {
        using var dir = new TempDirectory("cscode-ts-quarantine-");
        var file = Path.Combine(dir.Path, "quarantine.json");
        File.WriteAllText(file, """[ { "Grammar": "toml", "Identity": "id1", "Crashes": 0, "Hangs": 1, "LastUtc": "2026-10-30T00:00:00Z" } ]""");
        var q = new GrammarQuarantine(file);
        Assert.NotNull(q.Reason("toml", "id1"));
        q.Clear();
        Assert.Null(new GrammarQuarantine(file).Reason("toml", "id1"));
    }

    [Fact]
    public void The_quarantine_refuses_a_grammar_build_after_two_crashes_or_one_hang_and_persists()
    {
        using var dir = new TempDirectory("cscode-ts-quarantine-");
        var file = Path.Combine(dir.Path, "quarantine.json");
        var q = new GrammarQuarantine(file);
        Assert.False(q.RecordCrash("toml", "id1")); // one crash may be the user killing a busy process
        Assert.Null(q.Reason("toml", "id1"));
        Assert.True(q.RecordCrash("toml", "id1"));
        Assert.Contains("closed unexpectedly 2 times", q.Reason("toml", "id1"), StringComparison.Ordinal);
        Assert.Null(q.Reason("toml", "id2")); // a new build of the grammar starts clean
        Assert.True(q.RecordHang("nix", "id3", "a call did not return for 12 s"));
        Assert.Contains("stopped responding", q.Reason("nix", "id3"), StringComparison.Ordinal);

        var reloaded = new GrammarQuarantine(file); // the next process
        Assert.NotNull(reloaded.Reason("toml", "id1"));
        Assert.NotNull(reloaded.Reason("nix", "id3"));
        reloaded.Clear();
        Assert.Null(new GrammarQuarantine(file).Reason("toml", "id1"));
    }

    [Fact]
    public void The_registry_refuses_a_quarantined_grammar_with_the_reason()
    {
        RequireGrammar("toml");
        var locator = GrammarLocator.CreateDefault(null);
        var identity = GrammarIdentity.Of(locator.Find("toml")!);
        var quarantine = new GrammarQuarantine(null);
        quarantine.RecordHang("toml", identity, "test");
        using var registry = new GrammarRegistry(locator, TreeSitterLanguages.RootScopeFor) { Quarantine = quarantine };
        Assert.Null(registry.Get("toml", out var error));
        Assert.Contains("quarantined", error, StringComparison.Ordinal);
        Assert.NotNull(registry.Get("json", out _));
    }

    /// <summary>
    /// The whole crash path: two earlier processes died inside the TOML grammar (their flight files say so). On the next start TOML is not
    /// coloured by tree-sitter, the user is told why, and the command that clears the quarantine brings it back.
    /// </summary>
    [AvaloniaFact]
    public async Task After_two_crashes_inside_a_grammar_the_next_start_leaves_it_alone_and_tells_the_user()
    {
        RequireGrammar("toml");
        using var dir = new TempDirectory("cscode-ts-isolation-");
        var identity = GrammarIdentity.Of(GrammarLocator.CreateDefault(null).Find("toml")!);
        LeaveCrashRecord(Path.Combine(dir.Path, "flight"), "toml", identity);
        LeaveCrashRecord(Path.Combine(dir.Path, "flight"), "toml", identity);

        using var host = ViewHost.Create();
        host.Factory.Tokens.TreeSitter.IsolationDirectoryOverride = dir.Path;
        var (_, toml, _) = await host.OpenAsync("[a]\nb = 1\n", "a.toml");
        await ViewHost.WaitFor(() => toml.Tokens is { TreeSitterWanted: false }, "TOML handed back (quarantined)");
        Assert.Null(toml.Tokens!.TreeSitter);
        await ViewHost.WaitFor(() => host.Window.Messages.Any(m => m.Message.Contains("'toml'", StringComparison.Ordinal)), "the user is told");
        Assert.Equal(MessageSeverity.Warning, host.Window.Messages.First(m => m.Message.Contains("'toml'", StringComparison.Ordinal)).Severity);
        Assert.True(File.Exists(Path.Combine(dir.Path, "quarantine.json")));
        // this process's own recorder is in place for the next start
        Assert.Single(Directory.GetFiles(Path.Combine(dir.Path, "flight"), "*.flight"));

        await host.Commands.ExecuteAsync(TokenizationManager.ClearQuarantineCommand);
        await ViewHost.WaitFor(() => toml.Tokens?.TreeSitter is { CompletedParses: > 0 }, "tree-sitter again after the quarantine was cleared");
    }

    /// <summary>
    /// The hang path: a parse that does not return (stood in for by the test hook: a real one is an endless loop in a grammar's scanner) is seen
    /// by the watchdog, the grammar is quarantined, tree-sitter moves to a fresh lane, the document goes back to TextMate, and other languages keep
    /// tree-sitter — on the new lane. The editor worker (TextMate, folding…) never waits for any of it.
    /// </summary>
    [AvaloniaFact]
    public async Task A_grammar_that_never_returns_is_quarantined_and_tree_sitter_carries_on_in_a_new_lane()
    {
        RequireGrammar("erlang");
        RequireGrammar("kotlin");
        var previous = TreeSitterService.HangThreshold;
        TreeSitterService.HangThreshold = TimeSpan.FromMilliseconds(1500);
        using var release = new ManualResetEventSlim();
        SyntaxDocument.HangHookForTests = grammar =>
        {
            if (grammar == "erlang") release.Wait(TimeSpan.FromSeconds(30)); // "never" returns (until the test ends)
        };
        try
        {
            using var dir = new TempDirectory("cscode-ts-isolation-");
            using var host = ViewHost.Create();
            var service = host.Factory.Tokens.TreeSitter;
            service.IsolationDirectoryOverride = dir.Path;
            var (_, erl, _) = await host.OpenAsync("-module(a).\nf() -> ok.\n", "a.erl");
            var hungLane = service.Lane;
            await ViewHost.WaitFor(() => service.AbandonedLanes == 1, "the watchdog gives up the lane", 20_000);
            await ViewHost.WaitFor(() => erl.Tokens is { TreeSitterWanted: false, TreeSitter: null }, "the Erlang document is handed back");
            Assert.NotSame(hungLane, service.Lane);
            Assert.NotNull(service.Quarantine!.Reason("erlang", service.Quarantine.Entries.Single().Identity));
            Assert.Contains(host.Window.Messages, m => m.Message.Contains("'erlang' stopped responding", StringComparison.Ordinal));

            // other grammars carry on, on the new lane (the hung thread is still inside "erlang")
            var (_, kt, _) = await host.OpenAsync("fun main() {}\n", "Main.kt");
            await WaitForTreeSitter(kt, 0);
            Assert.Equal(service.Lane.Thread.ManagedThreadId, kt.Tokens!.TreeSitter!.LastSliceThreadId);
            Assert.NotEqual(hungLane.Thread.ManagedThreadId, kt.Tokens.TreeSitter.LastSliceThreadId);
            // and a new Erlang document is not given to the quarantined grammar again
            var (_, erl2, _) = await host.OpenAsync("-module(b).\n", "b.erl");
            await ViewHost.WaitFor(() => erl2.Tokens is { TreeSitterWanted: false }, "quarantined for new documents too");
        }
        finally
        {
            release.Set();
            SyntaxDocument.HangHookForTests = null;
            TreeSitterService.HangThreshold = previous;
        }
    }

    /// <summary>editor-8: the command used to do nothing before this process's first tree-sitter document (nothing had loaded the quarantine yet).</summary>
    [AvaloniaFact]
    public async Task Clearing_the_quarantine_before_the_first_tree_sitter_document_takes_effect()
    {
        RequireGrammar("toml");
        using var dir = new TempDirectory("cscode-ts-isolation-");
        var file = Path.Combine(dir.Path, "quarantine.json");
        var identity = GrammarIdentity.Of(GrammarLocator.CreateDefault(null).Find("toml")!);
        new GrammarQuarantine(file).RecordHang("toml", identity, "an earlier process hung in it");

        using var host = ViewHost.Create();
        host.Factory.Tokens.TreeSitter.IsolationDirectoryOverride = dir.Path;
        await host.Commands.ExecuteAsync(TokenizationManager.ClearQuarantineCommand); // first thing after the start
        await ViewHost.WaitFor(() => new GrammarQuarantine(file).Reason("toml", identity) is null, "quarantine.json cleared");
        var (_, toml, _) = await host.OpenAsync("[a]\nb = 1\n", "a.toml");
        await WaitForTreeSitter(toml, 1);
    }

    /// <summary>
    /// editor-10: the isolation set-up reads and writes files (the quarantine, earlier processes' flight files, this process's recorder) on the
    /// lane, but it used to hold the lock the UI thread takes to schedule every tree-sitter document while doing it, exactly while the first
    /// one lays out. Held up here by a hook, as slow storage would.
    /// </summary>
    [AvaloniaFact]
    public async Task The_UI_thread_never_waits_for_the_isolation_file_work()
    {
        RequireGrammar("toml");
        using var dir = new TempDirectory("cscode-ts-isolation-");
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var host = ViewHost.Create();
        var service = host.Factory.Tokens.TreeSitter;
        service.IsolationDirectoryOverride = dir.Path;
        service.IsolationSetupHookForTests = () =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(30));
        };
        try
        {
            var (_, toml, _) = await host.OpenAsync("[a]\nb = 1\n", "a.toml");
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)), "the lane started the isolation set-up");
            // the lane is inside the file work now; the UI thread schedules the document (what every edit and viewport report does)
            var clock = Stopwatch.StartNew();
            _ = service.Lane;
            toml.Controller.SetSelection(CsCode.Core.Text.TextRange.At(1, 5));
            ViewTestHelpers.Type(toml, "0");
            ViewHost.Pump();
            Assert.True(clock.ElapsedMilliseconds < 5_000, $"the UI thread waited {clock.ElapsedMilliseconds} ms for the lane's file work");
        }
        finally
        {
            release.Set();
        }
        await WaitForTreeSitter((await host.OpenAsync("[c]\nd = 2\n", "c.toml")).View, 1);
    }

    [AvaloniaFact]
    public async Task The_flight_recorder_file_lives_as_long_as_the_engine_and_a_clean_shutdown_removes_it()
    {
        RequireGrammar("toml");
        using var dir = new TempDirectory("cscode-ts-isolation-");
        var file = Path.Combine(dir.Path, "flight", Environment.ProcessId + ".flight");
        var host = ViewHost.Create();
        try
        {
            host.Factory.Tokens.TreeSitter.IsolationDirectoryOverride = dir.Path;
            var (_, toml, _) = await host.OpenAsync("[a]\nb = 1\n", "a.toml");
            await WaitForTreeSitter(toml, 1);
            Assert.True(File.Exists(file));
            // the files (quarantine, crash records, this recorder) and the native runtime were touched on the lane, never the UI thread
            var service = host.Factory.Tokens.TreeSitter;
            Assert.Equal(service.Lane.Thread.ManagedThreadId, service.RegistryCreatedOnThread);
            Assert.NotEqual(Environment.CurrentManagedThreadId, service.RegistryCreatedOnThread);
            Assert.Empty(FlightRecorder.CollectCrashes(Path.Combine(dir.Path, "flight"))); // a live process is never "crashed"
        }
        finally
        {
            host.Dispose();
        }
        await ViewHost.WaitFor(() => !File.Exists(file), "the recorder removed on shutdown (on the lane)");
    }

    [AvaloniaFact]
    public async Task A_document_whose_highlight_queries_keep_running_into_the_query_limit_goes_to_TextMate()
    {
        RequireGrammar("rust");
        var previous = SyntaxDocument.MaxQueryTime;
        try
        {
            using var host = ViewHost.Create();
            host.Config.Set(TreeSitterService.SettingKey, TreeSitterService.TreeSitter);
            SyntaxDocument.MaxQueryTime = TimeSpan.Zero; // every highlight pass is stopped at once
            var body = string.Concat(Enumerable.Range(0, 300).Select(i => $"fn f{i}(a: i32) -> i32 {{ a * {i} }}\n"));
            var (_, view, _) = await host.OpenAsync(body, "slow.rs");
            await ViewHost.WaitFor(() => view.Tokens is { TreeSitterGaveUp: true, TreeSitter: null } t && t.Store.ValidLines > 10, "TextMate takes over", 30_000);
            Assert.Equal("TextMate", view.SyntaxEngine);
        }
        finally
        {
            SyntaxDocument.MaxQueryTime = previous;
        }
    }

    [Fact]
    public void The_query_guard_stops_a_highlight_pass_and_says_so()
    {
        var bundle = RequireGrammar("rust");
        var previous = SyntaxDocument.MaxQueryTime;
        try
        {
            using var registry = NewRegistry();
            using var doc = new SyntaxDocument(registry, bundle);
            var text = new StringInput(string.Concat(Enumerable.Range(0, 2000).Select(i => $"fn f{i}() {{ let x = {i}; }}\n")));
            Assert.Equal(ParseOutcome.Completed, doc.Parse(text, long.MaxValue));
            var paint = new PaintBuffer();
            doc.Highlight(text, 0, text.Length, new TestStacks(), paint);
            Assert.False(doc.LastQueryTimedOut);
            SyntaxDocument.MaxQueryTime = TimeSpan.Zero;
            var partial = doc.Highlight(text, 0, text.Length, new TestStacks(), paint);
            Assert.True(doc.LastQueryTimedOut);
            SyntaxDocument.MaxQueryTime = previous;
            var full = doc.Highlight(text, 0, text.Length, new TestStacks(), paint);
            Assert.False(doc.LastQueryTimedOut);
            Assert.True(partial < full, $"stopped pass painted {partial} captures, a full one {full}");
        }
        finally
        {
            SyntaxDocument.MaxQueryTime = previous;
        }
    }
}
