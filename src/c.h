// C APIs consumed by the editor. Translated to the Zig `c` module by build.zig.
#define _GNU_SOURCE
// glibc's fortified wrappers (enabled by zig in release builds) use
// builtins translate-c can't express; we only need the declarations.
#ifdef _FORTIFY_SOURCE
#undef _FORTIFY_SOURCE
#endif
#ifdef __APPLE__
// The Cocoa window backend's C interface (cocoa.m).
#include "cocoa.h"
#else
#include <wayland-client.h>
#include <wayland-egl.h>
#include "xdg-shell-client-protocol.h"
#include "xdg-decoration-unstable-v1-client-protocol.h"
#include "viewporter-client-protocol.h"
#include "fractional-scale-v1-client-protocol.h"
#include "cursor-shape-v1-client-protocol.h"
#include "text-input-unstable-v3-client-protocol.h"
#include "primary-selection-unstable-v1-client-protocol.h"
// Optional: build.zig defines this when wayland-protocols has the protocol.
#ifdef CS_ZIG_TOPLEVEL_ICON
#include "xdg-toplevel-icon-v1-client-protocol.h"
#endif

#include <EGL/egl.h>
#include <EGL/eglext.h>

#include <xkbcommon/xkbcommon.h>
#include <xkbcommon/xkbcommon-compose.h>
#endif

#include <ft2build.h>
#include FT_FREETYPE_H
#include FT_OUTLINE_H
#include FT_LCD_FILTER_H
#include <hb.h>
#include <hb-ft.h>
#include <fontconfig/fontconfig.h>

#include <tree_sitter/api.h>

#include <poll.h>
#ifndef __APPLE__
#include <sys/eventfd.h>
#include <sys/timerfd.h>
#include <sys/inotify.h>
#endif
#include <unistd.h>
#include <fcntl.h>
#include <dlfcn.h>
#include <locale.h>
#include <time.h>
#include <sys/socket.h>
#include <sys/un.h>
#include <signal.h>

// Built-in terminal: pseudo-terminals and libvterm.
#include <stdlib.h>
#include <sys/ioctl.h>
#include <sys/wait.h>
#include <termios.h>
#include <vterm.h>
