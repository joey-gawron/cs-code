using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(CsCode.MemoryTests.TestAppBuilder))]
// Memory measurements are process-wide (heap, resident set, threads, descriptors) and CSCODE_HOME is an environment variable: one test at a time.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace CsCode.MemoryTests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<CsCode.App.App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

/// <summary>The soak switches: <c>CSCODE_SOAK=1</c> runs the long variants for <c>CSCODE_SOAK_MINUTES</c> (default 10) minutes each.</summary>
public static class SoakSettings
{
    public const string Category = "Soak";

    public static bool Long => Environment.GetEnvironmentVariable("CSCODE_SOAK") is "1" or "true";

    public static TimeSpan Duration =>
        TimeSpan.FromMinutes(double.TryParse(Environment.GetEnvironmentVariable("CSCODE_SOAK_MINUTES"), System.Globalization.CultureInfo.InvariantCulture, out var m) && m > 0 ? m : 10);
}
