using CsCode.TestDoubles;
using Xunit;

namespace CsCode.Sessions.Tests;

public class HotExitBackupTests
{
    private static BackupEntry Entry(string uri, string text, int version = 1) =>
        new(HotExitBackupStore.IdFor(uri), uri, text, "utf-8", "\n", null, version);

    [Fact]
    public void Backups_are_written_listed_and_read_back_with_the_document_identity()
    {
        using var dir = new TempDirectory("cscode-bak-");
        var file = dir.WriteFile("src/a.cs", "on disk\n");
        var uri = new Uri(file).ToString();
        var store = new HotExitBackupStore(dir.Combine("backups"));

        var result = store.Write("s", [Entry(uri, "unsaved ✓ edit\r\n", 7)], [], long.MaxValue);

        Assert.Single(result.Written);
        Assert.False(result.OverLimit);
        var info = Assert.Single(store.List("s"));
        Assert.Equal(uri, info.Uri);
        Assert.Equal(7, info.DocumentVersion);
        Assert.Equal("utf-8", info.Encoding);
        Assert.NotNull(info.Base);
        Assert.Equal(8, info.Base!.Size);
        Assert.NotNull(info.Base.Sha256);
        Assert.Equal("unsaved ✓ edit\r\n", store.ReadText("s", info.Id));
        Assert.Equal(BackupDiskState.Unchanged, HotExitBackupStore.Check(info));
    }

    [Fact]
    public void A_file_changed_on_disk_after_the_backup_is_detected()
    {
        using var dir = new TempDirectory("cscode-bak-");
        var file = dir.WriteFile("a.txt", "v1");
        var store = new HotExitBackupStore(dir.Combine("backups"));
        store.Write("s", [Entry(new Uri(file).ToString(), "mine")], [], long.MaxValue);

        File.WriteAllText(file, "v2"); // same size: only the hash tells
        Assert.Equal(BackupDiskState.ChangedOnDisk, HotExitBackupStore.Check(Assert.Single(store.List("s"))));

        File.Delete(file);
        Assert.Equal(BackupDiskState.MissingOnDisk, HotExitBackupStore.Check(Assert.Single(store.List("s"))));
    }

    [Fact]
    public void A_file_created_after_an_untitled_style_backup_of_a_missing_file_counts_as_changed()
    {
        using var dir = new TempDirectory("cscode-bak-");
        var path = dir.Combine("later.txt");
        var store = new HotExitBackupStore(dir.Combine("backups"));
        store.Write("s", [Entry(new Uri(path).ToString(), "mine")], [], long.MaxValue);
        Assert.Equal(BackupDiskState.MissingOnDisk, HotExitBackupStore.Check(Assert.Single(store.List("s"))));
        File.WriteAllText(path, "someone else's");
        Assert.Equal(BackupDiskState.ChangedOnDisk, HotExitBackupStore.Check(Assert.Single(store.List("s"))));
    }

    [Fact]
    public void Untitled_buffers_have_no_disk_identity()
    {
        using var dir = new TempDirectory("cscode-bak-");
        var store = new HotExitBackupStore(dir.Combine("backups"));
        store.Write("s", [Entry("untitled:Untitled-1", "notes")], [], long.MaxValue);
        var info = Assert.Single(store.List("s"));
        Assert.Null(info.Base);
        Assert.Equal(BackupDiskState.Untitled, HotExitBackupStore.Check(info));
    }

    [Fact]
    public void Kept_ids_survive_and_every_other_backup_is_deleted()
    {
        using var dir = new TempDirectory("cscode-bak-");
        var store = new HotExitBackupStore(dir.Combine("backups"));
        var a = Entry("untitled:A", "a");
        var b = Entry("untitled:B", "b");
        var c = Entry("untitled:C", "c");
        store.Write("s", [a, b, c], [], long.MaxValue);

        // a was saved (gone), b unchanged (kept), c edited again (rewritten).
        var result = store.Write("s", [c with { Text = "c2", DocumentVersion = 2 }], [b.Id], long.MaxValue);

        Assert.Equal([c.Id], result.Written);
        Assert.Equal([b.Id, c.Id], store.List("s").Select(i => i.Id).Order());
        Assert.Equal("c2", store.ReadText("s", c.Id));
        Assert.Equal("b", store.ReadText("s", b.Id));

        store.Write("s", [], [], long.MaxValue);
        Assert.False(Directory.Exists(store.DirectoryOf("s")), "an empty backup directory is removed");
    }

    [Fact]
    public void Above_the_size_limit_the_rest_is_skipped_and_reported()
    {
        using var dir = new TempDirectory("cscode-bak-");
        var store = new HotExitBackupStore(dir.Combine("backups"));
        var big = Entry("untitled:Big", new string('x', 600));
        var small = Entry("untitled:Small", new string('y', 300));
        var third = Entry("untitled:Third", new string('z', 300));

        var result = store.Write("s", [big, small, third], [], maxBytes: 1000);

        Assert.True(result.OverLimit);
        Assert.Equal([big.Id, small.Id], result.Written);
        Assert.Equal(["untitled:Third"], result.Skipped);
        Assert.Equal(900, result.TotalBytes);
        Assert.Equal(900, store.SizeOf("s"));
    }

    [Fact]
    public void A_backup_with_damaged_metadata_is_ignored_without_losing_the_others()
    {
        using var dir = new TempDirectory("cscode-bak-");
        var store = new HotExitBackupStore(dir.Combine("backups"));
        var a = Entry("untitled:A", "a");
        var b = Entry("untitled:B", "b");
        store.Write("s", [a, b], [], long.MaxValue);
        File.WriteAllText(Path.Combine(store.DirectoryOf("s"), a.Id + ".json"), "{ broken");
        File.Delete(Path.Combine(store.DirectoryOf("s"), b.Id + ".txt"));
        store.Write("s", [Entry("untitled:C", "c")], [a.Id, b.Id], long.MaxValue);

        Assert.Equal(["untitled:C"], store.List("s").Select(i => i.Uri));
    }

    [Fact]
    public void Ids_are_stable_per_uri()
    {
        Assert.Equal(HotExitBackupStore.IdFor("file:///a"), HotExitBackupStore.IdFor("file:///a"));
        Assert.NotEqual(HotExitBackupStore.IdFor("file:///a"), HotExitBackupStore.IdFor("file:///b"));
    }
}
