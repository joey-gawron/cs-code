using System.Text;
using CsCode.Terminal.Emulator;

namespace CsCode.Panels.Terminal.Input;

public enum MouseAction { Press, Release, Move, WheelUp, WheelDown }

/// <summary>
/// Mouse reports as xterm sends them, for the tracking mode the program asked for (DECSET 9, 1000, 1002, 1003) in its encoding (default
/// <c>CSI M Cb Cx Cy</c>, UTF-8 1005, SGR 1006 <c>CSI &lt; b;x;y M/m</c>, urxvt 1015). Buttons 0 left, 1 middle, 2 right; release is 3 except
/// in SGR; motion adds 32; wheel up/down are 64/65; Shift +4, Alt +8, Ctrl +16 (not in X10 mode).
/// </summary>
public static class MouseEncoder
{
    /// <summary>The report, or null when the program's mode does not want this event (or the position cannot be encoded).</summary>
    public static byte[]? Encode(TerminalModes modes, MouseAction action, int button, int column, int row, bool shift, bool alt, bool ctrl, bool buttonHeld)
    {
        var mode = modes.Mouse;
        if (mode == MouseTracking.None) return null;
        switch (action)
        {
            case MouseAction.Move when mode is MouseTracking.X10 or MouseTracking.Normal:
            case MouseAction.Move when mode == MouseTracking.ButtonEvent && !buttonHeld:
            case MouseAction.Release or MouseAction.WheelUp or MouseAction.WheelDown when mode == MouseTracking.X10:
                return null;
        }
        var sgr = modes.MouseEncoding == MouseEncoding.Sgr;
        int cb = action switch
        {
            MouseAction.WheelUp => 64,
            MouseAction.WheelDown => 65,
            MouseAction.Release when !sgr => 3,
            MouseAction.Move => 32 + (buttonHeld ? Math.Clamp(button, 0, 2) : 3),
            _ => Math.Clamp(button, 0, 2),
        };
        if (mode != MouseTracking.X10) cb += (shift ? 4 : 0) + (alt ? 8 : 0) + (ctrl ? 16 : 0);
        var x = column + 1;
        var y = row + 1;
        switch (modes.MouseEncoding)
        {
            case MouseEncoding.Sgr:
                return Encoding.ASCII.GetBytes($"\e[<{cb};{x};{y}{(action == MouseAction.Release ? 'm' : 'M')}");
            case MouseEncoding.Urxvt:
                return Encoding.ASCII.GetBytes($"\e[{32 + cb};{x};{y}M");
            case MouseEncoding.Utf8:
                if (x > 2015 || y > 2015) return null;
                return Encoding.UTF8.GetBytes($"\e[M{(char)(32 + cb)}{(char)(32 + x)}{(char)(32 + y)}");
            default:
                if (x > 223 || y > 223) return null; // the classic encoding cannot say it
                return [0x1b, (byte)'[', (byte)'M', (byte)(32 + cb), (byte)(32 + x), (byte)(32 + y)];
        }
    }
}
