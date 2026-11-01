using System.Diagnostics;
using Xunit;

namespace CsCode.App.Tests.Background;

/// <summary>
/// What build/Publish.props makes of a publish per runtime (docs/SESSIONS.md, "Start-up performance"), read from MSBuild's own evaluation of the
/// App project: ReadyToRun everywhere, one composite image on Linux, per-assembly images on macOS (where the runtime copies each loaded image's code
/// into private memory, so the composite cost every process ≈ 70 MB), and nothing of it for a plain build.
/// </summary>
public class PublishSettingsTests
{
    private static async Task<(string ReadyToRun, string Composite, string SelfContained)> EvaluateAsync(string? rid)
    {
        var project = Path.Combine(PublishedAppFixture.RepoRoot(), "src", "CsCode.App", "CsCode.App.csproj");
        // the dotnet running the tests (dotnet test sets DOTNET_HOST_PATH), else the one on PATH
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host && File.Exists(host) ? host : "dotnet";
        var psi = new ProcessStartInfo(dotnet) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "msbuild", project, "-nologo", "-getProperty:PublishReadyToRun", "-getProperty:PublishReadyToRunComposite", "-getProperty:SelfContained" })
            psi.ArgumentList.Add(a);
        if (rid is not null) psi.ArgumentList.Add("-p:RuntimeIdentifier=" + rid);
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = p.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await p.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.True(p.ExitCode == 0, await output + await error);
        using var json = System.Text.Json.JsonDocument.Parse(await output);
        var props = json.RootElement.GetProperty("Properties");
        return (props.GetProperty("PublishReadyToRun").GetString()!, props.GetProperty("PublishReadyToRunComposite").GetString()!, props.GetProperty("SelfContained").GetString()!);
    }

    [Theory]
    [InlineData("linux-x64", "true")]
    [InlineData("linux-arm64", "true")]
    [InlineData("osx-arm64", "false")]
    [InlineData("osx-x64", "false")]
    public async Task A_publish_is_ready_to_run_with_one_composite_image_except_on_macOS(string rid, string composite)
    {
        var (readyToRun, isComposite, selfContained) = await EvaluateAsync(rid);
        Assert.Equal("true", readyToRun);
        Assert.Equal(composite, isComposite);
        Assert.Equal("true", selfContained);
    }

    [Fact]
    public async Task A_plain_build_is_not_touched()
    {
        var (readyToRun, composite, _) = await EvaluateAsync(null);
        Assert.NotEqual("true", readyToRun);
        Assert.NotEqual("true", composite);
    }
}
