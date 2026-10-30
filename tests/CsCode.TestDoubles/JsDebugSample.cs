using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace CsCode.TestDoubles;

/// <summary>
/// What the real js-debug tests need on this machine: Node.js, the TypeScript compiler and Microsoft's js-debug DAP server
/// (<c>tools/fetch-js-debug.sh</c> → <c>.scratch/tools/js-debug/&lt;version&gt;/js-debug</c>, or <c>CSCODE_JSDEBUG</c>). Tests skip with
/// <see cref="SkipReason"/> when something is missing. Shared by the DAP, Debug panels and App tests (new file in the shared test doubles).
/// </summary>
public static class JsDebugToolchain
{
    public const string Category = "RealAdapter";
    public const string PinnedVersion = "v1.140.0";

    public static string? FindOnPath(string name)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir, OperatingSystem.IsWindows() ? name + (name == "node" ? ".exe" : ".cmd") : name);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { }
        }
        return null;
    }

    public static string? FindNode() => FindOnPath("node");

    public static string? FindTsc() => FindOnPath("tsc") ?? (File.Exists("/usr/local/bin/tsc") ? "/usr/local/bin/tsc" : null);

    /// <summary>The js-debug directory (the one containing <c>src/dapDebugServer.js</c>): <c>CSCODE_JSDEBUG</c>, else the repo-local fetch location.</summary>
    public static string? FindJsDebug()
    {
        if (Environment.GetEnvironmentVariable("CSCODE_JSDEBUG") is { Length: > 0 } env)
        {
            if (File.Exists(env)) return Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(env)));
            if (File.Exists(Path.Combine(env, "src", "dapDebugServer.js"))) return env;
            if (File.Exists(Path.Combine(env, "js-debug", "src", "dapDebugServer.js"))) return Path.Combine(env, "js-debug");
        }
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, ".scratch", "tools", "js-debug", PinnedVersion, "js-debug");
            if (File.Exists(Path.Combine(candidate, "src", "dapDebugServer.js"))) return candidate;
        }
        return null;
    }

    /// <summary>The pinned archive the fetch script downloads (next to the extracted adapter), when present.</summary>
    public static string? FindArchive()
    {
        if (FindJsDebug() is not { } dir) return null;
        var archive = Path.Combine(Path.GetDirectoryName(dir)!, "js-debug-dap-" + PinnedVersion + ".tar.gz");
        return File.Exists(archive) ? archive : null;
    }

    /// <summary>Null when everything the real js-debug tests need is present; otherwise an explicit reason to skip.</summary>
    public static string? SkipReason(bool needsTsc = true)
    {
        if (FindNode() is null) return "Node.js ('node') is not on PATH, so js-debug cannot run";
        if (needsTsc && FindTsc() is null) return "the TypeScript compiler ('tsc') is not on PATH, so the TypeScript sample cannot be compiled";
        if (FindJsDebug() is null) return "js-debug is not available: run tools/fetch-js-debug.sh (expects .scratch/tools/js-debug/" + PinnedVersion + "/js-debug) or set CSCODE_JSDEBUG";
        return null;
    }
}

/// <summary>
/// The TypeScript sample the real js-debug tests debug: <c>src/main.ts</c> + <c>src/util.ts</c>, compiled by the real <c>tsc</c> with source maps
/// into <c>out/</c> (the generated JavaScript has DIFFERENT line numbers than the TypeScript: class fields, split try/catch). Marker comments
/// (<c>// @name</c>) give tests stable lines. Command-line modes select extra behaviour (<c>uncaught</c>, <c>flood N</c>, <c>serve</c>).
/// </summary>
public sealed class JsDebugSample : IDisposable
{
    public const string MainSource = """
        import { add, Point, shout } from "./util";

        // Node's own typings (@types/node) are an npm package; the little the sample uses is declared here instead.
        declare const process: { argv: string[]; stdout: { write(text: string): boolean } };

        async function delay(ms: number): Promise<void> {
          await new Promise<void>(resolve => setTimeout(resolve, ms));
        }

        async function work(n: number): Promise<number> {
          await delay(1);
          const r = add(n, 1); // @async
          return r;
        }

        function fails(): void {
          throw new Error("boom"); // @throw
        }

        function flood(lines: number): void {
          const chunk: string[] = [];
          for (let i = 0; i < lines; i++) {
            chunk.push("flood line " + i);
            if (chunk.length === 10000) { process.stdout.write(chunk.join("\n") + "\n"); chunk.length = 0; }
          }
          if (chunk.length > 0) process.stdout.write(chunk.join("\n") + "\n");
          console.log("flood done"); // @flood-done
        }

        function serve(): void {
          let ticks = 0;
          const timer = setInterval(() => {
            ticks++; // @tick
            if (ticks > 600) clearInterval(timer);
          }, 100);
        }

        async function main(): Promise<void> {
          const mode = process.argv[2] ?? "";
          if (mode === "flood") { flood(Number(process.argv[3] ?? "1000000")); return; }
          if (mode === "serve") { serve(); return; }
          let x = 41; // @start
          // nothing to execute on this line: a breakpoint here moves to the next statement @comment
          const s = "hello";
          const p = new Point(3, 4);
          const m = new Map<string, number>([["a", 1], ["b", 2]]);
          const set = new Set<number>([1, 2, 3]);
          const big = Array.from({ length: 250 }, (_, i) => i * 2);
          const nested = { name: "outer", inner: { depth: 2, list: [1, 2] } };
          for (let i = 0; i < 5; i++) {
            x += i; // @loop
            console.log(`loop ${i}`); // @print
          }
          const r = await work(x); // @call
          console.error(`err ${r}`);
          try { fails(); } catch { /* caught */ } // @trycatch
          const total = shout(s) + x; // @shout
          console.log(`done ${s} ${r} ${p.length} ${m.size} ${set.size} ${big.length} ${nested.name} ${total}`); // @done
          if (mode === "uncaught") setTimeout(() => { throw new Error("uncaught boom"); }, 0); // @uncaught
        }

        main();

        """;

    public const string UtilSource = """
        export function add(a: number, b: number): number {
          const sum = a + b; // @add
          return sum; // @ret
        }

        export class Point {
          constructor(public x: number, public y: number) {}
          get length(): number { return Math.sqrt(this.x * this.x + this.y * this.y); }
        }

        export function shout(text: string): string {
          const loud = text.toUpperCase(); // @shout-body
          return loud + "!";
        }

        """;

    public const string TsConfig = """
        {
          "compilerOptions": {
            "target": "ES2022",
            "module": "commonjs",
            "sourceMap": true,
            "outDir": "out",
            "rootDir": "src",
            "strict": true
          }
        }

        """;

    public TempDirectory Dir { get; }
    public string MainTs => Dir.Combine("src", "main.ts");
    public string UtilTs => Dir.Combine("src", "util.ts");
    public string MainJs => Dir.Combine("out", "main.js");
    public string OutDir => Dir.Combine("out");
    /// <summary>tsc's console output (empty on success).</summary>
    public string CompilerOutput { get; private set; } = "";

    private JsDebugSample(TempDirectory dir) => Dir = dir;

    /// <summary>Writes the sources only (no compilation): for tests that let a preLaunchTask run tsc.</summary>
    public static JsDebugSample Write(string prefix = "jsdebug-")
    {
        var dir = new TempDirectory(prefix);
        dir.WriteFile("src/main.ts", MainSource);
        dir.WriteFile("src/util.ts", UtilSource);
        dir.WriteFile("tsconfig.json", TsConfig);
        return new JsDebugSample(dir);
    }

    /// <summary>Writes and compiles the sample with the real tsc (<c>tsc -p</c>). Throws with tsc's output when compilation fails.</summary>
    public static async Task<JsDebugSample> CreateAsync(string prefix = "jsdebug-", CancellationToken ct = default)
    {
        var sample = Write(prefix);
        var tsc = JsDebugToolchain.FindTsc() ?? throw new InvalidOperationException("tsc not found");
        var psi = new ProcessStartInfo(tsc) { WorkingDirectory = sample.Dir.Path, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add(sample.Dir.Path);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        sample.CompilerOutput = (await stdout) + (await stderr);
        if (p.ExitCode != 0 || !File.Exists(sample.MainJs) || !File.Exists(sample.MainJs + ".map"))
        {
            sample.Dispose();
            throw new InvalidOperationException($"tsc failed ({p.ExitCode}):\n{sample.CompilerOutput}");
        }
        return sample;
    }

    /// <summary>0-based line of a <c>// @marker</c> in <paramref name="source"/> (<see cref="MainSource"/> or <see cref="UtilSource"/>).</summary>
    public static int LineOf(string source, string marker)
    {
        var lines = source.Split('\n');
        for (var i = 0; i < lines.Length; i++)
            if (lines[i].Contains("// @" + marker, StringComparison.Ordinal) || lines[i].Contains(" @" + marker, StringComparison.Ordinal) && lines[i].TrimStart().StartsWith("//", StringComparison.Ordinal))
                return i;
        throw new ArgumentException($"No marker @{marker} in the sample");
    }

    public static int MainLine(string marker) => LineOf(MainSource, marker);
    public static int UtilLine(string marker) => LineOf(UtilSource, marker);

    public void Dispose() => Dir.Dispose();
}
