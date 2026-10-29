using System.Runtime.InteropServices;

namespace CsCode.App.Services.X11;

/// <summary>
/// The few libxcb calls the selection client needs. XCB rather than Xlib: XCB is thread safe and reports protocol errors as events instead of
/// through Xlib's process-wide error handler (which Avalonia's X11 backend owns and whose default implementation exits the process).
/// </summary>
internal static class Xcb
{
    private const string Lib = "libxcb.so.1";

    public const uint CurrentTime = 0;
    public const uint None = 0;
    public const uint AnyPropertyType = 0;
    public const uint AtomAtom = 4;
    public const uint AtomInteger = 19;
    public const uint AtomString = 31;
    public const byte PropModeReplace = 0;
    public const byte PropModeAppend = 2;
    public const ushort WindowClassInputOnly = 2;
    public const uint CwEventMask = 0x800;
    public const uint EventMaskPropertyChange = 0x400000;

    public const byte PropertyNotify = 28;
    public const byte SelectionClear = 29;
    public const byte SelectionRequest = 30;
    public const byte SelectionNotify = 31;
    public const byte ClientMessage = 33;
    public const byte PropertyNewValue = 0;
    public const byte PropertyDelete = 1;

    [StructLayout(LayoutKind.Sequential)]
    public struct ScreenIterator
    {
        public IntPtr Data;
        public int Rem;
        public int Index;
    }

    [DllImport(Lib)] public static extern IntPtr xcb_connect(string? displayName, out int screen);
    [DllImport(Lib)] public static extern int xcb_connection_has_error(IntPtr c);
    [DllImport(Lib)] public static extern void xcb_disconnect(IntPtr c);
    [DllImport(Lib)] public static extern IntPtr xcb_get_setup(IntPtr c);
    [DllImport(Lib)] public static extern ScreenIterator xcb_setup_roots_iterator(IntPtr setup);
    [DllImport(Lib)] public static extern uint xcb_generate_id(IntPtr c);
    [DllImport(Lib)] public static extern uint xcb_get_maximum_request_length(IntPtr c);
    [DllImport(Lib)] public static extern int xcb_flush(IntPtr c);

    [DllImport(Lib)]
    public static extern uint xcb_create_window(IntPtr c, byte depth, uint wid, uint parent, short x, short y, ushort width, ushort height,
        ushort borderWidth, ushort windowClass, uint visual, uint valueMask, uint[] valueList);

    [DllImport(Lib)] public static extern uint xcb_destroy_window(IntPtr c, uint window);
    [DllImport(Lib)] public static extern uint xcb_change_window_attributes(IntPtr c, uint window, uint valueMask, uint[] valueList);
    [DllImport(Lib)] public static extern uint xcb_intern_atom(IntPtr c, byte onlyIfExists, ushort nameLength, byte[] name);
    [DllImport(Lib)] public static extern IntPtr xcb_intern_atom_reply(IntPtr c, uint cookie, IntPtr error);
    [DllImport(Lib)] public static extern uint xcb_set_selection_owner(IntPtr c, uint owner, uint selection, uint time);
    [DllImport(Lib)] public static extern uint xcb_get_selection_owner(IntPtr c, uint selection);
    [DllImport(Lib)] public static extern IntPtr xcb_get_selection_owner_reply(IntPtr c, uint cookie, IntPtr error);
    [DllImport(Lib)] public static extern uint xcb_convert_selection(IntPtr c, uint requestor, uint selection, uint target, uint property, uint time);
    [DllImport(Lib)] public static extern uint xcb_change_property(IntPtr c, byte mode, uint window, uint property, uint type, byte format, uint dataLength, byte[] data);
    [DllImport(Lib)] public static extern uint xcb_delete_property(IntPtr c, uint window, uint property);
    [DllImport(Lib)] public static extern uint xcb_get_property(IntPtr c, byte delete, uint window, uint property, uint type, uint longOffset, uint longLength);
    [DllImport(Lib)] public static extern IntPtr xcb_get_property_reply(IntPtr c, uint cookie, IntPtr error);
    [DllImport(Lib)] public static extern IntPtr xcb_get_property_value(IntPtr reply);
    [DllImport(Lib)] public static extern int xcb_get_property_value_length(IntPtr reply);
    [DllImport(Lib)] public static extern uint xcb_send_event(IntPtr c, byte propagate, uint destination, uint eventMask, byte[] evt);
    [DllImport(Lib)] public static extern IntPtr xcb_poll_for_event(IntPtr c);
    [DllImport(Lib)] public static extern int xcb_get_file_descriptor(IntPtr c);

    [DllImport("libc", EntryPoint = "free")] public static extern void Free(IntPtr ptr);

    public static uint ReadU32(IntPtr p, int offset) => unchecked((uint)Marshal.ReadInt32(p, offset));

    public static void WriteU32(byte[] buffer, int offset, uint value) => BitConverter.TryWriteBytes(buffer.AsSpan(offset, 4), value);

    public static uint U32(byte[] buffer, int offset) => BitConverter.ToUInt32(buffer, offset);
}
