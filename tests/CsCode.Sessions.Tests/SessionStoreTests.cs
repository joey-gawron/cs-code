using CsCode.TestDoubles;
using Xunit;

namespace CsCode.Sessions.Tests;

public class SessionStoreTests
{
    [Fact]
    public void Save_then_load_returns_the_session_and_keeps_the_previous_version_as_bak()
    {
        using var dir = new TempDirectory("cscode-sess-");
        var store = new SessionStore(dir.Path);
        store.Save(SessionSerializerTests.Sample() with { ActiveEditor = "file:///first" });
        store.Save(SessionSerializerTests.Sample() with { ActiveEditor = "file:///second" });

        var loaded = store.Load("work");
        Assert.Equal(SessionLoadStatus.Loaded, loaded.Status);
        Assert.Equal("file:///second", loaded.Session!.ActiveEditor);
        Assert.True(File.Exists(store.PathOf("work") + ".bak"));
        Assert.Equal("file:///first", SessionSerializer.Deserialize(File.ReadAllBytes(store.PathOf("work") + ".bak")).ActiveEditor);
        Assert.Empty(Directory.GetFiles(store.SessionsDir, "*.tmp")); // no temp file left behind
    }

    [Fact]
    public void A_corrupt_session_file_falls_back_to_the_previous_version()
    {
        using var dir = new TempDirectory("cscode-sess-");
        var store = new SessionStore(dir.Path);
        store.Save(SessionSerializerTests.Sample() with { ActiveEditor = "file:///good" });
        store.Save(SessionSerializerTests.Sample() with { ActiveEditor = "file:///newer" });
        File.WriteAllText(store.PathOf("work"), "{ \"version\": 2, \"name\": \"wo"); // torn by something other than this store

        var loaded = store.Load("work");
        Assert.Equal(SessionLoadStatus.RecoveredFromBackup, loaded.Status);
        Assert.Equal("file:///good", loaded.Session!.ActiveEditor);
        Assert.Contains("unreadable", loaded.Message);
    }

    [Fact]
    public void A_missing_main_file_with_a_backup_recovers_and_both_unreadable_is_corrupt()
    {
        using var dir = new TempDirectory("cscode-sess-");
        var store = new SessionStore(dir.Path);
        store.Save(SessionSerializerTests.Sample());
        store.Save(SessionSerializerTests.Sample());
        File.Delete(store.PathOf("work"));
        Assert.Equal(SessionLoadStatus.RecoveredFromBackup, store.Load("work").Status);

        File.WriteAllText(store.PathOf("work"), "garbage");
        File.WriteAllText(store.PathOf("work") + ".bak", "garbage too");
        var corrupt = store.Load("work");
        Assert.Equal(SessionLoadStatus.Corrupt, corrupt.Status);
        Assert.Null(corrupt.Session);
        Assert.True(File.Exists(store.PathOf("work")), "a corrupt file is kept for inspection, not deleted");
    }

    [Fact]
    public void A_session_written_by_a_newer_build_is_not_loaded_and_never_overwritten()
    {
        using var dir = new TempDirectory("cscode-sess-");
        var store = new SessionStore(dir.Path);
        Directory.CreateDirectory(store.SessionsDir);
        File.WriteAllText(store.PathOf("future"), """{ "version": 42, "name": "future", "somethingNew": true }""");

        Assert.Equal(SessionLoadStatus.TooNew, store.Load("future").Status);
        Assert.Throws<InvalidOperationException>(() => store.Save(new SessionDocument { Name = "future" }));
        Assert.Contains("somethingNew", File.ReadAllText(store.PathOf("future")));
    }

    [Fact]
    public void Missing_sessions_are_reported_as_missing()
    {
        using var dir = new TempDirectory("cscode-sess-");
        var store = new SessionStore(dir.Path);
        Assert.Equal(SessionLoadStatus.Missing, store.Load("nope").Status);
        Assert.Equal(SessionLoadStatus.Missing, store.Load("../escape").Status);
        Assert.False(store.Exists("nope"));
        Assert.Empty(store.List());
    }

    [Fact]
    public void List_orders_by_last_use_and_includes_unreadable_sessions_by_name()
    {
        using var dir = new TempDirectory("cscode-sess-");
        var store = new SessionStore(dir.Path);
        store.Save(new SessionDocument { Name = "old", LastUsedUtc = DateTimeOffset.UnixEpoch.AddDays(1) });
        store.Save(new SessionDocument { Name = "new", LastUsedUtc = DateTimeOffset.UnixEpoch.AddDays(9), Folders = ["file:///n/"], WasResident = true });
        File.WriteAllText(store.PathOf("broken"), "x");
        File.WriteAllText(Path.Combine(store.SessionsDir, ".work.json.123.tmp"), "leftover");

        var list = store.List();
        Assert.Equal(["new", "old", "broken"], list.Select(s => s.Name));
        Assert.Equal(["file:///n/"], list[0].Folders);
        Assert.True(list[0].WasResident);
    }

    [Fact]
    public void Rename_moves_the_session_its_backups_and_the_last_active_marker()
    {
        using var dir = new TempDirectory("cscode-sess-");
        var store = new SessionStore(dir.Path);
        store.Save(SessionSerializerTests.Sample("a"));
        store.Backups.Write("a", [new BackupEntry("id1", "untitled:U-1", "text", "utf-8", "\n", null, 3)], [], long.MaxValue);
        store.WriteLastActive("a");

        store.Rename("a", "b");

        Assert.False(store.Exists("a"));
        Assert.Equal("b", store.Load("b").Session!.Name);
        Assert.Equal("text", store.Backups.ReadText("b", "id1"));
        Assert.Empty(store.Backups.List("a"));
        Assert.Equal("b", store.ReadLastActive());
        Assert.Throws<ArgumentException>(() => store.Rename("b", "bad/name"));
        store.Save(SessionSerializerTests.Sample("c"));
        Assert.Throws<InvalidOperationException>(() => store.Rename("b", "c"));
    }

    [Fact]
    public void Delete_removes_the_session_and_its_backups()
    {
        using var dir = new TempDirectory("cscode-sess-");
        var store = new SessionStore(dir.Path);
        store.Save(SessionSerializerTests.Sample("gone"));
        store.Save(SessionSerializerTests.Sample("gone"));
        store.Backups.Write("gone", [new BackupEntry("i", "untitled:U-1", "t", "utf-8", "\n", null, 1)], [], long.MaxValue);

        store.Delete("gone");

        Assert.False(store.Exists("gone"));
        Assert.False(Directory.Exists(store.Backups.DirectoryOf("gone")));
    }

    [Fact]
    public void The_name_inside_the_file_never_overrides_the_file_name()
    {
        using var dir = new TempDirectory("cscode-sess-");
        var store = new SessionStore(dir.Path);
        Directory.CreateDirectory(store.SessionsDir);
        File.WriteAllText(store.PathOf("copy"), """{ "version": 2, "name": "original" }""");
        Assert.Equal("copy", store.Load("copy").Session!.Name);
    }

    [Fact]
    public void Concurrent_saves_in_one_process_do_not_interfere()
    {
        using var dir = new TempDirectory("cscode-sess-");
        var store = new SessionStore(dir.Path);
        Parallel.For(0, 40, i => store.Save(new SessionDocument { Name = "s" + (i % 8), LastUsedUtc = DateTimeOffset.UnixEpoch.AddSeconds(i) }));
        Assert.Equal(8, store.List().Count);
        Assert.All(store.List(), s => Assert.Equal(SessionLoadStatus.Loaded, store.Load(s.Name).Status));
    }
}
