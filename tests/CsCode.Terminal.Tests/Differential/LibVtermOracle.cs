using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CsCode.Terminal.Emulator;

namespace CsCode.Terminal.Tests.Differential;

/// <summary>
/// libvterm (Neovim's 0.3 build) in-process as a cell-exact reference, through function pointers from <see cref="NativeLibrary"/>. The cell
/// layout was checked with a C program compiled against the 0.3 declarations (40 bytes: chars[6] at 0, width at 24, the attribute bit field
/// at 28 — 18 bits — and fg/bg colours at 32/36). The library is found through <c>CSCODE_LIBVTERM</c> or in the Nix store
/// (<c>*-libvterm-neovim-*</c>); without it the tests that need it skip with that reason.
/// </summary>
public sealed unsafe class LibVtermOracle : IOracle
{
    [StructLayout(LayoutKind.Sequential)]
    private struct VTermPos
    {
        public int Row;
        public int Col;
    }

    [StructLayout(LayoutKind.Sequential, Size = 40)]
    private struct VTermScreenCell
    {
        public fixed uint Chars[6];
        public sbyte Width;
        private byte _pad0, _pad1, _pad2;
        public uint Attrs;
        public uint Fg;
        public uint Bg;
    }

    // VTermScreenCallbacks (0.3): damage, moverect, movecursor, settermprop, bell, resize, sb_pushline, sb_popline, sb_clear
    [StructLayout(LayoutKind.Sequential)]
    private struct Callbacks
    {
        public nint Damage, MoveRect, MoveCursor, SetTermProp, Bell, Resize, SbPushLine, SbPopLine, SbClear;
    }

    private static readonly Lazy<nint> Handle = new(Load);

    private readonly delegate* unmanaged<int, int, nint> _new;
    private readonly delegate* unmanaged<nint, void> _free;
    private readonly delegate* unmanaged<nint, int, void> _setUtf8;
    private readonly delegate* unmanaged<nint, byte*, nuint, nuint> _inputWrite;
    private readonly delegate* unmanaged<nint, nint> _obtainScreen;
    private readonly delegate* unmanaged<nint, int, void> _screenReset;
    private readonly delegate* unmanaged<nint, int, void> _enableAltscreen;
    private readonly delegate* unmanaged<nint, VTermPos, VTermScreenCell*, int> _getCell;
    private readonly delegate* unmanaged<nint, nint> _obtainState;
    private readonly delegate* unmanaged<nint, VTermPos*, void> _getCursor;
    private readonly delegate* unmanaged<nint, Callbacks*, void*, void> _setCallbacks;
    private readonly Callbacks* _callbacks;

    [ThreadStatic] private static bool _altscreen;

    public LibVtermOracle()
    {
        var h = Handle.Value;
        if (h == 0) throw new InvalidOperationException("libvterm not found");
        _new = (delegate* unmanaged<int, int, nint>)NativeLibrary.GetExport(h, "vterm_new");
        _free = (delegate* unmanaged<nint, void>)NativeLibrary.GetExport(h, "vterm_free");
        _setUtf8 = (delegate* unmanaged<nint, int, void>)NativeLibrary.GetExport(h, "vterm_set_utf8");
        _inputWrite = (delegate* unmanaged<nint, byte*, nuint, nuint>)NativeLibrary.GetExport(h, "vterm_input_write");
        _obtainScreen = (delegate* unmanaged<nint, nint>)NativeLibrary.GetExport(h, "vterm_obtain_screen");
        _screenReset = (delegate* unmanaged<nint, int, void>)NativeLibrary.GetExport(h, "vterm_screen_reset");
        _enableAltscreen = (delegate* unmanaged<nint, int, void>)NativeLibrary.GetExport(h, "vterm_screen_enable_altscreen");
        _getCell = (delegate* unmanaged<nint, VTermPos, VTermScreenCell*, int>)NativeLibrary.GetExport(h, "vterm_screen_get_cell");
        _obtainState = (delegate* unmanaged<nint, nint>)NativeLibrary.GetExport(h, "vterm_obtain_state");
        _getCursor = (delegate* unmanaged<nint, VTermPos*, void>)NativeLibrary.GetExport(h, "vterm_state_get_cursorpos");
        _setCallbacks = (delegate* unmanaged<nint, Callbacks*, void*, void>)NativeLibrary.GetExport(h, "vterm_screen_set_callbacks");
        _callbacks = (Callbacks*)NativeMemory.AllocZeroed((nuint)sizeof(Callbacks));
        _callbacks->SetTermProp = (nint)(delegate* unmanaged<int, int*, void*, int>)&OnSetTermProp;
    }

    public static string? LibraryPath
    {
        get
        {
            if (Environment.GetEnvironmentVariable("CSCODE_LIBVTERM") is { Length: > 0 } custom && File.Exists(custom)) return custom;
            const string known = "/nix/store/dwmpmvkfxlbca95d878hbgfnhl73lmp8-libvterm-neovim-0.3.3/lib/libvterm.so";
            if (File.Exists(known)) return known;
            try
            {
                foreach (var dir in Directory.EnumerateDirectories("/nix/store", "*-libvterm-neovim-*"))
                    if (File.Exists(Path.Combine(dir, "lib", "libvterm.so"))) return Path.Combine(dir, "lib", "libvterm.so");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            return null;
        }
    }

    public static bool IsAvailable => Handle.Value != 0;

    private static nint Load() => LibraryPath is { } path && NativeLibrary.TryLoad(path, out var h) ? h : 0;

    public string Name => "libvterm";

    // libvterm 0.3 has no faint (SGR 2) and no overline; underline styles single/double/curly.
    public Compared Capabilities => Compared.Text | Compared.Colors | Compared.Bold | Compared.Italic | Compared.Underline | Compared.UnderlineStyle
        | Compared.Inverse | Compared.Strike | Compared.Blink | Compared.Invisible | Compared.Cursor | Compared.AlternateScreen;

    [UnmanagedCallersOnly]
    private static int OnSetTermProp(int prop, int* value, void* user)
    {
        if (prop == 3) _altscreen = *value != 0; // VTERM_PROP_ALTSCREEN
        return 1;
    }

    public ScreenState Run(byte[] input, int columns, int rows)
    {
        var vt = _new(rows, columns);
        try
        {
            _altscreen = false;
            _setUtf8(vt, 1);
            var screen = _obtainScreen(vt);
            _setCallbacks(screen, _callbacks, null);
            _enableAltscreen(screen, 1);
            _screenReset(screen, 1);
            fixed (byte* p = input) _inputWrite(vt, p, (nuint)input.Length);
            var state = new ScreenState(columns, rows);
            VTermScreenCell cell;
            for (var r = 0; r < rows; r++)
            for (var c = 0; c < columns; c++)
            {
                _getCell(screen, new VTermPos { Row = r, Col = c }, &cell);
                state.Cells[r, c] = Convert(cell);
            }
            VTermPos cursor;
            _getCursor(_obtainState(vt), &cursor);
            state.CursorX = cursor.Col;
            state.CursorY = cursor.Row;
            state.AlternateScreen = _altscreen;
            return state;
        }
        finally
        {
            _free(vt);
        }
    }

    private static RefCell Convert(VTermScreenCell cell)
    {
        if (cell.Chars[0] == 0xFFFFFFFF) return new RefCell("", true, 0, 0, CellFlags.None, UnderlineStyle.None);
        var text = new System.Text.StringBuilder();
        for (var i = 0; i < 6 && cell.Chars[i] != 0; i++)
            text.Append(cell.Chars[i] <= 0x10FFFF ? char.ConvertFromUtf32((int)cell.Chars[i]) : "�");
        var a = cell.Attrs & 0x3FFFF;
        var flags = CellFlags.None;
        if ((a & 1) != 0) flags |= CellFlags.Bold;
        if ((a & (1 << 3)) != 0) flags |= CellFlags.Italic;
        if ((a & (1 << 4)) != 0) flags |= CellFlags.Blink;
        if ((a & (1 << 5)) != 0) flags |= CellFlags.Inverse;
        if ((a & (1 << 6)) != 0) flags |= CellFlags.Invisible;
        if ((a & (1 << 7)) != 0) flags |= CellFlags.Strikethrough;
        var ul = (a >> 1) & 3;
        var underline = ul switch { 1 => UnderlineStyle.Single, 2 => UnderlineStyle.Double, 3 => UnderlineStyle.Curly, _ => UnderlineStyle.None };
        return new RefCell(text.Length == 0 ? " " : text.ToString(), false, Color(cell.Fg, defaultFlag: 0x02), Color(cell.Bg, defaultFlag: 0x04), flags, underline);
    }

    private static uint Color(uint raw, int defaultFlag)
    {
        var type = raw & 0xff;
        if ((type & defaultFlag) != 0) return TermColor.Default;
        if ((type & 0x01) != 0) return TermColor.Indexed((int)((raw >> 8) & 0xff));
        return TermColor.Rgb((int)((raw >> 8) & 0xff), (int)((raw >> 16) & 0xff), (int)((raw >> 24) & 0xff));
    }

    public void Dispose() => NativeMemory.Free(_callbacks);
}
