using System.Diagnostics;
using System.Text.Json;

namespace CsCode.TestDoubles;

/// <summary>
/// What the real GDB/Rust debugging tests need on this machine: GDB with its native DAP interpreter (<c>gdb -i=dap</c>, GDB 14+) and the Rust
/// toolchain (<c>cargo</c>, <c>rustc</c>) to build the sample offline. Tests skip with <see cref="SkipReason"/> when something is missing.
/// Shared by the DAP, Debug panels and App tests (new file in the shared test doubles).
/// </summary>
public static class GdbToolchain
{
    public const string Category = "RealAdapter";

    public static string? FindGdb() => JsDebugToolchain.FindOnPath("gdb") ?? (File.Exists("/usr/bin/gdb") ? "/usr/bin/gdb" : null);

    public static string? FindCargo() => JsDebugToolchain.FindOnPath("cargo");

    public static string? FindRustc() => JsDebugToolchain.FindOnPath("rustc");

    private static readonly Lazy<string?> DapSupport = new(ProbeDap);

    /// <summary>Null when everything the real GDB tests need is present; otherwise an explicit reason to skip.</summary>
    public static string? SkipReason()
    {
        if (!OperatingSystem.IsLinux()) return "the GDB/Rust tests run on Linux only (ptrace, /proc)";
        if (FindGdb() is null) return "GDB ('gdb') is not on PATH, so Rust cannot be debugged";
        if (FindCargo() is null || FindRustc() is null) return "the Rust toolchain ('cargo', 'rustc') is not on PATH, so the Rust sample cannot be built";
        return DapSupport.Value;
    }

    /// <summary>GDB speaks DAP natively since 14 (<c>-i=dap</c>); an older GDB fails at once with "Interpreter `dap' unrecognized".</summary>
    private static string? ProbeDap()
    {
        try
        {
            var psi = new ProcessStartInfo(FindGdb()!) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in new[] { "-nx", "-batch", "-ex", "python import gdb.dap" }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var err = p.StandardError.ReadToEndAsync();
            _ = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(30_000)) { p.Kill(entireProcessTree: true); return "GDB did not answer a version probe within 30 s"; }
            return p.ExitCode == 0 && !err.Result.Contains("Error", StringComparison.Ordinal) ? null
                : "this GDB has no DAP support (gdb -i=dap needs GDB 14 or newer with Python): " + err.Result.Trim();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return "GDB could not be started: " + ex.Message; }
    }

    /// <summary><c>gdb --version</c>'s first line, for the evidence the tests record.</summary>
    public static string Version()
    {
        try
        {
            var psi = new ProcessStartInfo(FindGdb() ?? "gdb", "--version") { RedirectStandardOutput = true, UseShellExecute = false };
            using var p = Process.Start(psi)!;
            var line = p.StandardOutput.ReadLine() ?? "";
            p.StandardOutput.ReadToEnd();
            p.WaitForExit(10_000);
            return line.Trim();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return "?"; }
    }
}

/// <summary>
/// The Rust program the real GDB tests debug: a cargo package (<c>src/main.rs</c> + <c>src/util.rs</c>, no dependencies, so it builds offline)
/// built by the real cargo in a temp directory. It covers what the evaluation matrix asks for: String, &amp;str, Vec, HashMap, Option/Result,
/// enums, structs, Box/Rc, tuples and slices; generics and closures in the call stack; a caught and an uncaught panic; <c>std::thread</c>
/// workers; a mode that waits to be attached to (<c>serve</c>) and a flood mode (<c>flood N</c>); a unit test for <c>cargo test --no-run</c>.
/// Marker comments (<c>// @name</c>) give tests stable lines.
/// </summary>
public sealed class RustSample : IDisposable
{
    public const string PackageName = "sample";

    public const string CargoToml = """
        [package]
        name = "sample"
        version = "0.1.0"
        edition = "2021"

        [dependencies]

        [profile.dev]
        opt-level = 0
        debug = true

        """;

    public const string MainSource = """
        mod util;

        use std::collections::HashMap;
        use std::rc::Rc;
        use std::time::Duration;

        #[derive(Debug, Clone, PartialEq)]
        pub enum Shape {
            Circle { radius: f64 },
            Rect(u32, u32),
            Empty,
        }

        #[derive(Debug)]
        pub struct Point {
            pub x: i32,
            pub y: i32,
        }

        #[derive(Debug)]
        pub struct Wrapper {
            pub point: Point,
            pub tag: Option<&'static str>,
        }

        fn apply<F: Fn(i32) -> i32>(f: F, value: i32) -> i32 {
            f(value) // @apply
        }

        fn parse(text: &str) -> Result<i32, String> {
            text.parse::<i32>().map_err(|e| e.to_string())
        }

        fn worker(id: u32) -> u32 {
            let mut acc = 0;
            for i in 0..3 {
                acc += id * 10 + i; // @worker
            }
            acc
        }

        fn serve() {
            let mut ticks: u64 = 0;
            loop {
                ticks += 1; // @tick
                std::thread::sleep(Duration::from_millis(100));
                if ticks > 600 {
                    break;
                }
            }
        }

        fn flood(lines: u64) {
            for i in 0..lines {
                println!("flood line {i}");
            }
            println!("flood done"); // @flood-done
        }

        fn main() {
            let args: Vec<String> = std::env::args().collect();
            let mode = args.get(1).map(String::as_str).unwrap_or("");
            if mode == "serve" { serve(); return; }
            if mode == "flood" { flood(args.get(2).and_then(|n| n.parse().ok()).unwrap_or(1_000_000)); return; }
            if mode == "threads" {
                let handles: Vec<_> = (1..=2u32)
                    .map(|id| std::thread::Builder::new().name(format!("worker-{id}")).spawn(move || worker(id)).unwrap())
                    .collect();
                let total: u32 = handles.into_iter().map(|h| h.join().unwrap()).sum();
                println!("threads done {total}"); // @threads-done
                return;
            }
            let mut x: i32 = 41; // @start
            // nothing to execute on this line: a breakpoint here moves to the next statement @comment
            let s = String::from("hello");
            let text: &str = "world";
            let v = vec![1, 2, 3];
            let mut map: HashMap<String, i32> = HashMap::new();
            map.insert("a".to_string(), 1);
            map.insert("b".to_string(), 2);
            let some: Option<i32> = Some(5);
            let none: Option<i32> = None;
            let ok = parse("12");
            let err = parse("x");
            let shape = Shape::Circle { radius: 1.5 };
            let rect = Shape::Rect(3, 4);
            let empty = Shape::Empty;
            let p = Point { x: 3, y: 4 };
            let boxed = Box::new(Point { x: 5, y: 6 });
            let shared = Rc::new(String::from("shared"));
            let shared2 = Rc::clone(&shared);
            let pair = (1, "two");
            let slice: &[i32] = &v[..2];
            let wrapper = Wrapper { point: Point { x: 7, y: 8 }, tag: Some("w") };
            let big: Vec<u32> = (0..250).map(|i| i * 2).collect();
            for i in 0..5 {
                x += i; // @loop
                println!("loop {i}"); // @print
            }
            let r = util::add(x, 1); // @call
            eprintln!("err {r}");
            let offset = 7;
            let doubled = apply(|n| n * 2 + offset, r); // @closure
            let label = util::describe("r", r); // @describe
            let caught = std::panic::catch_unwind(|| util::explode(2)).is_err(); // @caught
            println!("done {s} {text} {} {} {:?} {:?} {:?} {:?} {:?} {:?} {:?} {} {} {} {} {} {label} {caught}", // @done
                v.len(), map.len(), some, none, ok, err, shape, rect, empty, p.x + p.y, boxed.x, Rc::strong_count(&shared2), big.len(), doubled);
            println!("more {} {} {} {:?}", pair.1, slice.len(), wrapper.point.x, wrapper.tag); // @more
            if mode == "panic" {
                util::explode(1); // @uncaught
            }
        }

        """;

    public const string UtilSource = """
        use std::fmt::Display;

        pub fn add(a: i32, b: i32) -> i32 {
            let sum = a + b; // @add
            sum // @ret
        }

        pub fn describe<T: Display>(label: &str, value: T) -> String {
            let text = format!("{label}={value}"); // @generic
            text
        }

        pub fn explode(depth: u32) -> u32 {
            if depth == 0 {
                panic!("boom at the bottom"); // @panic
            }
            explode(depth - 1) + 1
        }

        #[cfg(test)]
        mod tests {
            use super::*;

            #[test]
            fn adds_numbers() {
                let total = add(2, 3); // @test
                assert_eq!(total, 5);
            }
        }

        """;

    public TempDirectory Dir { get; }
    public string MainRs => Dir.Combine("src", "main.rs");
    public string UtilRs => Dir.Combine("src", "util.rs");
    /// <summary><c>target/debug/sample</c> (after <see cref="BuildAsync"/>).</summary>
    public string Binary => Dir.Combine("target", "debug", PackageName);
    /// <summary>The unit-test executable cargo built (<c>target/debug/deps/sample-&lt;hash&gt;</c>), after <see cref="BuildTestsAsync"/>.</summary>
    public string? TestBinary { get; private set; }
    /// <summary>cargo's console output of the last build.</summary>
    public string BuildOutput { get; private set; } = "";

    private RustSample(TempDirectory dir) => Dir = dir;

    /// <summary>Writes the package only (no build): for tests that let a preLaunchTask run cargo.</summary>
    public static RustSample Write(string prefix = "rustgdb-")
    {
        var dir = new TempDirectory(prefix);
        dir.WriteFile("Cargo.toml", CargoToml);
        dir.WriteFile("src/main.rs", MainSource);
        dir.WriteFile("src/util.rs", UtilSource);
        return new RustSample(dir);
    }

    /// <summary>Writes and builds the binary (<c>cargo build --offline</c>). Throws with cargo's output when the build fails.</summary>
    public static async Task<RustSample> CreateAsync(string prefix = "rustgdb-", bool tests = false, CancellationToken ct = default)
    {
        var sample = Write(prefix);
        try
        {
            await sample.BuildAsync(ct);
            if (tests) await sample.BuildTestsAsync(ct);
            return sample;
        }
        catch
        {
            sample.Dispose();
            throw;
        }
    }

    private static readonly SemaphoreSlim SharedGate = new(1, 1);
    private static RustSample? _shared;

    /// <summary>
    /// One built sample (binary and unit-test executable) shared by every test of the process that only reads it; deleted when the process
    /// exits. Tests that write into the folder (launch.json, tasks.json) use <see cref="CreateAsync"/> or <see cref="Write"/>.
    /// </summary>
    public static async Task<RustSample> SharedAsync(CancellationToken ct = default)
    {
        await SharedGate.WaitAsync(ct);
        try
        {
            if (_shared is not null) return _shared;
            var sample = await CreateAsync("rustgdb-shared-", tests: true, ct);
            AppDomain.CurrentDomain.ProcessExit += (_, _) => sample.Dispose();
            return _shared = sample;
        }
        finally { SharedGate.Release(); }
    }

    public Task BuildAsync(CancellationToken ct = default) => CargoAsync(["build", "--offline"], ct);

    /// <summary><c>cargo test --no-run</c>; <see cref="TestBinary"/> is the executable cargo reports (its JSON message).</summary>
    public async Task BuildTestsAsync(CancellationToken ct = default)
    {
        var json = await CargoAsync(["test", "--no-run", "--offline", "--message-format=json-render-diagnostics"], ct);
        foreach (var line in json.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!line.StartsWith('{')) continue;
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.TryGetProperty("reason", out var reason) && reason.GetString() == "compiler-artifact"
                && root.TryGetProperty("executable", out var exe) && exe.ValueKind == JsonValueKind.String
                && root.TryGetProperty("profile", out var profile) && profile.TryGetProperty("test", out var test) && test.ValueKind == JsonValueKind.True)
                TestBinary = exe.GetString();
        }
        if (TestBinary is null || !File.Exists(TestBinary)) throw new InvalidOperationException("cargo test --no-run reported no test executable:\n" + json + "\n" + BuildOutput);
    }

    /// <summary>Runs cargo in the package with its own target directory (never a CARGO_TARGET_DIR from the environment); returns stdout.</summary>
    private async Task<string> CargoAsync(IEnumerable<string> args, CancellationToken ct)
    {
        var cargo = GdbToolchain.FindCargo() ?? throw new InvalidOperationException("cargo not found");
        var psi = new ProcessStartInfo(cargo) { WorkingDirectory = Dir.Path, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["CARGO_TARGET_DIR"] = Dir.Combine("target");
        psi.Environment["CARGO_TERM_COLOR"] = "never";
        psi.Environment.Remove("SSH_AUTH_SOCK");
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        BuildOutput = await stderr;
        if (p.ExitCode != 0) throw new InvalidOperationException($"cargo {string.Join(' ', psi.ArgumentList)} failed ({p.ExitCode}):\n{BuildOutput}");
        return await stdout;
    }

    /// <summary>0-based line of a <c>// @marker</c> in <paramref name="source"/> (<see cref="MainSource"/> or <see cref="UtilSource"/>).</summary>
    public static int LineOf(string source, string marker) => JsDebugSample.LineOf(source, marker);

    public static int MainLine(string marker) => LineOf(MainSource, marker);
    public static int UtilLine(string marker) => LineOf(UtilSource, marker);

    public void Dispose() => Dir.Dispose();
}
