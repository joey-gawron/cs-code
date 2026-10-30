using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using CsCode.Editor.TreeSitter;
using CsCode.Editor.TreeSitter.Grammars;
using CsCode.Editor.TreeSitter.Highlighting;
using CsCode.Editor.TreeSitter.Native;
using CsCode.TestDoubles;
using Xunit;
using static CsCode.Editor.Tests.TreeSitter.TreeSitterTestSupport;

namespace CsCode.Editor.Tests.TreeSitter;

/// <summary>
/// B6 — what OUT-OF-PROCESS parsing would cost (docs/TREE-SITTER.md, "Isolation": is a parser process worth it?). Measured against the
/// in-process engine: a native helper process (C, the bundle's runtime and the C# grammar loaded, a tree parsed) that answers framed messages
/// over its stdin/stdout pipes — the cheapest helper possible, so its numbers are a lower bound for any real one:
/// <list type="bullet">
/// <item>the helper's resident memory floor, and a .NET helper's (the headless <c>cscode-server</c>, when it is built);</item>
/// <item>spawn → ready (what a restart after a crash costs);</item>
/// <item>one keystroke's round trip: the edit in, one viewport's token rows out (the real size of a 60-line C# viewport), p50/p99;</item>
/// <item>the first transfer of a 1 MB document;</item>
/// <item>beside them, the in-process cost of the same keystroke (incremental parse + viewport highlight), p50/p99.</item>
/// </list>
/// </summary>
public partial class TreeSitterBenchmarks
{
    private const string HelperSource = """
        #include <dlfcn.h>
        #include <stdint.h>
        #include <stdio.h>
        #include <stdlib.h>
        #include <string.h>
        /* argv: runtime.so grammar.so symbol. Loads both, parses a little C#, says "ready", then echoes framed messages (u32 length + bytes). */
        int main(int argc, char **argv) {
            if (argc < 4) return 2;
            void *rt = dlopen(argv[1], RTLD_NOW | RTLD_GLOBAL), *g = dlopen(argv[2], RTLD_NOW);
            if (!rt || !g) { fprintf(stderr, "%s\n", dlerror()); return 3; }
            const void *(*lang)(void) = (const void *(*)(void))dlsym(g, argv[3]);
            void *(*parser_new)(void) = (void *(*)(void))dlsym(rt, "ts_parser_new");
            int (*set_language)(void *, const void *) = (int (*)(void *, const void *))dlsym(rt, "ts_parser_set_language");
            void *(*parse)(void *, void *, const char *, uint32_t) = (void *(*)(void *, void *, const char *, uint32_t))dlsym(rt, "ts_parser_parse_string");
            void *p = parser_new();
            set_language(p, lang());
            const char *src = "namespace N { class C { int F(int a) { return a * 2; } } }";
            if (!parse(p, NULL, src, (uint32_t)strlen(src))) return 4;
            fputs("ready\n", stdout);
            fflush(stdout);
            char *buf = NULL; uint32_t cap = 0, n;
            while (fread(&n, 4, 1, stdin) == 1) {
                if (n > cap) { buf = realloc(buf, n); cap = n; }
                if (fread(buf, 1, n, stdin) != n) break;
                fwrite(&n, 4, 1, stdout);
                fwrite(buf, 1, n, stdout);
                fflush(stdout);
            }
            return 0;
        }
        """;

    [Fact]
    public async Task B6_what_out_of_process_parsing_would_cost()
    {
        RequireBench();
        var bundle = RequireBundle();
        RequireRuntime();
        Assert.SkipUnless(File.Exists("/usr/bin/gcc") || File.Exists("/usr/bin/cc"), "needs gcc to build the helper");
        using var dir = new TempDirectory("cscode-ts-b6-");
        var helper = Path.Combine(dir.Path, "ts-helper");
        File.WriteAllText(Path.Combine(dir.Path, "helper.c"), HelperSource);
        var gcc = Process.Start(new ProcessStartInfo(File.Exists("/usr/bin/gcc") ? "/usr/bin/gcc" : "/usr/bin/cc", ["-O2", "-o", helper, Path.Combine(dir.Path, "helper.c"), "-ldl"]) { UseShellExecute = false })!;
        await gcc.WaitForExitAsync();
        Assert.Equal(0, gcc.ExitCode);

        // the payload of one keystroke: the edit in (offsets, points, the typed text), one 60-line viewport of C# token rows out
        var csharp = string.Concat(Enumerable.Range(0, 2500).Select(i => $"    public static int Method{i}(int first, string second) {{ var x = first * {i}; return x + second.Length; }} // c{i}\n"));
        csharp = "namespace N\n{\npublic static class K\n{\n" + csharp + "}\n}\n";
        var rowsBytes = ViewportRowBytes(csharp, 60);
        var editBytes = 64;
        Record("B6 out-of-process", "info", "one 60-line C# viewport as token rows", rowsBytes, "bytes");

        // helper: spawn -> ready, memory floor
        var psi = new ProcessStartInfo(helper, [bundle.RuntimePath, Path.Combine(bundle.Directory, bundle.Grammar("c_sharp")!.File), "tree_sitter_c_sharp"])
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        var spawn = Stopwatch.StartNew();
        using var child = Process.Start(psi)!;
        try
        {
            var stdin = child.StandardInput.BaseStream;
            var stdout = child.StandardOutput.BaseStream;
            var ready = new byte[6];
            await stdout.ReadExactlyAsync(ready);
            Assert.Equal("ready\n", System.Text.Encoding.ASCII.GetString(ready));
            Record("B6 out-of-process", "native helper", "spawn -> grammar loaded and ready", spawn.Elapsed.TotalMilliseconds, "ms");
            Record("B6 out-of-process", "native helper", "resident memory (VmRSS) when ready", RssMb(child.Id), "MB");

            // a keystroke: edit in, rows back (the helper echoes, so both directions carry the larger message: an upper bound for the bytes,
            // the lower bound for the work, since a real helper would also parse and query, which costs what it costs in process)
            var payload = new byte[Math.Max(rowsBytes, editBytes)];
            Random.Shared.NextBytes(payload);
            var times = new List<double>();
            for (var i = 0; i < 2200; i++)
            {
                var sw = Stopwatch.StartNew();
                await RoundTrip(stdin, stdout, payload.AsMemory(0, i % 2 == 0 ? rowsBytes : editBytes));
                if (i >= 200) times.Add(sw.Elapsed.TotalMilliseconds);
            }
            times.Sort();
            Record("B6 out-of-process", "native helper", "keystroke IPC round trip p50", P(times, 0.5), "ms");
            Record("B6 out-of-process", "native helper", "keystroke IPC round trip p99", P(times, 0.99), "ms");

            var big = new byte[2 * 1024 * 1024]; // 1 M characters as UTF-16, as the engine reads text
            var first = Stopwatch.StartNew();
            await RoundTrip(stdin, stdout, big);
            Record("B6 out-of-process", "native helper", "first transfer of a 1 MB document (there and back)", first.Elapsed.TotalMilliseconds, "ms");
        }
        finally
        {
            child.StandardInput.Close();
            if (!child.WaitForExit(5000)) child.Kill();
        }

        // a .NET helper's floor: the headless server CsCode already ships, idle
        var server = Path.GetFullPath(Path.Combine(RepoRoot(), "src", "CsCode.Server", "bin", "Debug", "net10.0", "cscode-server.dll"));
        if (File.Exists(server))
        {
            using var data = new TempDirectory("cscode-ts-b6-server-");
            var sp = new ProcessStartInfo("dotnet", [server, "--stdio", "--data-dir", data.Path])
            {
                UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using var s = Process.Start(sp)!;
            try
            {
                await Task.Delay(3000);
                Record("B6 out-of-process", ".NET helper (cscode-server)", "resident memory (VmRSS) idle after 3 s", RssMb(s.Id), "MB");
            }
            finally
            {
                s.Kill(entireProcessTree: true);
                s.WaitForExit(5000);
            }
        }

        // in process, the same keystroke: incremental parse after one typed character + highlight of the 60-line viewport around it
        using var registry = NewRegistry();
        var cs = RequireGrammar("c_sharp");
        using var doc = new SyntaxDocument(registry, cs);
        var text = csharp;
        Assert.Equal(ParseOutcome.Completed, doc.Parse(new StringInput(text), long.MaxValue));
        var paint = new PaintBuffer();
        var stacks = new TestStacks();
        var inProcess = new List<double>();
        for (var i = 0; i < 300; i++)
        {
            var line = 1000 + i % 60;
            var at = new StringInput(text).LineStart(line) + 30;
            text = text.Insert(at, "x");
            var input = new StringInput(text); // the editor's snapshot input is O(1); this test input's line table is built outside the clock
            var sw = Stopwatch.StartNew();
            doc.ApplyEdit(new TextEditInfo(at, at, at + 1, line, 30, line, 30, line, 31));
            doc.Parse(input, long.MaxValue);
            doc.Highlight(input, input.LineStart(line - 30), input.LineStart(line + 30), stacks, paint);
            if (i >= 30) inProcess.Add(sw.Elapsed.TotalMilliseconds);
        }
        inProcess.Sort();
        Record("B6 out-of-process", "in process", $"keystroke: incremental parse + 60-line highlight ({text.Length / 1024} K chars of C#) p50", P(inProcess, 0.5), "ms");
        Record("B6 out-of-process", "in process", "keystroke p99", P(inProcess, 0.99), "ms");
    }

    private static double P(List<double> sorted, double q) => sorted[Math.Min(sorted.Count - 1, (int)(sorted.Count * q))];

    private static async Task RoundTrip(Stream stdin, Stream stdout, ReadOnlyMemory<byte> message)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)message.Length);
        await stdin.WriteAsync(header);
        await stdin.WriteAsync(message);
        await stdin.FlushAsync();
        await stdout.ReadExactlyAsync(header);
        var n = (int)BinaryPrimitives.ReadUInt32LittleEndian(header);
        var back = new byte[n];
        await stdout.ReadExactlyAsync(back);
    }

    private static double RssMb(int pid)
    {
        foreach (var line in File.ReadLines($"/proc/{pid}/status"))
            if (line.StartsWith("VmRSS:", StringComparison.Ordinal))
                return double.Parse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1], CultureInfo.InvariantCulture) / 1024;
        return double.NaN;
    }

    /// <summary>The bytes a 60-line viewport of <paramref name="text"/> takes as token rows ((start, stack id) int pairs per run), in process.</summary>
    private static int ViewportRowBytes(string text, int lines)
    {
        using var registry = NewRegistry();
        using var doc = new SyntaxDocument(registry, RequireGrammar("c_sharp"));
        var input = new StringInput(text);
        doc.Parse(input, long.MaxValue);
        var paint = new PaintBuffer();
        var first = 1000;
        doc.Highlight(input, input.LineStart(first), input.LineStart(first + lines), new TestStacks(), paint);
        var runs = 0;
        for (var line = first; line < first + lines; line++)
        {
            var previous = int.MinValue;
            for (var i = input.LineStart(line); i < input.LineStart(line + 1) - 1; i++)
            {
                var id = paint.StackAt(i);
                if (id != previous) runs++;
                previous = id;
            }
        }
        return runs * 8 + lines * 4; // pairs of int32 + a count per line
    }
}
