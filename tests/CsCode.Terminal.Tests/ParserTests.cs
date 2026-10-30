using System.Text;
using CsCode.Terminal.Emulator;
using Xunit;

namespace CsCode.Terminal.Tests;

/// <summary>The VT500 state machine on its own: what it recognises, how it decodes UTF-8, and that every string and list it keeps is bounded.</summary>
public sealed class ParserTests
{
    private sealed class Recorder : IVtHandler
    {
        public readonly List<string> Events = new();
        public void Print(int codePoint) => Events.Add($"print {char.ConvertFromUtf32(codePoint)}");
        public void PrintAscii(ReadOnlySpan<byte> text) => Events.Add($"print {Encoding.ASCII.GetString(text)}");
        public void Execute(int control) => Events.Add($"exec {control:x2}");
        public void EscDispatch(int intermediates, char final) => Events.Add($"esc {Inter(intermediates)}{final}");
        public void CsiDispatch(char prefix, VtParams parameters, int intermediates, char final) =>
            Events.Add($"csi {(prefix == '\0' ? "" : prefix.ToString())}{parameters}{Inter(intermediates)}{final}");
        public void OscDispatch(ReadOnlySpan<byte> payload) => Events.Add($"osc {Encoding.UTF8.GetString(payload)}");
        public void DcsDispatch(char prefix, VtParams parameters, int intermediates, char final, ReadOnlySpan<byte> data) =>
            Events.Add($"dcs {(prefix == '\0' ? "" : prefix.ToString())}{parameters}{Inter(intermediates)}{final} {Encoding.UTF8.GetString(data)}");

        private static string Inter(int packed)
        {
            var sb = new StringBuilder();
            while (packed != 0) { sb.Append((char)(packed & 0xff)); packed >>= 8; }
            return sb.ToString();
        }
    }

    private static List<string> Parse(params byte[][] chunks)
    {
        var r = new Recorder();
        var p = new VtParser(r);
        foreach (var c in chunks) p.Feed(c);
        return r.Events;
    }

    private static List<string> Parse(string s) => Parse(Encoding.UTF8.GetBytes(s));

    [Fact]
    public void Plain_text_and_controls()
    {
        Assert.Equal(["print ab", "exec 0d", "exec 0a", "print c"], Parse("ab\r\nc"));
    }

    [Fact]
    public void Csi_with_parameters_prefix_intermediates_and_sub_parameters()
    {
        Assert.Equal(["csi 1;2H"], Parse("\e[1;2H"));
        Assert.Equal(["csi ?1049h"], Parse("\e[?1049h"));
        Assert.Equal(["csi 2 q"], Parse("\e[2 q"));
        Assert.Equal(["csi 4:3m"], Parse("\e[4:3m"));
        Assert.Equal(["csi 38:2::10:20:30m"], Parse("\e[38:2::10:20:30m"));
        Assert.Equal(["csi ;5H"], Parse("\e[;5H"));
        Assert.Equal(["csi >c"], Parse("\e[>c"));
        Assert.Equal(["csi !p"], Parse("\e[!p"));
    }

    [Fact]
    public void Controls_inside_a_sequence_are_executed_and_the_sequence_continues()
    {
        Assert.Equal(["exec 0d", "csi 12H"], Parse("\e[1\r2H"));
    }

    [Fact]
    public void Cancel_and_substitute_abort_a_sequence()
    {
        Assert.Equal(["print x"], Parse("\e[12\x18x"));
        Assert.Equal(["print y"], Parse("\e]0;tit\x1ay"));
    }

    [Fact]
    public void Osc_ends_with_bel_or_st_and_decodes_utf8()
    {
        Assert.Equal(["osc 0;héllo"], Parse("\e]0;héllo\a"));
        Assert.Equal(["osc 2;title"], Parse("\e]2;title\e\\"));
        Assert.Equal(["osc 8;;http://x"], Parse("\e]8;;http://x\e\\"));
    }

    [Fact]
    public void Esc_inside_an_osc_that_is_not_st_aborts_it_and_starts_a_new_sequence()
    {
        Assert.Equal(["csi 1m"], Parse("\e]0;abc\e[1m"));
    }

    [Fact]
    public void Dcs_and_apc_strings_are_consumed()
    {
        Assert.Equal(["dcs $q \"p", "print x"], Parse("\eP$q\"p\e\\x"));
        Assert.Equal(["print x"], Parse("\e_some apc payload\e\\x"));
        Assert.Equal(["print x"], Parse("\e^privacy\e\\x"));
    }

    [Fact]
    public void Utf8_is_decoded_across_chunk_boundaries()
    {
        var bytes = Encoding.UTF8.GetBytes("é€😀");
        var chunks = bytes.Select(b => new[] { b }).ToArray();
        Assert.Equal(["print é", "print €", "print 😀"], Parse(chunks));
    }

    [Theory]
    [InlineData(new byte[] { 0x80 }, 1)]                       // stray continuation
    [InlineData(new byte[] { 0xC0, 0x80 }, 2)]                 // overlong lead (C0) and its continuation
    [InlineData(new byte[] { 0xE0, 0x80, 0x80 }, 3)]           // overlong three-byte: lead rejected... each byte replaced
    [InlineData(new byte[] { 0xED, 0xA0, 0x80 }, 3)]           // surrogate
    [InlineData(new byte[] { 0xF5, 0x80 }, 2)]
    [InlineData(new byte[] { 0xE2, 0x82 }, 0)]                 // incomplete at the end: still pending
    public void Invalid_utf8_becomes_replacement_characters(byte[] input, int replacements)
    {
        var events = Parse(input);
        Assert.Equal(replacements, events.Count(e => e == "print �"));
    }

    [Fact]
    public void An_interrupted_utf8_sequence_yields_a_replacement_and_the_byte_is_reprocessed()
    {
        Assert.Equal(["print �", "print A"], Parse([0xE2, 0x82, (byte)'A']));
    }

    [Fact]
    public void Decoded_c1_controls_act_as_8_bit_controls()
    {
        Assert.Equal(["csi 5A"], Parse("\u009b5A"));
        Assert.Equal(["osc 0;t"], Parse("\u009d0;t\u009c"));
        Assert.Equal(["exec 85"], Parse("\u0085"));
    }

    [Fact]
    public void Too_many_parameters_are_dropped_and_values_are_clamped()
    {
        var many = string.Join(';', Enumerable.Range(1, 200));
        var events = Parse($"\e[{many}m");
        Assert.Single(events);
        Assert.Equal(VtParams.Max, events[0].Split(';').Length);
        var big = Parse("\e[99999999999999999999H");
        Assert.Equal($"csi {VtParams.MaxValue}H", big.Single());
    }

    [Fact]
    public void Three_intermediates_make_a_sequence_be_ignored()
    {
        Assert.Equal(["print x"], Parse("\e[1!!!px"));
        Assert.Equal(["print x"], Parse("\e(((0x"));
    }

    [Fact]
    public void An_oversized_osc_is_dropped_and_its_buffer_released()
    {
        var r = new Recorder();
        var p = new VtParser(r);
        p.Feed("\e]2;"u8);
        var chunk = Encoding.ASCII.GetBytes(new string('x', 64 * 1024));
        for (var i = 0; i < (VtParser.MaxOscBytes / chunk.Length) + 8; i++) p.Feed(chunk);
        Assert.True(p.StringCapacity <= VtParser.MaxOscBytes);
        p.Feed("\aok"u8);
        Assert.Equal(["print ok"], r.Events);
        Assert.True(p.StringCapacity <= 64 * 1024, "the big buffer was kept after the string ended");
    }

    [Fact]
    public void Random_bytes_never_throw()
    {
        var rng = new Random(1234);
        var r = new Recorder();
        var p = new VtParser(r);
        var buf = new byte[4096];
        for (var i = 0; i < 2000; i++)
        {
            rng.NextBytes(buf);
            p.Feed(buf);
            if (r.Events.Count > 10_000) r.Events.Clear();
        }
    }
}
