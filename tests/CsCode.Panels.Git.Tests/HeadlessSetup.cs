using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Themes.Fluent;
using CsCode.Ui.Theming;

[assembly: AvaloniaTestApplication(typeof(CsCode.Panels.Git.Tests.TestAppBuilder))]

namespace CsCode.Panels.Git.Tests;

public sealed class TestApp : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());
}

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<TestApp>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .WithCodeFonts();
}
