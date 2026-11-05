// X11 APIs for the X11 backend (src/framework/platform/x11.zig), translated
// into the Zig `cx11` module separately from `c` because Xlib's macros
// (None, Bool, Status, ...) would collide with other headers.
#define _GNU_SOURCE
#ifdef _FORTIFY_SOURCE
#undef _FORTIFY_SOURCE
#endif
#include <X11/Xlib.h>
#include <X11/Xutil.h>
#include <X11/Xatom.h>
#include <X11/Xresource.h>
#include <X11/XKBlib.h>
#include <X11/Xcursor/Xcursor.h>
#include <X11/extensions/Xrandr.h>
#include <X11/extensions/XInput2.h>
#include <X11/Xlib-xcb.h>
#include <xkbcommon/xkbcommon-x11.h>
#include <locale.h>
