namespace CsCode.Editor.TreeSitter.Highlighting;

/// <summary>
/// The per-character result of highlighting a range: the scope-stack id of every character in [<see cref="Start"/>, <see cref="Start"/> +
/// <see cref="Length"/>), plus the bookkeeping the painter needs (priority of the capture that painted it, which capture, which injection
/// layer, the layer's base stack). Reused between calls (grown, never shrunk below what the last range needed), so highlighting allocates
/// nothing per character. Worker only.
/// </summary>
public sealed class PaintBuffer
{
    internal int[] Stack = new int[256];
    internal int[] Priority = new int[256];
    internal int[] Owner = new int[256];
    internal int[] Layer = new int[256];
    internal int[] Base = new int[256];

    public int Start { get; private set; }
    public int Length { get; private set; }

    /// <summary>The scope-stack id at document offset <paramref name="offset"/> (inside the painted range).</summary>
    public int StackAt(int offset) => Stack[offset - Start];

    internal void Reset(int start, int length, int baseStack)
    {
        Start = start;
        Length = length;
        if (Stack.Length < length)
        {
            var size = Math.Max(length, Stack.Length * 2);
            Stack = new int[size];
            Priority = new int[size];
            Owner = new int[size];
            Layer = new int[size];
            Base = new int[size];
        }
        Array.Fill(Stack, baseStack, 0, length);
        Array.Fill(Priority, int.MinValue, 0, length);
        Array.Fill(Owner, -1, 0, length);
        Array.Clear(Layer, 0, length);
        Array.Fill(Base, baseStack, 0, length);
    }

    /// <summary>Drops oversized arrays after an unusually large range (read-ahead of a huge region) so the buffer does not pin memory.</summary>
    internal void Shrink(int max)
    {
        if (Stack.Length <= max) return;
        Stack = new int[256];
        Priority = new int[256];
        Owner = new int[256];
        Layer = new int[256];
        Base = new int[256];
        Length = 0;
    }
}
