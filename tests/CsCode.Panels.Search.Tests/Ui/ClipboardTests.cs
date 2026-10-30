using Avalonia.Headless.XUnit;
using CsCode.Panels.Search.ViewModels;
using CsCode.Panels.Search.Views;
using CsCode.Ui.Menus;
using Xunit;

namespace CsCode.Panels.Search.Tests.Ui;

/// <summary>Clipboard brief A1: Find in Files results copy as <c>path(line): text</c> (a file as its path), from Ctrl+C / Ctrl+Insert and Edit > Copy.</summary>
public class FindInFilesClipboardTests
{
    [AvaloniaFact]
    public async Task Results_copy_as_path_line_and_text_and_the_view_is_the_Edit_menus_target()
    {
        UiSupport.EnsureTheme();
        using var f = new UiFixture();
        f.WriteSampleSources();
        var vm = new FindInFilesViewModel(f.Services);
        var view = new FindInFilesView(vm);
        var window = UiSupport.Host(view, 420, 640);
        vm.Query = "UserService";
        vm.Start();
        await UiSupport.WaitUntilAsync(() => vm.WaitForSearchAsync().IsCompleted);
        UiSupport.Pump();

        var match = vm.Rows.OfType<MatchRow>().First();
        Assert.Equal($"{match.File.FullPath}({match.LineNumber}): {match.Preview.Trim()}", FindInFilesView.CopyText(match));
        Assert.Equal(match.File.FullPath, FindInFilesView.CopyText(match.File));

        IClipboardTarget target = view;
        vm.SelectedRow = null;
        Assert.False(target.CanCopy);
        vm.SelectedRow = match;
        Assert.True(target.CanCopy);
        await target.CopyAsync(); // through the panel's window service (a recording fake here): must not throw
        Assert.Equal(ClipboardAction.Copy, ClipboardKeys.Classify(Avalonia.Input.Key.Insert, Avalonia.Input.KeyModifiers.Control));
        window.Close();
    }
}
