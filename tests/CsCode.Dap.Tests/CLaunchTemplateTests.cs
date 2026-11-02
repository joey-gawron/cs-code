using System.Text.Json;
using CsCode.Core.Platform;
using CsCode.Dap.Adapters;
using CsCode.Dap.Configuration;
using CsCode.Dap.Tests.Fakes;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.Dap.Tests;

/// <summary>Stage 3 (C): the C launch templates of CodeLLDB, lldb-dap and GDB, and the single-file build task they bring.</summary>
public sealed class CLaunchTemplateTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DapPlatform Platform(TempDirectory dir) => new() { Workspace = new FakeWorkspace(dir.Uri) };

    [Fact]
    public async Task A_cmake_project_gets_the_launch_target_and_active_file_templates_before_the_generic_ones()
    {
        using var dir = new TempDirectory("ctemplates-");
        dir.WriteFile("CMakeLists.txt", "project(x C)\n");
        dir.WriteFile("main.c", "int main(void) { return 0; }\n");
        var codelldb = new CodeLldbAdapterFactory(Platform(dir));

        var configs = await codelldb.ProvideInitialAsync(dir.Uri, Ct);

        Assert.Equal(["C: Debug CMake launch target (CodeLLDB)", "C: Debug active file (CodeLLDB)", "LLDB: Launch program", "LLDB: Attach to process"],
            configs.Select(c => c.Name));
        var target = configs[0];
        Assert.Equal(("lldb", "launch", "${command:cmake.launchTargetPath}"), (target.Type, target.Request, target.GetString("program")));
        Assert.Equal("c", target.Properties["sourceLanguages"][0].GetString());
        var file = configs[1];
        Assert.Equal(("${fileDirname}/${fileBasenameNoExtension}", CFolder.BuildTaskLabel), (file.GetString("program"), file.PreLaunchTask));

        var (path, content) = Assert.Single(await codelldb.ProvideInitialFilesAsync(dir.Uri, Ct));
        Assert.Equal("tasks.json", path);
        using var tasks = JsonDocument.Parse(content);
        var task = tasks.RootElement.GetProperty("tasks")[0];
        Assert.Equal(CFolder.BuildTaskLabel, task.GetProperty("label").GetString());
        Assert.Equal("process", task.GetProperty("type").GetString());
        Assert.Equal(["-g", "-O0", "-Wall", "${file}", "-o", "${fileDirname}/${fileBasenameNoExtension}"], task.GetProperty("args").EnumerateArray().Select(a => a.GetString()));
        Assert.Equal("$gcc", task.GetProperty("problemMatcher").GetString());
    }

    [Fact]
    public async Task C_sources_without_cmake_get_the_active_file_template_for_each_debugger()
    {
        using var dir = new TempDirectory("ctemplates-");
        dir.WriteFile("hello.c", "int main(void) { return 0; }\n");
        var lldbDap = await new LldbDapAdapterFactory(Platform(dir)).ProvideInitialAsync(dir.Uri, Ct);
        var gdb = await new GdbAdapterFactory(Platform(dir)).ProvideInitialAsync(dir.Uri, Ct);

        Assert.Equal("C: Debug active file (lldb-dap)", lldbDap[0].Name);
        Assert.False(lldbDap[0].Properties.ContainsKey("sourceLanguages")); // CodeLLDB's own key
        Assert.Equal(("C: Debug active file (GDB)", "gdb"), (gdb[0].Name, gdb[0].Type));
        Assert.DoesNotContain(lldbDap, c => c.Name.Contains("CMake", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Other_folders_keep_only_the_generic_templates()
    {
        using var dir = new TempDirectory("ctemplates-");
        dir.WriteFile("notes.txt", "x\n");
        var codelldb = new CodeLldbAdapterFactory(Platform(dir));
        Assert.Equal(["LLDB: Launch program", "LLDB: Attach to process"], (await codelldb.ProvideInitialAsync(dir.Uri, Ct)).Select(c => c.Name));
        Assert.Empty(await codelldb.ProvideInitialFilesAsync(dir.Uri, Ct));
    }
}
