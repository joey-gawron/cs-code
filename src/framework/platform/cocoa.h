// C interface of the Cocoa backend (cocoa.m), used from cocoa.zig.
#ifndef CS_ZIG_COCOA_H
#define CS_ZIG_COCOA_H

#include <stddef.h>
#include <poll.h>

typedef struct JeWindow JeWindow;

enum {
    JE_EV_KEY_DOWN,
    JE_EV_KEY_UP,
    JE_EV_TEXT_INPUT,
    JE_EV_PREEDIT,
    JE_EV_MOUSE_DOWN,
    JE_EV_MOUSE_UP,
    JE_EV_MOUSE_MOVE,
    JE_EV_MOUSE_EXIT,
    JE_EV_SCROLL,
    JE_EV_RESIZE,
    JE_EV_FOCUS,
    JE_EV_CLOSE,
    JE_EV_COMMAND, // a menu bar item: text is the editor action's name
};

enum { JE_MOD_SHIFT = 1, JE_MOD_CTRL = 2, JE_MOD_ALT = 4, JE_MOD_SUPER = 8 };

/// Keys without a character, in platform.NamedKey's order (+1; 0 = a character key).
enum {
    JE_KEY_NONE,
    JE_KEY_ENTER,
    JE_KEY_TAB,
    JE_KEY_BACKSPACE,
    JE_KEY_DELETE,
    JE_KEY_ESCAPE,
    JE_KEY_LEFT,
    JE_KEY_RIGHT,
    JE_KEY_UP,
    JE_KEY_DOWN,
    JE_KEY_HOME,
    JE_KEY_END,
    JE_KEY_PAGE_UP,
    JE_KEY_PAGE_DOWN,
    JE_KEY_INSERT,
    JE_KEY_F1,
    JE_KEY_F12 = JE_KEY_F1 + 11,
    JE_KEY_CONTEXT_MENU,
};

/// One window event. Strings are UTF-8 and stay valid until the next je_poll.
typedef struct {
    int kind;
    unsigned mods;
    // Keys: a named key, or else the unshifted character.
    int named;
    unsigned codepoint;
    int repeat;
    const char *text;
    size_t text_len;
    // Preedit cursor (byte offsets into text), -1 if none.
    int cursor_begin, cursor_end;
    // Mouse and scroll, in logical pixels from the top left.
    double x, y, dx, dy;
    int precise; // scroll in pixels (touchpad) rather than lines
    int button; // 0 left, 1 right, 2 middle, 3 back, 4 forward
    int pressed; // button held during a move, -1 if none
    int click_count;
    // Resize: logical size and backing scale.
    double width, height, scale;
    int focused;
} JeEvent;

JeWindow *je_window_create(const char *title, int width, int height);
void je_window_destroy(JeWindow *w);
void je_window_size(JeWindow *w, double *width, double *height, double *scale);

/// Waits up to timeout_ms (-1: forever) for window events or for one of
/// `fds` to become ready; sets their revents. Then je_events() lists the
/// window's events.
int je_poll(JeWindow *w, int timeout_ms, struct pollfd *fds, int nfds);
size_t je_event_count(JeWindow *w);
const JeEvent *je_events(JeWindow *w);

int je_can_render(JeWindow *w);
void je_present(JeWindow *w);
void je_set_title(JeWindow *w, const char *title);
/// The Dock icon: `size`×`size` straight-alpha RGBA.
void je_set_app_icon(const unsigned char *rgba, int size);
/// Decodes a PNG, JPEG, GIF, TIFF, BMP or other image AppKit reads to
/// straight-alpha RGBA, scaled down to `max_width` pixels wide if it is wider
/// (0: no limit). `*out_rgba` is malloc'd (free it); returns 0 on failure.
int je_image_decode(const unsigned char *data, size_t len, int max_width, int *out_w, int *out_h, unsigned char **out_rgba);
/// platform.CursorShape's order.
void je_set_cursor(JeWindow *w, int shape);
void je_toggle_fullscreen(JeWindow *w);
void je_set_text_input_rect(JeWindow *w, double x, double y, double width, double height, int enabled);

void je_set_clipboard(const char *text, size_t len);
/// malloc'd UTF-8 (free with free()), or NULL when the clipboard holds no text.
char *je_get_clipboard(size_t *len);

/// The system's choose-a-folder panel (blocks until it closes), starting in
/// `start` when that is not empty. malloc'd absolute path (free with free()),
/// or NULL when the user cancelled.
char *je_pick_folder(const char *start, const char *title, size_t *len);

/// Files Finder or the Dock asked the app to open since the last call
/// (Open With, a drop on the Dock icon), one absolute path per line.
/// malloc'd (free with free()), or NULL when there are none.
char *je_take_opened_paths(size_t *len);

/// A process's current directory into buf (NUL-terminated); 0 on success.
int je_process_cwd(int pid, char *buf, size_t len);

/// OpenGL entry points for gl.load.
const void *je_gl_proc_address(const char *name);

/// An offscreen OpenGL 3.3+ core context, made current (headless rendering).
void *je_headless_context_create(void);
void je_headless_context_destroy(void *ctx);

/// File watching with FSEvents: changes to files directly inside the watched
/// folders. `fd` becomes readable when there are changes to read.
typedef struct JeWatcher JeWatcher;
JeWatcher *je_watcher_create(void);
void je_watcher_destroy(JeWatcher *w);
int je_watcher_fd(JeWatcher *w);
void je_watcher_set_dirs(JeWatcher *w, const char *const *dirs, const size_t *lens, size_t count);
/// Changed paths since the last call, malloc'd (free with free()): records of
/// one byte ('c' changed, 'd' deleted), the path, and a NUL. NULL if none.
char *je_watcher_read(JeWatcher *w, size_t *len);

#endif
