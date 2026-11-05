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
// Linux backends (not built by this spike's build.zig yet).
#include <wayland-client.h>
#include <wayland-egl.h>
#include "xdg-shell-client-protocol.h"
#include "xdg-decoration-unstable-v1-client-protocol.h"
#include "viewporter-client-protocol.h"
#include "fractional-scale-v1-client-protocol.h"
#include "cursor-shape-v1-client-protocol.h"
#include "text-input-unstable-v3-client-protocol.h"
#include "primary-selection-unstable-v1-client-protocol.h"
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

#include <poll.h>
#include <unistd.h>
#include <stdlib.h>
#include <time.h>
#include <signal.h>
#include <sys/resource.h>
