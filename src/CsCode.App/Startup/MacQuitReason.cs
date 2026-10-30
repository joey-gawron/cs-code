using System.Runtime.InteropServices;

namespace CsCode.App.Startup;

/// <summary>
/// Why macOS asks the app to quit. Avalonia's <c>ShutdownRequestedEventArgs.IsOSShutdown</c> is not public (and is never set on macOS), so the
/// quit Apple event the request comes from is asked instead: a log out, restart or shut down sends it with a reason (<c>kAEQuitReason</c>);
/// ⌘Q, the Dock's Quit and <c>osascript -e 'quit app "CsCode"'</c> send none, or no Apple event at all. Asked on the main thread while the
/// request is handled (<c>applicationShouldTerminate:</c>), when that event is the current one.
/// </summary>
internal static class MacQuitReason
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private const string Foundation = "/System/Library/Frameworks/Foundation.framework/Foundation";

    // AERegistry.h
    private static readonly uint QuitReasonKeyword = Code("why?");
    private static readonly uint[] SessionEnds = [Code("logo"), Code("rlgo"), Code("rrst"), Code("rest"), Code("rsdn"), Code("shut")];

    /// <summary>True when the quit being handled ends the desktop session (log out, restart, shut down); false for any other quit, and off macOS.</summary>
    public static bool IsSessionEnding()
    {
        if (!OperatingSystem.IsMacOS()) return false;
        try
        {
            NativeLibrary.Load(Foundation); // loaded by AppKit in the app already; a test process may not have it
            var manager = Send(objc_getClass("NSAppleEventManager"), sel_registerName("sharedAppleEventManager"));
            if (manager == 0) return false;
            var quit = Send(manager, sel_registerName("currentAppleEvent"));
            if (quit == 0) return false;
            var reason = Send(quit, sel_registerName("attributeDescriptorForKeyword:"), QuitReasonKeyword);
            return reason != 0 && SessionEnds.Contains(SendUInt32(reason, sel_registerName("enumCodeValue")));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>A four-character code (<c>OSType</c>) as its 32-bit value.</summary>
    internal static uint Code(string fourChars) => (uint)(fourChars[0] << 24 | fourChars[1] << 16 | fourChars[2] << 8 | fourChars[3]);

    [DllImport(ObjC)] private static extern nint objc_getClass([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(ObjC)] private static extern nint sel_registerName([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint Send(nint receiver, nint selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint Send(nint receiver, nint selector, uint argument);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern uint SendUInt32(nint receiver, nint selector);
}
