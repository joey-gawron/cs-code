using System.Text.Json;
using CsCode.Core.Tasks;
using CsCode.Dap.Client;
using CsCode.Dap.Configuration;
using CsCode.Dap.Tasks;
using CsCode.Dap.Tests.Fakes;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.Dap.Tests;

/// <summary>Cargo's detected tasks: the metadata parsing (no cargo needed) and a real cargo workspace (skips without cargo).</summary>
public class CargoTaskProviderTests
{
    private static JsonElement Def(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static void NeedsCargo() => Assert.SkipWhen(RustGdbSampleCargo() is null, "cargo is not on PATH, so no cargo workspace can be read");

    private static string? RustGdbSampleCargo() => GdbToolchain.FindCargo();

    [Fact]
    public void Metadata_lists_the_workspace_members_with_their_binaries()
    {
        var json = """
            {"packages":[
              {"name":"app","id":"path+file:///w/app#0.1.0","manifest_path":"/w/app/Cargo.toml","targets":[
                {"kind":["bin"],"name":"app"},{"kind":["bin"],"name":"tool"},{"kind":["example"],"name":"ex"}]},
              {"name":"core","id":"path+file:///w/core#0.1.0","manifest_path":"/w/core/Cargo.toml","targets":[{"kind":["lib"],"name":"core"}]},
              {"name":"dep","id":"registry+https://x#dep@1.0.0","manifest_path":"/r/dep/Cargo.toml","targets":[{"kind":["bin"],"name":"dep"}]}],
             "workspace_members":["path+file:///w/app#0.1.0","path+file:///w/core#0.1.0"]}
            """;
        var packages = CargoTaskProvider.ParseMetadata(json);
        Assert.Equal(["app", "core"], packages.Select(p => p.Name));
        Assert.Equal(["app", "tool"], packages[0].Binaries);
        Assert.Empty(packages[1].Binaries);
    }

    [Fact]
    public async Task A_folder_without_Cargo_toml_has_no_cargo_tasks()
    {
        using var dir = new TempDirectory("cargotasks-");
        var provider = new CargoTaskProvider(new DapPlatform());
        Assert.Empty(await provider.ProvideTasksAsync(dir.Uri, TestToken.Ct));
    }

    [Fact]
    public async Task Without_cargo_the_workspace_wide_tasks_are_still_listed()
    {
        using var dir = new TempDirectory("cargotasks-");
        dir.WriteFile("Cargo.toml", "[package]\nname = \"x\"\nversion = \"0.1.0\"\n");
        var provider = new CargoTaskProvider(new DapPlatform()) { FindCargo = () => null };
        var tasks = await provider.ProvideTasksAsync(dir.Uri, TestToken.Ct);
        Assert.Equal(["cargo: build", "cargo: check", "cargo: clippy", "cargo: test", "cargo: run", "cargo: doc"], tasks.Select(t => t.Label));
        Assert.Equal("build", tasks[0].Group);
        Assert.Equal("test", tasks[3].Group);
        Assert.Null(tasks[4].Group);
        Assert.All(tasks, t => Assert.Equal(["$rustc"], t.ProblemMatchers));
        Assert.Equal("""{"type":"cargo","command":"build"}""", tasks[0].Definition!.Value.GetRawText());
    }

    [Fact]
    [Trait("Category", "RealTools")]
    public async Task A_real_workspace_gives_per_package_and_per_binary_tasks()
    {
        NeedsCargo();
        using var dir = Workspace();
        var provider = new CargoTaskProvider(new DapPlatform());
        var labels = (await provider.ProvideTasksAsync(dir.Uri, TestToken.Ct)).Select(t => t.Label).ToList();
        Assert.Contains("cargo: build", labels);
        Assert.Contains("cargo: build - app", labels);
        Assert.Contains("cargo: clippy - core", labels);
        Assert.Contains("cargo: doc - core", labels);
        Assert.Contains("cargo: run - app", labels);
        Assert.Contains("cargo: run - tool", labels);
        var runTool = (await provider.ProvideTasksAsync(dir.Uri, TestToken.Ct)).Single(t => t.Label == "cargo: run - tool");
        Assert.Equal(["run", "-p", "app", "--bin", "tool"], runTool.Args);
    }

    [Fact]
    [Trait("Category", "RealTools")]
    public async Task The_answer_is_cached_until_a_manifest_changes()
    {
        NeedsCargo();
        using var dir = Workspace();
        var provider = new CargoTaskProvider(new DapPlatform());
        var first = await provider.ProvideTasksAsync(dir.Uri, TestToken.Ct);
        Assert.Same(first, await provider.ProvideTasksAsync(dir.Uri, TestToken.Ct));
        var manifest = System.IO.Path.Combine(dir.Path, "core", "Cargo.toml");
        File.WriteAllText(manifest, File.ReadAllText(manifest).Replace("name = \"core\"", "name = \"kernel\""));
        File.SetLastWriteTimeUtc(manifest, DateTime.UtcNow.AddMinutes(1));
        var second = await provider.ProvideTasksAsync(dir.Uri, TestToken.Ct);
        Assert.Contains(second, t => t.Label == "cargo: build - kernel");
    }

    [Fact]
    [Trait("Category", "RealTools")]
    public async Task Cargo_build_runs_through_the_task_runner_and_a_rust_analyzer_style_entry_customises_it()
    {
        NeedsCargo();
        using var dir = Workspace();
        dir.WriteFile(".vscode/tasks.json", """
            {"version":"2.0.0","tasks":[
              {"type":"cargo","command":"check","problemMatcher":["$rustc"],"group":"build","label":"rust: cargo check"},
              {"type":"cargo","command":"build","args":["-p","core","--release"],"label":"release core"}]}
            """);
        using var lane = new DebugLane("cargo-task-lane");
        var output = new InMemoryOutputService();
        var platform = new DapPlatform { Workspace = new FakeWorkspace(dir.Uri), Output = output };
        var provider = new CargoTaskProvider(platform);
        using var service = new TaskService(lane, new DapPlatform { Workspace = platform.Workspace, Output = output, TaskProviders = () => [provider] });

        var tasks = await service.GetTasksAsync(null, TestToken.Ct);
        Assert.Equal(["rust: cargo check", "release core"], tasks.Take(2).Select(t => t.Label));
        Assert.DoesNotContain(tasks, t => t.Label == "cargo: check"); // customised by the first entry
        Assert.Contains(tasks, t => t.Label == "cargo: build" && t.Source == "cargo");

        var build = await service.RunAsync("cargo: build", null, TestToken.Ct);
        Assert.True(build.Succeeded, build.Error + "\n" + output.Text(TaskService.ChannelName));
        Assert.True(File.Exists(System.IO.Path.Combine(dir.Path, "target", "debug", OperatingSystem.IsWindows() ? "tool.exe" : "tool")));

        var release = await service.RunAsync("release core", null, TestToken.Ct);
        Assert.True(release.Succeeded, release.Error + "\n" + output.Text(TaskService.ChannelName));
        Assert.True(Directory.Exists(System.IO.Path.Combine(dir.Path, "target", "release")));
    }

    /// <summary>A workspace of two independent packages: <c>app</c> (binaries <c>app</c> and <c>tool</c>) and <c>core</c> (a library).</summary>
    private static TempDirectory Workspace()
    {
        var dir = new TempDirectory("cargotasks-");
        dir.WriteFile("Cargo.toml", "[workspace]\nmembers = [\"app\", \"core\"]\nresolver = \"2\"\n");
        dir.WriteFile("app/Cargo.toml", "[package]\nname = \"app\"\nversion = \"0.1.0\"\nedition = \"2021\"\n");
        dir.WriteFile("app/src/main.rs", "fn main() { println!(\"hi\"); }\n");
        dir.WriteFile("app/src/bin/tool.rs", "fn main() {}\n");
        dir.WriteFile("core/Cargo.toml", "[package]\nname = \"core\"\nversion = \"0.1.0\"\nedition = \"2021\"\n");
        dir.WriteFile("core/src/lib.rs", "pub fn answer() -> i32 { 42 }\n");
        return dir;
    }
}
