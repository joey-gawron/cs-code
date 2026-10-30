using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Xunit;
using CsCode.Ui.Theming;

[assembly: AvaloniaTestApplication(typeof(CsCode.MemoryTests.TestAppBuilder))]
// Memory measurements are process-wide (heap, resident set, threads, descriptors) and CSCODE_HOME is an environment variable: one test at a time.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace CsCode.MemoryTests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp()
    {
        // Avalonia's own timing instruments (layout, render, input per pass; Avalonia.Diagnostic.Meter): budget reports say where UI-thread time went (T55).
        AppContext.SetSwitch("Avalonia.Diagnostics.Diagnostic.IsEnabled", true);
        // T55: Avalonia 12's headless platform renders BY ITSELF when ShouldRenderOnUIThread is true (its default): a DispatcherTimer at Fps (60)
        // rasterises every dirty frame with software Skia on the UI thread, so a budget window "without ticks" still spent ~16 ms per frame there
        // (1.4-2.2 s of a 4.8 s typing window in the baseline row). A desktop rasterises on its render thread; so do these tests now. The UI thread
        // keeps what it does on a desktop: input, layout, the composition commit. ForceRenderTimerTick still drives a frame (commit + wait).
        return AppBuilder.Configure<CsCode.App.App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false, ShouldRenderOnUIThread = false })
            .WithCodeFonts();
    }
}

/// <summary>The soak switches: <c>CSCODE_SOAK=1</c> runs the long variants for <c>CSCODE_SOAK_MINUTES</c> (default 10) minutes each.</summary>
public static class SoakSettings
{
    public const string Category = "Soak";

    public static bool Long => Environment.GetEnvironmentVariable("CSCODE_SOAK") is "1" or "true";

    public static TimeSpan Duration =>
        TimeSpan.FromMinutes(double.TryParse(Environment.GetEnvironmentVariable("CSCODE_SOAK_MINUTES"), System.Globalization.CultureInfo.InvariantCulture, out var m) && m > 0 ? m : 10);
}
