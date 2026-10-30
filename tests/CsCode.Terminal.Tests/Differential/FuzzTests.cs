using System.Diagnostics;
using System.Text;
using CsCode.Terminal.Emulator;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.Terminal.Tests.Differential;

/// <summary>
/// The fuzz pass: random bytes and random well-formed-ish sequences must never make CsCode's emulator throw or exceed its memory caps, and
/// both reference terminals must survive the same input (libvterm runs in a child process compiled from a tiny C harness, because a crash of
/// a native library in-process would take the test host down — libvterm 0.3 does crash on a CSI with more than 16 parameters).
/// </summary>
[Collection(SerialCollection.Name)]
public sealed class FuzzTests
{
    private static byte[] RandomBytes(int seed, int length)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    /// <summary>Random sequences built from the grammar (ESC, CSI with parameters, OSC, DCS, text, controls, UTF-8), far likelier to reach deep states than raw noise.</summary>
    private static byte[] RandomSequences(int seed, int count) => Encoding.UTF8.GetBytes(string.Concat(RandomTokens(seed, count)));

    private static List<string> RandomTokens(int seed, int count)
    {
        var rng = new Random(seed);
        var tokens = new List<string>(count);
        string[] finals = ["A", "B", "C", "D", "E", "F", "G", "H", "J", "K", "L", "M", "P", "S", "T", "X", "@", "b", "d", "f", "g", "h", "l", "m", "n", "r", "s", "u", "c", "t", " q", "!p", "$p"];
        for (var i = 0; i < count; i++)
        {
            var sb = new StringBuilder();
            tokens.Add(Token(sb));
        }
        return tokens;

        string Token(StringBuilder sb)
        {
            switch (rng.Next(10))
            {
                case 0 or 1 or 2: sb.Append(new string((char)rng.Next(0x20, 0x7f), rng.Next(1, 30))); break;
                case 3: sb.Append(rng.Next(3) switch { 0 => "中文", 1 => "é", _ => "\U0001F600" }); break;
                case 4: sb.Append((char)rng.Next(0, 0x20)); break;
                case 5:
                {
                    sb.Append("\e[");
                    if (rng.Next(4) == 0) sb.Append("?>="[rng.Next(3)]);
                    var n = rng.Next(0, 8); // stays under libvterm's 16-parameter limit
                    for (var k = 0; k < n; k++) { if (k > 0) sb.Append(rng.Next(5) == 0 ? ':' : ';'); sb.Append(rng.Next(0, rng.Next(2) == 0 ? 10 : 3000)); }
                    sb.Append(finals[rng.Next(finals.Length)]);
                    break;
                }
                case 6: sb.Append("\e]").Append(rng.Next(0, 140)).Append(';').Append(new string('x', rng.Next(0, 50))).Append(rng.Next(2) == 0 ? "\a" : "\e\\"); break;
                case 7: sb.Append('\e').Append("78DEHMc=>"[rng.Next(9)]); break;
                case 8: sb.Append("\e(").Append("0BA"[rng.Next(3)]); break;
                default: sb.Append("\eP").Append(new string('q', rng.Next(0, 20))).Append("\e\\"); break;
            }
            return sb.ToString();
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Random_bytes_never_throw_and_stay_within_the_memory_caps(int seed)
    {
        var t = new VtEmulator(80, 24, new EmulatorOptions { ScrollbackLines = 1000, ScrollbackMaxBytes = 4 * 1024 * 1024 });
        var data = RandomBytes(seed, 8 * 1024 * 1024);
        GC.Collect();
        var before = GC.GetTotalMemory(true);
        for (var i = 0; i < data.Length; i += 4096) t.Feed(data.AsSpan(i, Math.Min(4096, data.Length - i)));
        t.Resize(57, 13);
        t.Resize(120, 40);
        _ = t.CreateSnapshot(1);
        Assert.True(t.Scrollback.Bytes <= 4 * 1024 * 1024);
        var after = GC.GetTotalMemory(true);
        GC.KeepAlive(data);
        Assert.True(after - before < 16 * 1024 * 1024, $"the emulator holds {(after - before) / 1024} KB after 8 MB of noise");
    }

    [Theory]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    public void Random_sequences_never_throw(int seed)
    {
        var t = new VtEmulator(40, 12);
        var answers = 0;
        t.Respond = _ => answers++;
        var data = RandomSequences(seed, 50_000);
        t.Feed(data);
        for (var i = 0; i < 20; i++) t.Resize(10 + i * 7 % 90, 3 + i * 5 % 30);
        Assert.InRange(t.CursorX, 0, t.Columns - 1);
        Assert.InRange(t.CursorY, 0, t.Rows - 1);
        _ = t.CreateSnapshot(1);
    }

    [Fact]
    public void Tmux_survives_the_same_noise()
    {
        Assert.SkipUnless(TmuxOracle.Executable is not null, "tmux is not installed");
        using var tmux = new TmuxOracle();
        var data = RandomBytes(7, 256 * 1024).Concat(RandomSequences(7, 5000)).ToArray();
        var screen = tmux.Run(data, 80, 24); // throws if the server died or did not answer
        Assert.Equal(80, screen.Columns);
        foreach (var pid in tmux.StartedPids) Assert.False(Support.ProcessChecks.IsAlive(pid), $"tmux process {pid} survived");
    }

    [Fact]
    public async Task Libvterm_survives_the_same_noise_in_a_child_process()
    {
        Assert.SkipUnless(LibVtermOracle.LibraryPath is not null, "libvterm is not available");
        var gcc = Support.ShellSandbox.Which("gcc") ?? Support.ShellSandbox.Which("cc");
        Assert.SkipWhen(gcc is null, "no C compiler to build the libvterm harness");
        using var dir = new TempDirectory("cscode-vterm-fuzz-");
        var lib = Path.GetDirectoryName(LibVtermOracle.LibraryPath)!;
        File.WriteAllText(dir.Combine("harness.c"), """
            #include <stdio.h>
            #include <stdlib.h>
            typedef struct VTerm VTerm; typedef struct VTermScreen VTermScreen;
            VTerm *vterm_new(int rows, int cols); void vterm_free(VTerm*); void vterm_set_utf8(VTerm*, int);
            size_t vterm_input_write(VTerm*, const char*, size_t); VTermScreen *vterm_obtain_screen(VTerm*);
            void vterm_screen_reset(VTermScreen*, int); void vterm_screen_enable_altscreen(VTermScreen*, int);
            int main(int argc, char **argv) {
              FILE *f = fopen(argv[1], "rb"); if (!f) return 2;
              VTerm *vt = vterm_new(24, 80); vterm_set_utf8(vt, 1); VTermScreen *s = vterm_obtain_screen(vt);
              vterm_screen_enable_altscreen(s, 1); vterm_screen_reset(s, 1);
              char buf[4096]; size_t n; while ((n = fread(buf, 1, sizeof buf, f)) > 0) vterm_input_write(vt, buf, n);
              vterm_free(vt); fclose(f); puts("survived"); return 0; }
            """);
        var harness = dir.Combine("harness");
        var cc = Process.Start(new ProcessStartInfo(gcc!, ["-O1", "-o", harness, dir.Combine("harness.c"), "-L" + lib, "-lvterm", "-Wl,-rpath," + lib]) { RedirectStandardError = true })!;
        var err = await cc.StandardError.ReadToEndAsync();
        await cc.WaitForExitAsync();
        Assert.True(cc.ExitCode == 0, "the harness did not compile: " + err);

        async Task<int> Run(byte[] data)
        {
            var file = dir.Combine("in.bin");
            await File.WriteAllBytesAsync(file, data);
            using var p = Process.Start(new ProcessStartInfo(harness, [file]) { RedirectStandardOutput = true })!;
            var output = await p.StandardOutput.ReadToEndAsync();
            Assert.True(p.WaitForExit(60_000), "libvterm hung");
            return p.ExitCode == 0 && output.Contains("survived") ? 0 : p.ExitCode;
        }

        // Raw noise: libvterm survives it, as CsCode's emulator does.
        Assert.Equal(0, await Run(RandomBytes(5, 4 * 1024 * 1024)));

        // Grammar-built sequences: CsCode's emulator survives them (Random_sequences_never_throw); libvterm 0.3 does not, so the shortest
        // crashing prefix is found and its last token reported, which is what this test documents (the reference's bug, not ours).
        var tokens = RandomTokens(5, 100_000);
        if (await Run(Encoding.UTF8.GetBytes(string.Concat(tokens))) == 0) return;
        int lo = 0, hi = tokens.Count; // the prefix of length hi crashes, of length lo does not
        while (hi - lo > 1)
        {
            var mid = (lo + hi) / 2;
            if (await Run(Encoding.UTF8.GetBytes(string.Concat(tokens.Take(mid)))) == 0) lo = mid; else hi = mid;
        }
        var trigger = tokens[hi - 1];
        var ours = new VtEmulator(80, 24);
        ours.Feed(Encoding.UTF8.GetBytes(string.Concat(tokens.Take(hi)))); // CsCode survives the same prefix
        var context = string.Concat(tokens.Skip(Math.Max(0, hi - 4)).Take(Math.Min(4, hi)));
        Assert.Skip($"libvterm 0.3 crashes (a reference bug; CsCode's emulator survives the same input) on token {hi - 1}: '{Printable(trigger)}', after '{Printable(context)}'");
    }

    private static string Printable(string s) => string.Concat(s.Select(c => c < ' ' || c == 0x7f ? $"\\x{(int)c:x2}" : c.ToString()));
}
