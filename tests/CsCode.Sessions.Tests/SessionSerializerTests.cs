using System.Text;
using Xunit;

namespace CsCode.Sessions.Tests;

public class SessionSerializerTests
{
    internal static SessionDocument Sample(string name = "work") => new()
    {
        Name = name,
        LastUsedUtc = new DateTimeOffset(2026, 10, 29, 10, 30, 0, TimeSpan.Zero),
        Folders = ["file:///home/u/proj/", "ssh://box/srv/app/"],
        Editors =
        [
            new SessionEditor
            {
                Uri = "file:///home/u/proj/a.cs", Group = 0, Order = 0, Pinned = true,
                ViewState = new SessionViewState(12, 4, 10, 0, 12, 4, 180.5, 0, [3, 40]),
            },
            new SessionEditor { Uri = "untitled:Untitled-1", Group = 1, Order = 0, LanguageId = "markdown", BackupId = "abc" },
        ],
        ActiveEditor = "file:///home/u/proj/a.cs",
        Layout = "{\"version\":1}",
        Window = new SessionWindow(10, 20, 1280, 800, "maximized"),
        WasResident = true,
    };

    [Fact]
    public void A_session_round_trips_with_every_field()
    {
        var original = Sample();
        var back = SessionSerializer.Deserialize(SessionSerializer.Serialize(original));

        Assert.Equal(SessionDocument.CurrentVersion, back.Version);
        Assert.Equal(original.Name, back.Name);
        Assert.Equal(original.LastUsedUtc, back.LastUsedUtc);
        Assert.Equal(original.Folders, back.Folders);
        Assert.Equal(original.ActiveEditor, back.ActiveEditor);
        Assert.Equal(original.Layout, back.Layout);
        Assert.Equal(original.Window, back.Window);
        Assert.True(back.WasResident);
        Assert.Equal(2, back.Editors.Count);
        var a = back.Editors[0];
        Assert.True(a.Pinned);
        Assert.Equal(original.Editors[0].ViewState!.FoldedStartLines, a.ViewState!.FoldedStartLines);
        Assert.Equal(original.Editors[0].ViewState! with { FoldedStartLines = a.ViewState.FoldedStartLines }, a.ViewState);
        Assert.Equal("markdown", back.Editors[1].LanguageId);
        Assert.Equal("abc", back.Editors[1].BackupId);
    }

    [Fact]
    public void A_v1_file_is_migrated_to_editors_in_tab_order()
    {
        var v1 = """
            { "version": 1, "name": "old", "folders": ["file:///p/"], "openFiles": ["file:///p/a.cs", "file:///p/b.cs"], "activeFile": "file:///p/b.cs",
              "layout": "{}", "lastUsedUtc": "2026-10-29T09:00:00+00:00" }
            """;
        var s = SessionSerializer.Deserialize(Encoding.UTF8.GetBytes(v1));

        Assert.Equal(SessionDocument.CurrentVersion, s.Version);
        Assert.Equal(["file:///p/a.cs", "file:///p/b.cs"], s.Editors.Select(e => e.Uri));
        Assert.Equal([0, 1], s.Editors.Select(e => e.Order));
        Assert.Equal("file:///p/b.cs", s.ActiveEditor);
        Assert.Equal(["file:///p/"], s.Folders);
    }

    [Fact]
    public void A_file_without_a_version_is_read_as_v1()
    {
        var s = SessionSerializer.Deserialize("""{ "name": "x", "openFiles": ["file:///a"] }"""u8);
        Assert.Equal("file:///a", Assert.Single(s.Editors).Uri);
    }

    [Fact]
    public void A_file_from_a_newer_build_is_refused_as_too_new()
    {
        var ex = Assert.Throws<SessionFormatException>(() => SessionSerializer.Deserialize("""{ "version": 99, "name": "x" }"""u8));
        Assert.Equal(SessionFormatError.TooNew, ex.Error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("""{ "version": 2 }""")]
    [InlineData("""{ "version": "two", "name": "x" }""")]
    [InlineData("""{ "version": 2, "name": "x", "editors": 5 }""")]
    public void Garbage_is_reported_as_corrupt(string json)
    {
        var ex = Assert.Throws<SessionFormatException>(() => SessionSerializer.Deserialize(Encoding.UTF8.GetBytes(json)));
        Assert.Equal(SessionFormatError.Corrupt, ex.Error);
    }

    [Fact]
    public void Nulls_and_comments_in_a_hand_edited_file_are_tolerated()
    {
        var s = SessionSerializer.Deserialize("""
            // edited by hand
            { "version": 2, "name": "x", "folders": null, "editors": [ null, { "uri": "file:///a" }, ], }
            """u8);
        Assert.Empty(s.Folders);
        Assert.Equal("file:///a", Assert.Single(s.Editors).Uri);
    }

    [Theory]
    [InlineData("work", true)]
    [InlineData("Work Project (2) v1.0_beta+x", true)]
    [InlineData("", false)]
    [InlineData(" lead", false)]
    [InlineData("trail.", false)]
    [InlineData(".hidden", false)]
    [InlineData("a/b", false)]
    [InlineData("a\\b", false)]
    [InlineData("a:b", false)]
    [InlineData("ünïcode", false)]
    public void Session_names_are_restricted_to_portable_file_names(string name, bool valid) => Assert.Equal(valid, SessionNames.IsValid(name));

    [Fact]
    public void Unnamed_sessions_take_the_lowest_free_number()
    {
        Assert.Equal("window-1", SessionNames.NextUnnamed([]));
        Assert.Equal("window-3", SessionNames.NextUnnamed(["window-1", "window-2", "work"]));
        Assert.Equal("window-2", SessionNames.NextUnnamed(["window-1", "window-3"]));
    }
}
