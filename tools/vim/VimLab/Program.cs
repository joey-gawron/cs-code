using CsCode.Vim.Tests.Oracle;

// Usage: VimLab '<text with \n and a ‸ cursor marker>' '<keys in Vim notation>' [ex setup ...]
// Prints what real Neovim ends up with and what CsCode.Vim ends up with, plus a diff.
if (args.Length < 2)
{
    Console.Error.WriteLine("usage: VimLab '<text>' '<keys>' [ex-setup...]");
    return 2;
}

var text = args[0].Replace("\\n", "\n").Replace("\\t", "\t");
var marker = text.IndexOf('‸');
var lines = text.Replace("‸", "").Split('\n');
var (line, col) = (0, 0);
if (marker >= 0)
{
    var before = text[..marker].Split('\n');
    line = before.Length - 1;
    col = before[^1].Length;
}

var input = new OracleInput(lines, line, col, args[1], args.Skip(2).ToArray());

VimSnapshot? nvim = null;
VimSnapshot? initial = null;
if (NvimOracle.Available)
{
    using var client = NvimOracle.Start(input, out initial);
    NvimOracle.Type(client, input.Keys);
    nvim = NvimOracle.Snapshot(client);
    Console.WriteLine("=== nvim ===");
    Console.Write(nvim);
}
else Console.Error.WriteLine("nvim not found");

#if ENGINE
try
{
    var mine = CsCode.Vim.Tests.Harness.EngineRunner.Run(input);
    Console.WriteLine("=== CsCode.Vim ===");
    Console.Write(mine);
    if (nvim is not null)
        Console.WriteLine(CsCode.Vim.Tests.Harness.EngineRunner.Diff(nvim, mine, initial) is { Length: > 0 } d ? "=== DIFF ===\n" + d : "=== identical ===");
}
catch (Exception ex)
{
    Console.WriteLine("=== CsCode.Vim threw ===\n" + ex);
}
#endif
return 0;
