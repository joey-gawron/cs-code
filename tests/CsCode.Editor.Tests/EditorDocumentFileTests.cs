using System.Text;
using Avalonia.Headless.XUnit;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Editor.Documents;
using CsCode.Editor.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CsCode.Editor.Tests;

/// <summary>MUST 1: load/save through IFileSystem, encoding + EOL detection and preservation, watching, large-file mode.</summary>
public class EditorDocumentFileTests
{
    private static string DetectByExtension(Uri uri, string? firstLine) => uri.LocalPath.EndsWith(".cs", StringComparison.Ordinal) ? "csharp" : "plaintext";

    private static async Task<EditorDocument> Open(TestHost host, Uri uri) =>
        await EditorDocument.OpenAsync(host.Services, uri, DetectByExtension, CancellationToken.None);

    // ---- encoding / EOL goldens (pure codec) ----

    public static IEnumerable<object[]> EncodingCases() =>
    [
        ["utf8", new byte[] { 0x68, 0x69 }, "hi"],
        ["utf8bom", new byte[] { 0xEF, 0xBB, 0xBF, 0x68, 0x69 }, "hi"],
        ["utf16le", new byte[] { 0xFF, 0xFE, 0x68, 0x00, 0x69, 0x00 }, "hi"],
        ["utf16be", new byte[] { 0xFE, 0xFF, 0x00, 0x68, 0x00, 0x69 }, "hi"],
        ["utf16le", new byte[] { 0x68, 0x00, 0x69, 0x00, 0x20, 0x00, 0x6F, 0x00, 0x6B, 0x00 }, "hi ok"],   // BOM-less UTF-16 LE heuristic
        ["utf16be", new byte[] { 0x00, 0x68, 0x00, 0x69, 0x00, 0x20, 0x00, 0x6F, 0x00, 0x6B }, "hi ok"],   // BOM-less UTF-16 BE heuristic
        ["utf8", Encoding.UTF8.GetBytes("café \U0001F600"), "café \U0001F600"],
        ["windows1252", new byte[] { 0x63, 0x61, 0x66, 0xE9 }, "café"],                              // invalid UTF-8 -> fallback
        ["windows1252", new byte[] { 0x93, 0x68, 0x69, 0x94 }, "“hi”"],                         // 1252 smart quotes (Latin-1 would differ)
    ];

    [Theory]
    [MemberData(nameof(EncodingCases))]
    public void Encoding_detection_and_decoding_golden(string expectedName, byte[] bytes, string expectedText)
    {
        var enc = TextFileCodec.Detect(bytes);
        Assert.Equal(expectedName, enc.Name);
        Assert.Equal(expectedText, TextFileCodec.Decode(bytes, enc));
    }

    [Theory]
    [MemberData(nameof(EncodingCases))]
    public void Encoding_round_trips_the_original_bytes(string expectedName, byte[] bytes, string text)
    {
        var enc = TextFileCodec.Detect(bytes);
        Assert.Equal(expectedName, enc.Name);
        Assert.Equal(bytes, TextFileCodec.Encode(text, enc)); // decode -> text -> encode reproduces the file, BOM included
    }

    [Fact]
    public void Configured_encoding_is_used_when_there_is_no_BOM_but_a_BOM_still_wins()
    {
        Assert.Equal("windows1252", TextFileCodec.Detect("hi"u8, FileEncoding.Windows1252).Name);
        Assert.Equal("utf8bom", TextFileCodec.Detect([0xEF, 0xBB, 0xBF, 0x61], FileEncoding.Windows1252).Name);
    }

    [Theory]
    [InlineData("a\nb\nc\n", "\n", 3, 0, 0)]
    [InlineData("a\r\nb\r\nc", "\r\n", 0, 2, 0)]
    [InlineData("a\rb\rc\r", "\r", 0, 0, 3)]
    [InlineData("a\r\nb\nc\r\nd\r\n", "\r\n", 1, 3, 0)] // mixed: majority wins
    [InlineData("a\nb\nc\r\nd", "\n", 2, 1, 0)]
    [InlineData("no breaks", "\n", 0, 0, 0)]            // falls back to the default
    public void Eol_detection_majority_and_counts(string text, string expected, int lf, int crlf, int cr)
    {
        var info = TextFileCodec.DetectEol(text);
        Assert.Equal(expected, info.Eol);
        Assert.Equal((lf, crlf, cr), (info.Lf, info.CrLf, info.Cr));
    }

    [Fact]
    public void Eol_normalisation_rewrites_every_terminator_and_is_identity_when_uniform()
    {
        Assert.Equal("a\r\nb\r\nc\r\n", TextFileCodec.NormalizeEol("a\nb\r\nc\r", "\r\n"));
        var uniform = "a\nb\n";
        Assert.Same(uniform, TextFileCodec.NormalizeEol(uniform, "\n"));
    }

    // ---- load / save through IFileSystem ----

    [AvaloniaFact]
    public async Task Open_detects_encoding_and_eol_and_save_preserves_them_byte_for_byte()
    {
        using var host = TestHost.Create();
        var cases = new (string Name, byte[] Bytes)[]
        {
            ("bom.txt", [.. new byte[] { 0xEF, 0xBB, 0xBF }, .. Encoding.UTF8.GetBytes("one\r\ntwo\r\n")]),
            ("u16.txt", [.. new byte[] { 0xFF, 0xFE }, .. Encoding.Unicode.GetBytes("one\ntwo\n")]),
            ("latin.txt", [0x63, 0x61, 0x66, 0xE9, 0x0A]),
            ("cr.txt", Encoding.UTF8.GetBytes("a\rb\rc")),
            ("plain.txt", Encoding.UTF8.GetBytes("plain\nlf\n")),
        };
        foreach (var (name, bytes) in cases)
        {
            var path = host.Dir.Combine(name);
            File.WriteAllBytes(path, bytes);
            var doc = await Open(host, host.FileUri(name));
            Assert.False(doc.IsDirty);
            doc.Dispose();
            doc = await Open(host, host.FileUri(name));
            // Make a change and revert it so save() really writes, then compare bytes.
            doc.ApplyEdits([TextEdit.Insert(new TextPosition(0, 0), "X")]);
            doc.ApplyEdits([TextEdit.Delete(TextRange.FromLines(0, 0, 0, 1))]);
            await doc.SaveAsync();
            Assert.Equal(bytes, File.ReadAllBytes(path));
            doc.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task Open_reports_encoding_name_and_eol_for_the_status_bar()
    {
        using var host = TestHost.Create();
        File.WriteAllBytes(host.Dir.Combine("a.txt"), [0xEF, 0xBB, 0xBF, 0x61, 0x0D, 0x0A, 0x62]);
        var doc = await Open(host, host.FileUri("a.txt"));
        Assert.Equal("utf8bom", doc.EncodingName);
        Assert.Equal("\r\n", doc.EndOfLine);
        Assert.Equal("a\r\nb", doc.GetText());
        doc.Dispose();
    }

    [AvaloniaFact]
    public async Task Mixed_eol_is_normalised_to_the_majority_on_save()
    {
        using var host = TestHost.Create();
        File.WriteAllText(host.Dir.Combine("m.txt"), "a\r\nb\nc\r\nd");
        var doc = await Open(host, host.FileUri("m.txt"));
        Assert.Equal("\r\n", doc.EndOfLine);
        doc.ApplyEdits([TextEdit.Insert(new TextPosition(0, 1), "!")]);
        await doc.SaveAsync();
        Assert.Equal("a!\r\nb\r\nc\r\nd", File.ReadAllText(host.Dir.Combine("m.txt")));
        doc.Dispose();
    }

    [AvaloniaFact]
    public async Task Save_is_atomic_no_temp_files_remain_and_content_replaced()
    {
        using var host = TestHost.Create();
        File.WriteAllText(host.Dir.Combine("t.txt"), "old");
        var doc = await Open(host, host.FileUri("t.txt"));
        doc.ApplyEdits([TextEdit.Insert(new TextPosition(0, 3), " new")]);
        Assert.True(doc.IsDirty);
        await doc.SaveAsync();
        Assert.False(doc.IsDirty);
        Assert.Equal("old new", File.ReadAllText(host.Dir.Combine("t.txt")));
        Assert.Equal(["t.txt"], FileNames(host));
        doc.Dispose();
    }

    [AvaloniaFact]
    public async Task A_failed_write_leaves_the_original_untouched_and_cleans_the_temp_file()
    {
        using var host = TestHost.Create();
        var target = host.Dir.Combine("keep.txt");
        File.WriteAllText(target, "original");
        var failing = new FailingRenameFileSystem(host.Services.FileSystem);
        await Assert.ThrowsAsync<IOException>(async () =>
            await EditorDocument.WriteAtomicAsync(failing, new Uri(target), "replacement"u8.ToArray(), CancellationToken.None));
        Assert.Equal("original", File.ReadAllText(target));
        Assert.Equal(["keep.txt"], FileNames(host));
    }

    private static string[] FileNames(TestHost host) => Directory.GetFiles(host.Dir.Path).Select(p => Path.GetFileName(p)!).OrderBy(n => n).ToArray();

    [AvaloniaFact]
    public async Task Edits_made_while_the_write_is_in_flight_keep_the_document_dirty()
    {
        using var lane = new CsCode.Editor.Threading.DedicatedLane("test-io");
        var gated = new GatedWriteFileSystem(new CsCode.Editor.Fallbacks.LocalFileSystem(lane));
        using var host = TestHost.Create(configure: s => s.AddSingleton<IFileSystem>(gated));
        File.WriteAllText(host.Dir.Combine("race.txt"), "x");
        var doc = await Open(host, host.FileUri("race.txt"));
        doc.ApplyEdits([TextEdit.Insert(new TextPosition(0, 1), "1")]);
        gated.Block = true;
        var save = doc.SaveAsync();
        await gated.WriteStarted.Task;                                     // the snapshot ("x1") is taken and the write is in flight
        doc.ApplyEdits([TextEdit.Insert(new TextPosition(0, 2), "2")]);    // typed while saving
        gated.Release.SetResult();
        await save;
        Assert.Equal("x1", File.ReadAllText(host.Dir.Combine("race.txt"))); // the snapshot, not the later edit
        Assert.True(doc.IsDirty);                                            // the later edit is NOT saved
        gated.Block = false;
        await doc.SaveAsync();
        Assert.False(doc.IsDirty);
        Assert.Equal("x12", File.ReadAllText(host.Dir.Combine("race.txt")));
        doc.Dispose();
    }

    [AvaloniaFact]
    public async Task Save_fires_Saved_and_notifies_the_document_manager_and_dirty_events()
    {
        using var host = TestHost.Create();
        File.WriteAllText(host.Dir.Combine("e.txt"), "a");
        var doc = await Open(host, host.FileUri("e.txt"));
        var saved = 0;
        var dirty = new List<bool>();
        var managerSaved = 0;
        doc.Saved.Subscribe(_ => saved++);
        doc.DirtyChanged.Subscribe(d => dirty.Add(((EditorDocument)d).IsDirty));
        host.Documents.Saved.Subscribe(_ => managerSaved++);
        doc.ApplyEdits([TextEdit.Insert(new TextPosition(0, 1), "b")]);
        await doc.SaveAsync();
        Assert.Equal(1, saved);
        Assert.Equal(1, managerSaved);
        Assert.Equal([true, false], dirty);
        doc.Dispose();
    }

    [AvaloniaFact]
    public async Task Open_registers_with_the_document_manager_and_dispose_unregisters()
    {
        using var host = TestHost.Create();
        File.WriteAllText(host.Dir.Combine("r.txt"), "a");
        var uri = host.FileUri("r.txt");
        var opened = 0;
        var closed = 0;
        host.Documents.Opened.Subscribe(_ => opened++);
        host.Documents.Closed.Subscribe(_ => closed++);
        var doc = await Open(host, uri);
        Assert.Same(doc, host.Documents.Get(uri));
        Assert.Equal(1, opened);
        doc.Dispose();
        Assert.Null(host.Documents.Get(uri));
        Assert.Equal(1, closed);
    }

    [AvaloniaFact]
    public async Task Missing_file_throws_FileNotFound_and_directory_is_rejected()
    {
        using var host = TestHost.Create();
        await Assert.ThrowsAsync<FileNotFoundException>(async () => await Open(host, host.FileUri("nope.txt")));
        await Assert.ThrowsAsync<IOException>(async () => await Open(host, new Uri(host.Dir.Path + "/")));
    }

    // ---- save participants ----

    [AvaloniaFact]
    public async Task Editorconfig_trim_and_final_newline_apply_on_save_as_one_undoable_edit()
    {
        using var host = TestHost.Create();
        host.Dir.WriteFile(".editorconfig", "root = true\n[*.txt]\ntrim_trailing_whitespace = true\ninsert_final_newline = true\nend_of_line = lf\n");
        host.Dir.WriteFile("p.txt", "a  \nb\t\nc");
        var doc = await Open(host, host.FileUri("p.txt"));
        await doc.SaveAsync();
        Assert.Equal("a\nb\nc\n", File.ReadAllText(host.Dir.Combine("p.txt")));
        Assert.Equal("a\nb\nc\n", doc.GetText()); // the buffer matches the file
        doc.Undo();
        Assert.Equal("a  \nb\t\nc", doc.GetText()); // one undo reverts all participants
        doc.Dispose();
    }

    [AvaloniaFact]
    public async Task Files_settings_apply_when_there_is_no_editorconfig()
    {
        using var host = TestHost.Create();
        host.Config.Set(EditorSettingKeys.FilesTrimTrailingWhitespace, true);
        host.Config.Set(EditorSettingKeys.FilesInsertFinalNewline, true);
        host.Dir.WriteFile("q.txt", "x   ");
        var doc = await Open(host, host.FileUri("q.txt"));
        await doc.SaveAsync();
        Assert.Equal("x\n", File.ReadAllText(host.Dir.Combine("q.txt")));
        doc.Dispose();
    }

    [Theory]
    [InlineData("a  \nb \t\nc", true, false, false, "a\nb\nc")]
    [InlineData("a\nb", false, true, false, "a\nb\n")]
    [InlineData("a\nb\n", false, true, false, "a\nb\n")]
    [InlineData("a\n\n\n\n", false, false, true, "a\n")]
    [InlineData("a  \n\n\n", true, true, true, "a\n")]
    [InlineData("a  ", true, true, false, "a\n")]
    [InlineData("", false, true, false, "")]
    public void Save_edit_computation(string text, bool trim, bool finalNewline, bool trimFinal, string expected)
    {
        var ui = new ThreadAffinityUiThread();
        var doc = new EditorDocument(new Uri("file:///x.txt"), "plaintext", EditorDocument.CreateBuffer(text, ui), ui);
        var edits = SaveEdits.Compute(doc.Snapshot(), trim, finalNewline, trimFinal, "\n");
        Assert.True(doc.ApplyEdits(edits));
        Assert.Equal(expected, doc.GetText());
    }

    // ---- external changes ----

    [AvaloniaFact]
    public async Task A_clean_document_reloads_silently_when_the_file_changes_on_disk()
    {
        using var host = TestHost.Create();
        File.WriteAllText(host.Dir.Combine("ext.txt"), "one\ntwo\n");
        var doc = await Open(host, host.FileUri("ext.txt"));
        var changes = 0;
        doc.Changed.Subscribe(_ => changes++);
        File.WriteAllText(host.Dir.Combine("ext.txt"), "one\nTWO\nthree\n");
        await TestHost.WaitUntil(() => doc.GetText() == "one\nTWO\nthree\n", "silent reload");
        Assert.False(doc.IsDirty);
        Assert.Empty(host.Window.Messages);
        Assert.Equal(1, changes);          // ONE minimal edit, not a full replace + something else
        Assert.False(doc.CanUndo);          // reload is not an undoable user edit
        doc.Dispose();
    }

    [AvaloniaFact]
    public async Task A_dirty_document_asks_and_Reload_discards_local_edits()
    {
        using var host = TestHost.Create();
        File.WriteAllText(host.Dir.Combine("c.txt"), "base");
        var doc = await Open(host, host.FileUri("c.txt"));
        doc.ApplyEdits([TextEdit.Insert(new TextPosition(0, 4), " mine")]);
        host.Window.MessageActionResponses.Enqueue("Reload");
        File.WriteAllText(host.Dir.Combine("c.txt"), "disk version");
        await TestHost.WaitUntil(() => doc.GetText() == "disk version", "conflict -> reload");
        Assert.Single(host.Window.Messages);
        Assert.Contains("changed on disk", host.Window.Messages[0].Message);
        Assert.False(doc.IsDirty);
        doc.Dispose();
    }

    [AvaloniaFact]
    public async Task A_dirty_document_asks_and_Keep_preserves_edits_without_asking_again_for_the_same_content()
    {
        using var host = TestHost.Create();
        File.WriteAllText(host.Dir.Combine("k.txt"), "base");
        var doc = await Open(host, host.FileUri("k.txt"));
        doc.ApplyEdits([TextEdit.Insert(new TextPosition(0, 4), " mine")]);
        host.Window.MessageActionResponses.Enqueue("Keep my changes");
        File.WriteAllText(host.Dir.Combine("k.txt"), "disk version");
        await TestHost.WaitUntil(() => host.Window.Messages.Count == 1, "conflict prompt");
        await Task.Delay(400);
        Assert.Equal("base mine", doc.GetText());
        Assert.True(doc.IsDirty);
        File.SetLastWriteTimeUtc(host.Dir.Combine("k.txt"), DateTime.UtcNow.AddMinutes(1)); // touch only
        await Task.Delay(500);
        Assert.Single(host.Window.Messages);
        doc.Dispose();
    }

    [AvaloniaFact]
    public async Task The_documents_own_save_does_not_trigger_a_reload_or_a_prompt()
    {
        using var host = TestHost.Create();
        File.WriteAllText(host.Dir.Combine("own.txt"), "a");
        var doc = await Open(host, host.FileUri("own.txt"));
        var changes = 0;
        doc.Changed.Subscribe(_ => changes++);
        doc.ApplyEdits([TextEdit.Insert(new TextPosition(0, 1), "b")]);
        await doc.SaveAsync();
        var afterSave = changes;
        await Task.Delay(700); // let the watcher event for our own write arrive and be dismissed
        Assert.Equal(afterSave, changes);
        Assert.Empty(host.Window.Messages);
        Assert.Equal("ab", doc.GetText());
        doc.Dispose();
    }

    [AvaloniaFact]
    public async Task ReloadAsync_discards_edits_and_clears_undo()
    {
        using var host = TestHost.Create();
        File.WriteAllText(host.Dir.Combine("rl.txt"), "disk");
        var doc = await Open(host, host.FileUri("rl.txt"));
        doc.ApplyEdits([TextEdit.Insert(new TextPosition(0, 0), "local ")]);
        await doc.ReloadAsync();
        Assert.Equal("disk", doc.GetText());
        Assert.False(doc.IsDirty);
        Assert.False(doc.CanUndo);
        doc.Dispose();
    }

    [AvaloniaFact]
    public async Task ReadOnly_files_are_reported_and_reject_edits()
    {
        if (OperatingSystem.IsWindows()) return;
        using var host = TestHost.Create();
        var path = host.Dir.WriteFile("ro.txt", "x");
        File.SetUnixFileMode(path, UnixFileMode.UserRead);
        var doc = await Open(host, host.FileUri("ro.txt"));
        Assert.True(doc.IsReadOnly);
        Assert.False(doc.ApplyEdits([TextEdit.Insert(new TextPosition(0, 0), "y")]));
        Assert.Equal("x", doc.GetText());
        doc.Dispose();
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UserWriteMode);
    }

    private const UnixFileMode UserWriteMode = UnixFileMode.UserWrite;

    // ---- untitled / Save As ----

    [AvaloniaFact]
    public async Task Untitled_buffer_save_as_changes_identity_language_and_reregisters()
    {
        using var host = TestHost.Create();
        var doc = EditorDocument.CreateUntitled(host.Services, 1, "plaintext", DetectByExtension);
        Assert.True(doc.IsUntitled);
        Assert.Equal("untitled:Untitled-1", doc.Uri.OriginalString);
        Assert.Same(doc, host.Documents.Get(doc.Uri));
        doc.ApplyEdits([TextEdit.Insert(TextPosition.Zero, "class A {}")]);
        Assert.True(doc.IsDirty);

        var old = doc.Uri;
        var events = new List<string>();
        host.Documents.Closed.Subscribe(e => events.Add("closed " + e.Document.Uri));
        host.Documents.Opened.Subscribe(e => events.Add("opened " + e.Document.Uri));
        var target = host.FileUri("Saved.cs");
        await doc.SaveAsAsync(target);

        Assert.False(doc.IsUntitled);
        Assert.Equal(target, doc.Uri);
        Assert.Equal("csharp", doc.LanguageId);          // re-detected from the new name
        Assert.Equal("class A {}", File.ReadAllText(host.Dir.Combine("Saved.cs")));
        Assert.False(doc.IsDirty);
        Assert.Null(host.Documents.Get(old));
        Assert.Same(doc, host.Documents.Get(target));
        Assert.Equal(["closed " + old, "opened " + target], events);
        doc.Dispose();
    }

    // The Explorer's rename and move (docs/SHELL.md): the open document follows its file without being closed or saved.
    [AvaloniaFact]
    public async Task A_moved_file_keeps_its_document_with_unsaved_changes_and_undo_and_is_watched_at_its_new_path()
    {
        using var host = TestHost.Create();
        host.Dir.WriteFile("a.txt", "on disk\n");
        var doc = await Open(host, host.FileUri("a.txt"));
        doc.ApplyEdits([TextEdit.Insert(TextPosition.Zero, "unsaved ")]);
        var old = doc.Uri;
        var events = new List<string>();
        host.Documents.Closed.Subscribe(e => events.Add("closed " + e.Document.Uri));
        host.Documents.Opened.Subscribe(e => events.Add("opened " + e.Document.Uri));
        Directory.CreateDirectory(host.Dir.Combine("sub"));
        File.Move(host.Dir.Combine("a.txt"), host.Dir.Combine("sub", "b.cs"));
        var target = new Uri(host.Dir.Combine("sub", "b.cs"));

        await doc.RelocateAsync(target);

        Assert.Equal(target, doc.Uri);
        Assert.Equal("csharp", doc.LanguageId); // detected again from the new name, as after Save As
        Assert.True(doc.IsDirty);
        Assert.True(doc.CanUndo);
        Assert.Equal("unsaved on disk\n", doc.GetText());
        Assert.Equal("on disk\n", File.ReadAllText(target.LocalPath)); // nothing written
        Assert.Null(host.Documents.Get(old));
        Assert.Same(doc, host.Documents.Get(target));
        Assert.Equal(["closed " + old, "opened " + target], events);
        await Task.Delay(300);
        Assert.Empty(host.Window.Messages); // the move itself is no change on disk

        // the new path is watched: a change there meets the unsaved changes
        host.Window.MessageActionResponses.Enqueue("Keep my changes");
        File.WriteAllText(target.LocalPath, "changed elsewhere\n");
        await TestHost.WaitUntil(() => host.Window.Messages.Count == 1, "the changed-on-disk question for the new path");
        Assert.Contains("'b.cs' has changed on disk", host.Window.Messages[0].Message);
        doc.Dispose();
    }

    [AvaloniaFact]
    public async Task Saving_an_untitled_buffer_with_a_cancelled_dialog_leaves_it_untouched()
    {
        using var host = TestHost.Create();
        var doc = EditorDocument.CreateUntitled(host.Services, 2, "plaintext", DetectByExtension);
        doc.ApplyEdits([TextEdit.Insert(TextPosition.Zero, "text")]);
        await doc.SaveAsync(); // RecordingWindowService.ShowSaveDialogAsync returns null = cancelled
        Assert.True(doc.IsUntitled);
        Assert.True(doc.IsDirty);
        doc.Dispose();
    }

    [AvaloniaFact]
    public async Task Explicit_language_choice_survives_save_as()
    {
        using var host = TestHost.Create();
        var doc = EditorDocument.CreateUntitled(host.Services, 3, "plaintext", DetectByExtension);
        doc.SetLanguageId("markdown");
        await doc.SaveAsAsync(host.FileUri("x.cs"));
        Assert.Equal("markdown", doc.LanguageId);
        doc.Dispose();
    }

    // ---- large files ----

    [AvaloniaFact]
    public async Task Large_files_open_as_plaintext_with_a_reason()
    {
        using var host = TestHost.Create();
        host.Config.Set(EditorSettingKeys.LargeFileThresholdBytes, 1000);
        host.Dir.WriteFile("big.cs", string.Join("\n", Enumerable.Repeat("int x = 1;", 400)));
        var doc = await Open(host, host.FileUri("big.cs"));
        Assert.True(doc.IsLargeFile);
        Assert.Equal("plaintext", doc.LanguageId);
        Assert.Contains("Large file", doc.LargeFileReason);
        doc.Dispose();
    }

    [AvaloniaFact]
    public async Task A_very_long_line_triggers_large_file_mode_even_in_a_small_file()
    {
        using var host = TestHost.Create();
        host.Dir.WriteFile("long.cs", new string('x', 25_000));
        var doc = await Open(host, host.FileUri("long.cs"));
        Assert.True(doc.IsLargeFile);
        Assert.Contains("longer than", doc.LargeFileReason);
        doc.Dispose();
    }

    [AvaloniaFact]
    public async Task Small_files_are_not_large()
    {
        using var host = TestHost.Create();
        host.Dir.WriteFile("small.cs", "class A {}\n");
        var doc = await Open(host, host.FileUri("small.cs"));
        Assert.False(doc.IsLargeFile);
        Assert.Equal("csharp", doc.LanguageId);
        Assert.Null(doc.LargeFileReason);
        doc.Dispose();
    }

    [AvaloniaFact]
    public async Task Files_over_the_hard_limit_are_refused_with_a_clear_message()
    {
        using var host = TestHost.Create();
        host.Config.Set(EditorSettingKeys.MaxFileSizeBytes, 100);
        host.Dir.WriteFile("huge.txt", new string('x', 500));
        var ex = await Assert.ThrowsAsync<FileTooLargeException>(async () => await Open(host, host.FileUri("huge.txt")));
        Assert.Contains("huge.txt", ex.Message);
        Assert.Contains("larger than", ex.Message);
        Assert.Null(host.Documents.Get(host.FileUri("huge.txt")));
    }

    private sealed class GatedWriteFileSystem(IFileSystem inner) : IFileSystem
    {
        public bool Block;
        public TaskCompletionSource WriteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<FileStat?> StatAsync(Uri uri, CancellationToken ct = default) => inner.StatAsync(uri, ct);
        public ValueTask<IReadOnlyList<DirectoryEntry>> ReadDirectoryAsync(Uri uri, CancellationToken ct = default) => inner.ReadDirectoryAsync(uri, ct);
        public ValueTask<byte[]> ReadFileAsync(Uri uri, CancellationToken ct = default) => inner.ReadFileAsync(uri, ct);
        public async ValueTask WriteFileAsync(Uri uri, ReadOnlyMemory<byte> content, bool createParents = true, CancellationToken ct = default)
        {
            if (Block)
            {
                WriteStarted.TrySetResult();
                await Release.Task;
            }
            await inner.WriteFileAsync(uri, content, createParents, ct);
        }
        public ValueTask CreateDirectoryAsync(Uri uri, CancellationToken ct = default) => inner.CreateDirectoryAsync(uri, ct);
        public ValueTask DeleteAsync(Uri uri, bool recursive = false, bool useTrash = true, CancellationToken ct = default) => inner.DeleteAsync(uri, recursive, useTrash, ct);
        public ValueTask RenameAsync(Uri source, Uri target, bool overwrite = false, CancellationToken ct = default) => inner.RenameAsync(source, target, overwrite, ct);
        public ValueTask CopyAsync(Uri source, Uri target, bool overwrite = false, CancellationToken ct = default) => inner.CopyAsync(source, target, overwrite, ct);
    }

    private sealed class FailingRenameFileSystem(IFileSystem inner) : IFileSystem
    {
        public ValueTask<FileStat?> StatAsync(Uri uri, CancellationToken ct = default) => inner.StatAsync(uri, ct);
        public ValueTask<IReadOnlyList<DirectoryEntry>> ReadDirectoryAsync(Uri uri, CancellationToken ct = default) => inner.ReadDirectoryAsync(uri, ct);
        public ValueTask<byte[]> ReadFileAsync(Uri uri, CancellationToken ct = default) => inner.ReadFileAsync(uri, ct);
        public ValueTask WriteFileAsync(Uri uri, ReadOnlyMemory<byte> content, bool createParents = true, CancellationToken ct = default) => inner.WriteFileAsync(uri, content, createParents, ct);
        public ValueTask CreateDirectoryAsync(Uri uri, CancellationToken ct = default) => inner.CreateDirectoryAsync(uri, ct);
        public ValueTask DeleteAsync(Uri uri, bool recursive = false, bool useTrash = true, CancellationToken ct = default) => inner.DeleteAsync(uri, recursive, useTrash, ct);
        public ValueTask RenameAsync(Uri source, Uri target, bool overwrite = false, CancellationToken ct = default) => throw new IOException("simulated rename failure");
        public ValueTask CopyAsync(Uri source, Uri target, bool overwrite = false, CancellationToken ct = default) => inner.CopyAsync(source, target, overwrite, ct);
    }
}
