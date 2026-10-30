using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(CsCode.Panels.Terminal.Tests.TestAppBuilder))]
// Latency budgets, process-wide counters (threads, descriptors) and CSCODE_HOME: no other test may run alongside.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace CsCode.Panels.Terminal.Tests;

/// <summary>The real application class on Avalonia's headless platform with Skia (screenshots are real renders); never a window on a display.</summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<CsCode.App.App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
