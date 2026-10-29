using System.Text.Json;
using CsCode.Core.Platform;
using CsCode.Platform.Io;
using CsCode.Platform.State;
using CsCode.Platform.Tests.Support;
using CsCode.Platform.Workspace;
using CsCode.TestDoubles;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CsCode.Platform.Tests;

public sealed class StateFixture : IDisposable
{
    public TempDirectory Temp { get; } = new("cscode-state-");
    public IoLane Lane { get; } = new("test.state", 2);
    public CapturingLogger<StateStorage> Log { get; } = new();
    public AppPaths Paths { get; }
    public PlatformOptions Options { get; } = new() { StateFlushDelay = TimeSpan.FromMilliseconds(80), StateMaxUnsaved = TimeSpan.FromMilliseconds(600) };
    public string AppFile => Path.Combine(Paths.GlobalStorageDir, "state.json");

    public StateFixture()
    {
        Paths = new AppPaths(k => k == "CSCODE_HOME" ? Temp.Combine("home") : null, HostOs.Linux, "/home/test");
    }

    public StateStorage Create() => new(Paths, Lane, Options, Log);
    public void Dispose() { Lane.Dispose(); Temp.Dispose(); }
}

public sealed record Layout(string Name, int[] Sizes, Dictionary<string, bool> Flags);

public class StateStorageTests : IDisposable
{
    private readonly StateFixture _f = new();
    public void Dispose() => _f.Dispose();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task Eventually(Func<bool> condition, string what, int timeoutMs = 10_000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return;
            await Task.Delay(20, Ct);
        }
        throw new TimeoutException("Timed out waiting for " + what);
    }

    // ---- typed access ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Typed_values_round_trip_and_missing_keys_are_default()
    {
        using var s = _f.Create();
        s.Set("n", 42);
        s.Set("s", "text");
        s.Set("b", true);
        s.Set("layout", new Layout("main", [1, 2, 3], new() { ["x"] = true }));
        s.Set("list", new List<string> { "a", "b" });

        Assert.Equal(42, s.Get<int>("n"));
        Assert.Equal("text", s.Get<string>("s"));
        Assert.True(s.Get<bool>("b"));
        var layout = s.Get<Layout>("layout")!;
        Assert.Equal("main", layout.Name);
        Assert.Equal([1, 2, 3], layout.Sizes);
        Assert.True(layout.Flags["x"]);
        Assert.Equal(["a", "b"], s.Get<List<string>>("list"));
        Assert.Null(s.Get<string>("missing"));
        Assert.Equal(0, s.Get<int>("missing"));
    }

    [Fact]
    public void Overwriting_replaces_and_setting_null_or_Remove_deletes()
    {
        using var s = _f.Create();
        s.Set("k", 1);
        s.Set("k", 2);
        Assert.Equal(2, s.Get<int>("k"));
        s.Set<string>("k", null);
        Assert.Equal(0, s.Get<int>("k"));
        s.Set("x", "y");
        s.Remove("x");
        Assert.Null(s.Get<string>("x"));
        s.Remove("never-there");   // harmless
    }

    [Fact]
    public void A_stored_value_is_a_snapshot_later_mutation_of_the_object_does_not_leak_in()
    {
        using var s = _f.Create();
        var list = new List<int> { 1, 2 };
        s.Set("list", list);
        list.Add(3);
        Assert.Equal([1, 2], s.Get<List<int>>("list"));
    }

    [Fact]
    public void A_value_of_the_wrong_type_reads_as_default_and_warns()
    {
        using var s = _f.Create();
        s.Set("k", "not a number");
        Assert.Equal(0, s.Get<int>("k"));
        Assert.Contains(_f.Log.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public void JsonElement_values_pass_through_unchanged()
    {
        using var s = _f.Create();
        s.Set("raw", JsonDocument.Parse("""{"a":[1,2,{"b":null}]}""").RootElement);
        Assert.Equal(2, s.Get<JsonElement>("raw").GetProperty("a")[1].GetInt32());
    }

    [Fact]
    public void Application_and_workspace_scopes_are_independent()
    {
        using var s = _f.Create();
        s.Set("k", "app");
        s.Set("k", "ws", StorageScope.Workspace);
        Assert.Equal("app", s.Get<string>("k"));
        Assert.Equal("ws", s.Get<string>("k", StorageScope.Workspace));
        s.Remove("k", StorageScope.Workspace);
        Assert.Equal("app", s.Get<string>("k"));
    }

    // ---- persistence -------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Flush_persists_and_a_new_instance_reads_it_back()
    {
        using (var s = _f.Create())
        {
            s.Set("recent", new[] { "a", "b" });
            s.Set("n", 7);
            await s.FlushAsync();
        }
        using var again = _f.Create();
        Assert.Equal(["a", "b"], again.Get<string[]>("recent")!);
        Assert.Equal(7, again.Get<int>("n"));
    }

    [Fact]
    public async Task The_file_is_stable_sorted_indented_json()
    {
        using var s = _f.Create();
        s.Set("b", "x");
        s.Set("a", 1);
        s.Set("c", new[] { 1, 2 });
        await s.FlushAsync();
        Assert.Equal("{\n  \"a\": 1,\n  \"b\": \"x\",\n  \"c\": [\n    1,\n    2\n  ]\n}".ReplaceLineEndings("\n"), File.ReadAllText(_f.AppFile).ReplaceLineEndings("\n"));
    }

    [Fact]
    public async Task Writes_are_debounced_not_issued_per_Set()
    {
        _f.Options.StateFlushDelay = TimeSpan.FromMilliseconds(400);
        _f.Options.StateMaxUnsaved = TimeSpan.FromSeconds(30);
        using var s = _f.Create();
        for (var i = 0; i < 200; i++) s.Set("counter", i);
        Assert.False(File.Exists(_f.AppFile), "Set must not touch the disk");

        await Eventually(() => File.Exists(_f.AppFile), "debounced flush");
        using var again = _f.Create();
        Assert.Equal(199, again.Get<int>("counter")); // one write, with the final value
    }

    [Fact]
    public async Task A_continuously_busy_writer_still_saves_within_the_max_unsaved_window()
    {
        _f.Options.StateFlushDelay = TimeSpan.FromSeconds(3);     // debounce alone would never fire while Sets keep coming
        _f.Options.StateMaxUnsaved = TimeSpan.FromMilliseconds(500);
        using var s = _f.Create();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var i = 0;
        while (sw.ElapsedMilliseconds < 2000 && !File.Exists(_f.AppFile)) { s.Set("tick", i++); await Task.Delay(25, Ct); }
        Assert.True(File.Exists(_f.AppFile), "nothing was saved although changes kept coming for 2 s");
        Assert.True(sw.ElapsedMilliseconds < 1800);
    }

    [Fact]
    public async Task Setting_an_identical_value_does_not_dirty_the_state()
    {
        using var s = _f.Create();
        s.Set("k", "v");
        await s.FlushAsync();
        var stamp = File.GetLastWriteTimeUtc(_f.AppFile);
        await Task.Delay(50, Ct);
        s.Set("k", "v");
        await s.FlushAsync();
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(_f.AppFile));
    }

    [Fact]
    public async Task Sync_Dispose_and_DisposeAsync_both_flush_pending_changes()
    {
        var s1 = _f.Create();
        s1.Set("a", 1);
        s1.Dispose();
        Assert.Equal(1, JsonDocument.Parse(File.ReadAllText(_f.AppFile)).RootElement.GetProperty("a").GetInt32());

        var s2 = _f.Create();
        s2.Set("b", 2);
        await s2.DisposeAsync();
        using var again = _f.Create();
        Assert.Equal(2, again.Get<int>("b"));
        Assert.Equal(1, again.Get<int>("a"));
    }

    [Fact]
    public async Task A_failed_write_keeps_the_state_dirty_and_a_later_flush_succeeds()
    {
        // GlobalStorageDir's parent is a regular FILE, so the directory cannot be created
        var blocker = Path.Combine(_f.Paths.UserDataDir);
        Directory.CreateDirectory(Path.GetDirectoryName(blocker)!);
        File.WriteAllText(blocker, "i am a file, not a directory");
        using var s = _f.Create();
        s.Set("k", "v");
        await s.FlushAsync();                                  // fails quietly: logged, not thrown
        Assert.Contains(_f.Log.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("Could not save state"));

        File.Delete(blocker);
        await s.FlushAsync();
        Assert.True(File.Exists(_f.AppFile));
        using var again = _f.Create();
        Assert.Equal("v", again.Get<string>("k"));
    }

    // ---- corruption --------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_good_file_is_copied_to_bak_before_it_is_replaced()
    {
        using var s = _f.Create();
        s.Set("v", 1);
        await s.FlushAsync();
        Assert.False(File.Exists(_f.AppFile + ".bak"));
        s.Set("v", 2);
        await s.FlushAsync();
        Assert.Equal(1, JsonDocument.Parse(File.ReadAllText(_f.AppFile + ".bak")).RootElement.GetProperty("v").GetInt32()); // previous good state
        Assert.Equal(2, JsonDocument.Parse(File.ReadAllText(_f.AppFile)).RootElement.GetProperty("v").GetInt32());
    }

    [Fact]
    public async Task A_corrupt_file_is_recovered_from_the_bak_and_the_corrupt_copy_is_kept()
    {
        using (var s = _f.Create())
        {
            s.Set("precious", "data");
            await s.FlushAsync();
            s.Set("precious", "data2");
            await s.FlushAsync();                              // now there is a .bak holding "data"
        }
        File.WriteAllText(_f.AppFile, "{ this is truncated garba");

        using var recovered = _f.Create();
        Assert.Equal("data", recovered.Get<string>("precious"));
        Assert.True(File.Exists(_f.AppFile + ".corrupt"));
        Assert.Contains(_f.Log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("corrupt"));

        await recovered.FlushAsync();                          // a healthy main file is written again
        Assert.Equal("data", JsonDocument.Parse(File.ReadAllText(_f.AppFile)).RootElement.GetProperty("precious").GetString());
    }

    [Fact]
    public async Task A_corrupt_file_without_a_usable_bak_starts_empty_without_throwing()
    {
        Directory.CreateDirectory(_f.Paths.GlobalStorageDir);
        File.WriteAllText(_f.AppFile, "\0\0\0 not json");
        File.WriteAllText(_f.AppFile + ".bak", "also not json");
        using var s = _f.Create();
        Assert.Null(s.Get<string>("anything"));
        Assert.Contains(_f.Log.Entries, e => e.Level == LogLevel.Error);
        s.Set("fresh", 1);
        await s.FlushAsync();
        Assert.Equal(1, JsonDocument.Parse(File.ReadAllText(_f.AppFile)).RootElement.GetProperty("fresh").GetInt32());
    }

    [Fact]
    public async Task A_corrupt_main_file_never_overwrites_a_good_bak()
    {
        using (var s = _f.Create())
        {
            s.Set("k", "good1"); await s.FlushAsync();
            s.Set("k", "good2"); await s.FlushAsync();         // .bak = good1
        }
        File.WriteAllText(_f.AppFile, "garbage");
        using var s2 = _f.Create();                            // recovers good1 from .bak
        s2.Set("k", "good3");
        await s2.FlushAsync();
        // the garbage main was NOT copied over the .bak
        Assert.Equal("good1", JsonDocument.Parse(File.ReadAllText(_f.AppFile + ".bak")).RootElement.GetProperty("k").GetString());
    }

    [Theory]
    [InlineData("[1,2,3]")]
    [InlineData("\"str\"")]
    [InlineData("")]
    public void A_file_whose_root_is_not_an_object_is_treated_as_corrupt(string content)
    {
        Directory.CreateDirectory(_f.Paths.GlobalStorageDir);
        File.WriteAllText(_f.AppFile, content);
        using var s = _f.Create();
        Assert.Null(s.Get<string>("x"));
    }

    // ---- workspace scope ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Workspace_state_lives_in_a_per_folder_directory_and_switches_with_the_folder()
    {
        var folderA = FileUri.FromDirectoryPath(_f.Temp.Combine("projA"));
        var folderB = FileUri.FromDirectoryPath(_f.Temp.Combine("projB"));
        using var s = _f.Create();
        var bound = (IWorkspaceBound)s;

        await bound.OnWorkspaceFoldersChangedAsync([folderA], Ct);
        s.Set("open", new[] { "a.cs" }, StorageScope.Workspace);
        await bound.OnWorkspaceFoldersChangedAsync([folderB], Ct);          // saves A, loads B
        Assert.Null(s.Get<string[]>("open", StorageScope.Workspace));
        s.Set("open", new[] { "b.cs" }, StorageScope.Workspace);
        await bound.OnWorkspaceFoldersChangedAsync([folderA], Ct);
        Assert.Equal(["a.cs"], s.Get<string[]>("open", StorageScope.Workspace)!);

        var fileA = Path.Combine(_f.Paths.WorkspaceStorageDir(folderA)!, "state.json");
        var fileB = Path.Combine(_f.Paths.WorkspaceStorageDir(folderB)!, "state.json");
        Assert.NotEqual(fileA, fileB);
        Assert.True(File.Exists(fileA) && File.Exists(fileB));
    }

    [Fact]
    public async Task Workspace_state_survives_a_restart()
    {
        var folder = FileUri.FromDirectoryPath(_f.Temp.Combine("proj"));
        using (var s = _f.Create())
        {
            await ((IWorkspaceBound)s).OnWorkspaceFoldersChangedAsync([folder], Ct);
            s.Set("layout", "tabs", StorageScope.Workspace);
        }                                                       // Dispose flushes
        using var again = _f.Create();
        await ((IWorkspaceBound)again).OnWorkspaceFoldersChangedAsync([folder], Ct);
        Assert.Equal("tabs", again.Get<string>("layout", StorageScope.Workspace));
    }

    [Fact]
    public async Task Without_a_folder_the_workspace_scope_works_in_memory_and_writes_nothing()
    {
        using var s = _f.Create();
        s.Set("k", "v", StorageScope.Workspace);
        Assert.Equal("v", s.Get<string>("k", StorageScope.Workspace));
        await s.FlushAsync();
        Assert.False(Directory.Exists(Path.Combine(_f.Paths.UserDataDir, "workspaceStorage")));
        await ((IWorkspaceBound)s).OnWorkspaceFoldersChangedAsync([], Ct);
        Assert.Null(s.Get<string>("k", StorageScope.Workspace));
    }

    // ---- threading & lifetime ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Flush_does_not_block_the_caller_when_the_io_lane_is_saturated()
    {
        using var lane = new IoLane("test.sat", 1);
        using var s = new StateStorage(_f.Paths, lane, _f.Options);
        s.Set("k", 1);
        using var gate = new ManualResetEventSlim();
        using var started = new ManualResetEventSlim();
        var blocker = lane.RunAsync(_ => { started.Set(); gate.Wait(); }, Ct);
        Assert.True(started.Wait(TimeSpan.FromSeconds(5), Ct));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var flush = s.FlushAsync();
        sw.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromMilliseconds(100));
        Assert.False(flush.IsCompleted);
        Assert.False(File.Exists(_f.AppFile));
        gate.Set();
        await flush.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.True(File.Exists(_f.AppFile));
        await blocker;
    }

    [Fact]
    public async Task Parallel_get_set_remove_flush_have_no_exceptions_and_end_consistent()
    {
        using var s = _f.Create();
        var errors = new System.Collections.Concurrent.ConcurrentBag<Exception>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var tasks = Enumerable.Range(0, 4).Select(w => Task.Run(async () =>
        {
            try
            {
                var i = 0;
                while (!cts.IsCancellationRequested)
                {
                    s.Set($"k{w}", i++);
                    _ = s.Get<int>($"k{(w + 1) % 4}");
                    if (i % 50 == 0) s.Remove($"tmp{w}");
                    if (i % 100 == 0) await s.FlushAsync();
                }
                s.Set($"final{w}", w);
            }
            catch (Exception ex) { errors.Add(ex); }
        }, Ct)).ToArray();
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(30), Ct);
        await s.FlushAsync();
        Assert.Empty(errors);

        using var again = _f.Create();
        for (var w = 0; w < 4; w++) Assert.Equal(w, again.Get<int>($"final{w}"));
    }

    [Fact]
    public void A_disposed_storage_is_not_retained()
    {
        LeakAssert.NotRetained(() =>
        {
            var s = _f.Create();
            s.Set("k", 1);
            s.Dispose();
            return [s];
        });
    }
}
