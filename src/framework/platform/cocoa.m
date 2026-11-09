// macOS window backend: an NSWindow whose content view renders through an
// NSOpenGLContext (OpenGL 4.1 core, which runs the renderer's 3.3 shaders),
// with keyboard input through NSTextInputClient so input methods and dead
// keys work. cocoa.zig turns the events queued here into platform.Events.
//
// The editor runs its own loop rather than [NSApp run]: je_poll pumps the
// Cocoa event queue and, while it waits, a helper thread polls the editor's
// file descriptors (wake-ups, sockets, terminals) and posts an event to end
// the wait when one becomes ready.

#import <Cocoa/Cocoa.h>
#import <OpenGL/OpenGL.h>
#import <OpenGL/gl3.h>
#include <dlfcn.h>
#include <libproc.h>
#include <pthread.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

#include "cocoa.h"

// ---- Event queue -----------------------------------------------------------

struct JeWindow;
static void push(struct JeWindow *w, JeEvent ev, NSData *text);

static unsigned modsOf(NSEventModifierFlags flags) {
    unsigned m = 0;
    if (flags & NSEventModifierFlagShift) m |= JE_MOD_SHIFT;
    if (flags & NSEventModifierFlagControl) m |= JE_MOD_CTRL;
    if (flags & NSEventModifierFlagOption) m |= JE_MOD_ALT;
    if (flags & NSEventModifierFlagCommand) m |= JE_MOD_SUPER;
    return m;
}

static int namedKey(unsigned short code) {
    switch (code) {
    case 0x24: case 0x4C: return JE_KEY_ENTER; // return, keypad enter
    case 0x30: return JE_KEY_TAB;
    case 0x33: return JE_KEY_BACKSPACE;
    case 0x75: return JE_KEY_DELETE;
    case 0x35: return JE_KEY_ESCAPE;
    case 0x7B: return JE_KEY_LEFT;
    case 0x7C: return JE_KEY_RIGHT;
    case 0x7E: return JE_KEY_UP;
    case 0x7D: return JE_KEY_DOWN;
    case 0x73: return JE_KEY_HOME;
    case 0x77: return JE_KEY_END;
    case 0x74: return JE_KEY_PAGE_UP;
    case 0x79: return JE_KEY_PAGE_DOWN;
    case 0x72: return JE_KEY_INSERT; // "help" on Apple keyboards, insert on PC ones
    case 0x6E: return JE_KEY_CONTEXT_MENU;
    case 0x7A: return JE_KEY_F1;
    case 0x78: return JE_KEY_F1 + 1;
    case 0x63: return JE_KEY_F1 + 2;
    case 0x76: return JE_KEY_F1 + 3;
    case 0x60: return JE_KEY_F1 + 4;
    case 0x61: return JE_KEY_F1 + 5;
    case 0x62: return JE_KEY_F1 + 6;
    case 0x64: return JE_KEY_F1 + 7;
    case 0x65: return JE_KEY_F1 + 8;
    case 0x6D: return JE_KEY_F1 + 9;
    case 0x67: return JE_KEY_F1 + 10;
    case 0x6F: return JE_KEY_F1 + 11;
    default: return JE_KEY_NONE;
    }
}

static unsigned firstCodepoint(NSString *s) {
    if (s.length == 0) return 0;
    unichar hi = [s characterAtIndex:0];
    if (hi >= 0xD800 && hi < 0xDC00 && s.length > 1) {
        unichar lo = [s characterAtIndex:1];
        return 0x10000 + ((hi - 0xD800) << 10) + (lo - 0xDC00);
    }
    return hi;
}

static NSData *utf8(NSString *s) {
    return [s dataUsingEncoding:NSUTF8StringEncoding] ?: [NSData data];
}

/// UTF-8 byte offset of UTF-16 index `i` in `s`.
static int byteOffset(NSString *s, NSUInteger i) {
    if (i > s.length) i = s.length;
    return (int)[[s substringToIndex:i] lengthOfBytesUsingEncoding:NSUTF8StringEncoding];
}

// ---- View --------------------------------------------------------------------

@interface JeView : NSView <NSTextInputClient>
@property(nonatomic) struct JeWindow *owner;
@property(nonatomic, strong) NSOpenGLContext *gl;
@property(nonatomic, strong) NSMutableAttributedString *marked;
@property(nonatomic) NSRange markedSelection;
@property(nonatomic) NSRect caretRect;
@property(nonatomic) BOOL textInputEnabled;
@property(nonatomic) BOOL inKeyDown;
@property(nonatomic, strong) NSMutableString *keyText;
@property(nonatomic, strong) NSCursor *cursor;
@property(nonatomic) int pressedButton;
@end

@interface JeWindowDelegate : NSObject <NSWindowDelegate>
@property(nonatomic) struct JeWindow *owner;
@end

struct JeWindow {
    NSWindow *window;
    JeView *view;
    JeWindowDelegate *delegate;
    JeEvent *events;
    size_t count, cap;
    /// Keeps each event's text alive (NSData or NSNull), index for index.
    NSMutableArray *keep;
    /// Events handed out by the last je_poll, dropped by the next one.
    size_t returned;
};

static void push(struct JeWindow *w, JeEvent ev, NSData *text) {
    if (w->count == w->cap) {
        w->cap = w->cap ? w->cap * 2 : 64;
        w->events = realloc(w->events, w->cap * sizeof(JeEvent));
    }
    if (text) {
        ev.text = text.bytes;
        ev.text_len = text.length;
    }
    w->events[w->count++] = ev;
    [w->keep addObject:text ?: (id)[NSNull null]];
}

static void pushResize(struct JeWindow *w) {
    NSSize size = w->view.bounds.size;
    JeEvent ev = {.kind = JE_EV_RESIZE, .width = size.width, .height = size.height, .scale = w->window.backingScaleFactor};
    push(w, ev, nil);
}

@implementation JeView

- (BOOL)isFlipped { return YES; }
- (BOOL)acceptsFirstResponder { return YES; }
- (BOOL)acceptsFirstMouse:(NSEvent *)e { return YES; }
- (BOOL)isOpaque { return YES; }

- (void)updateTrackingAreas {
    for (NSTrackingArea *a in self.trackingAreas) [self removeTrackingArea:a];
    NSTrackingAreaOptions opts = NSTrackingMouseMoved | NSTrackingMouseEnteredAndExited | NSTrackingActiveAlways |
                                 NSTrackingInVisibleRect | NSTrackingCursorUpdate;
    [self addTrackingArea:[[NSTrackingArea alloc] initWithRect:NSZeroRect options:opts owner:self userInfo:nil]];
    [super updateTrackingAreas];
}

- (void)setFrameSize:(NSSize)size {
    [super setFrameSize:size];
    [self.gl update];
    if (self.owner) pushResize(self.owner);
}

- (void)viewDidChangeBackingProperties {
    [super viewDidChangeBackingProperties];
    [self.gl update];
    if (self.owner) pushResize(self.owner);
}

- (void)cursorUpdate:(NSEvent *)e { [self.cursor set]; }
- (void)resetCursorRects { [self addCursorRect:self.bounds cursor:self.cursor]; }

// -- Mouse

- (JeEvent)mouseEvent:(NSEvent *)e kind:(int)kind {
    NSPoint p = [self convertPoint:e.locationInWindow fromView:nil];
    JeEvent ev = {.kind = kind, .mods = modsOf(e.modifierFlags), .x = p.x, .y = p.y, .pressed = -1};
    return ev;
}

static int buttonOf(NSEvent *e) {
    switch (e.type) {
    case NSEventTypeLeftMouseDown: case NSEventTypeLeftMouseUp: case NSEventTypeLeftMouseDragged: return 0;
    case NSEventTypeRightMouseDown: case NSEventTypeRightMouseUp: case NSEventTypeRightMouseDragged: return 1;
    default:
        switch (e.buttonNumber) {
        case 3: return 3;
        case 4: return 4;
        default: return 2;
        }
    }
}

- (void)press:(NSEvent *)e down:(BOOL)down {
    JeEvent ev = [self mouseEvent:e kind:down ? JE_EV_MOUSE_DOWN : JE_EV_MOUSE_UP];
    ev.button = buttonOf(e);
    // Control-click is the Mac's right click. The press, its drags and its
    // release all report the right button, without Control.
    BOOL ctrl_click = ev.button == 0 && (down ? (ev.mods & JE_MOD_CTRL) && !(ev.mods & JE_MOD_SUPER) : self.pressedButton == 1);
    if (ctrl_click) {
        ev.button = 1;
        ev.mods &= ~JE_MOD_CTRL;
    }
    ev.click_count = (int)MAX(1, e.clickCount);
    self.pressedButton = down ? ev.button : -1;
    push(self.owner, ev, nil);
}

- (void)move:(NSEvent *)e {
    JeEvent ev = [self mouseEvent:e kind:JE_EV_MOUSE_MOVE];
    ev.pressed = self.pressedButton;
    if (ev.pressed == 1) ev.mods &= ~JE_MOD_CTRL;
    push(self.owner, ev, nil);
}

- (void)mouseDown:(NSEvent *)e { [self press:e down:YES]; }
- (void)mouseUp:(NSEvent *)e { [self press:e down:NO]; }
- (void)rightMouseDown:(NSEvent *)e { [self press:e down:YES]; }
- (void)rightMouseUp:(NSEvent *)e { [self press:e down:NO]; }
- (void)otherMouseDown:(NSEvent *)e { [self press:e down:YES]; }
- (void)otherMouseUp:(NSEvent *)e { [self press:e down:NO]; }
- (void)mouseMoved:(NSEvent *)e { [self move:e]; }
- (void)mouseDragged:(NSEvent *)e { [self move:e]; }
- (void)rightMouseDragged:(NSEvent *)e { [self move:e]; }
- (void)otherMouseDragged:(NSEvent *)e { [self move:e]; }
- (void)mouseEntered:(NSEvent *)e { [self move:e]; }

- (void)mouseExited:(NSEvent *)e {
    JeEvent ev = {.kind = JE_EV_MOUSE_EXIT};
    push(self.owner, ev, nil);
}

- (void)scrollWheel:(NSEvent *)e {
    JeEvent ev = [self mouseEvent:e kind:JE_EV_SCROLL];
    ev.precise = e.hasPreciseScrollingDeltas;
    // Cocoa's deltas are positive when content should move down; ours are
    // positive when the viewport moves down.
    ev.dx = -e.scrollingDeltaX;
    ev.dy = -e.scrollingDeltaY;
    if (ev.dx == 0 && ev.dy == 0) return;
    push(self.owner, ev, nil);
}

// -- Keys

- (JeEvent)keyEvent:(NSEvent *)e kind:(int)kind {
    JeEvent ev = {.kind = kind, .mods = modsOf(e.modifierFlags), .repeat = e.ARepeat, .cursor_begin = -1, .cursor_end = -1};
    ev.named = namedKey(e.keyCode);
    if (ev.named == JE_KEY_NONE) {
        // The key's character without modifiers, so "ctrl+shift+p" matches
        // whatever shift would produce.
        NSString *base = nil;
        if (@available(macOS 10.15, *)) base = [e charactersByApplyingModifiers:0];
        if (base.length == 0) base = e.charactersIgnoringModifiers;
        ev.codepoint = firstCodepoint(base.lowercaseString);
    }
    return ev;
}

/// The text a key types when it isn't going through the input method.
static NSData *plainText(NSEvent *e, unsigned mods) {
    if (mods & (JE_MOD_CTRL | JE_MOD_SUPER)) return nil;
    NSString *s = e.characters;
    unsigned cp = firstCodepoint(s);
    if (cp < 0x20 || cp == 0x7f || (cp >= 0xF700 && cp <= 0xF8FF)) return nil;
    return utf8(s);
}

- (void)keyDown:(NSEvent *)e {
    JeEvent ev = [self keyEvent:e kind:JE_EV_KEY_DOWN];
    if (ev.named == JE_KEY_NONE && (ev.codepoint == 0 || (ev.codepoint >= 0xF700 && ev.codepoint <= 0xF8FF))) return;
    BOOL shortcut = (ev.mods & (JE_MOD_CTRL | JE_MOD_SUPER)) != 0;
    if (!self.textInputEnabled || (shortcut && !self.hasMarkedText)) {
        push(self.owner, ev, plainText(e, ev.mods));
        return;
    }
    // The input method decides: insertText: (typed text, kept for this key
    // event), setMarkedText: (a composition) or doCommandBySelector: (a key
    // with no text).
    BOOL composing = self.hasMarkedText;
    self.inKeyDown = YES;
    self.keyText = nil;
    [self interpretKeyEvents:@[ e ]];
    self.inKeyDown = NO;
    if (self.keyText) {
        push(self.owner, ev, utf8(self.keyText));
    } else if (!composing && !self.hasMarkedText) {
        push(self.owner, ev, nil);
    }
    self.keyText = nil;
}

- (void)keyUp:(NSEvent *)e {
    JeEvent ev = [self keyEvent:e kind:JE_EV_KEY_UP];
    if (ev.named == JE_KEY_NONE && ev.codepoint == 0) return;
    push(self.owner, ev, nil);
}

/// Shortcuts reach the editor before menus and Cocoa's own key handling,
/// except the few the application menu keeps (quit, hide, minimize).
- (BOOL)performKeyEquivalent:(NSEvent *)e {
    if (e.type != NSEventTypeKeyDown || self.window.firstResponder != self) return NO;
    NSEventModifierFlags flags = e.modifierFlags & NSEventModifierFlagDeviceIndependentFlagsMask;
    if (!(flags & (NSEventModifierFlagCommand | NSEventModifierFlagControl))) return NO;
    if (flags & NSEventModifierFlagCommand) {
        NSString *ch = e.charactersIgnoringModifiers.lowercaseString;
        if ([ch isEqualToString:@"q"] || [ch isEqualToString:@"h"] || [ch isEqualToString:@"m"]) return NO;
    }
    [self keyDown:e];
    return YES;
}

- (void)flagsChanged:(NSEvent *)e {}

// -- NSTextInputClient

- (BOOL)hasMarkedText { return self.marked.length > 0; }
- (NSRange)markedRange { return self.hasMarkedText ? NSMakeRange(0, self.marked.length) : NSMakeRange(NSNotFound, 0); }
- (NSRange)selectedRange { return self.hasMarkedText ? self.markedSelection : NSMakeRange(0, 0); }
- (NSArray<NSAttributedStringKey> *)validAttributesForMarkedText { return @[]; }
- (NSAttributedString *)attributedSubstringForProposedRange:(NSRange)r actualRange:(NSRangePointer)actual { return nil; }
- (NSUInteger)characterIndexForPoint:(NSPoint)p { return NSNotFound; }

- (NSRect)firstRectForCharacterRange:(NSRange)r actualRange:(NSRangePointer)actual {
    NSRect inWindow = [self convertRect:self.caretRect toView:nil];
    return [self.window convertRectToScreen:inWindow];
}

- (void)setMarkedText:(id)string selectedRange:(NSRange)sel replacementRange:(NSRange)repl {
    NSString *s = [string isKindOfClass:[NSAttributedString class]] ? [string string] : string;
    self.marked = [[NSMutableAttributedString alloc] initWithString:s];
    self.markedSelection = sel;
    JeEvent ev = {.kind = JE_EV_PREEDIT, .cursor_begin = byteOffset(s, sel.location), .cursor_end = byteOffset(s, NSMaxRange(sel))};
    push(self.owner, ev, utf8(s));
}

- (void)unmarkText {
    if (!self.hasMarkedText) return;
    self.marked = nil;
    JeEvent ev = {.kind = JE_EV_PREEDIT, .cursor_begin = -1, .cursor_end = -1};
    push(self.owner, ev, nil);
}

- (void)insertText:(id)string replacementRange:(NSRange)repl {
    NSString *s = [string isKindOfClass:[NSAttributedString class]] ? [string string] : string;
    BOOL wasComposing = self.hasMarkedText;
    if (wasComposing) [self unmarkText];
    if (self.inKeyDown && !wasComposing) {
        // Plain typing: the text belongs to the key event.
        if (!self.keyText) self.keyText = [NSMutableString string];
        [self.keyText appendString:s];
        return;
    }
    JeEvent ev = {.kind = JE_EV_TEXT_INPUT};
    push(self.owner, ev, utf8(s));
}

- (void)doCommandBySelector:(SEL)sel {}

@end

// ---- Window delegate --------------------------------------------------------------

@implementation JeWindowDelegate

- (BOOL)windowShouldClose:(NSWindow *)sender {
    JeEvent ev = {.kind = JE_EV_CLOSE};
    push(self.owner, ev, nil);
    return NO;
}

- (void)windowDidBecomeKey:(NSNotification *)n {
    JeEvent ev = {.kind = JE_EV_FOCUS, .focused = 1};
    push(self.owner, ev, nil);
}

- (void)windowDidResignKey:(NSNotification *)n {
    JeEvent ev = {.kind = JE_EV_FOCUS, .focused = 0};
    push(self.owner, ev, nil);
}

- (void)windowDidChangeOcclusionState:(NSNotification *)n {
    // Coming back into view needs a redraw.
    pushResize(self.owner);
}

@end

// ---- Application ------------------------------------------------------------------

/// The application menu's Quit asks the key window to close, like its close
/// button, so unsaved changes are still asked about.
@interface JeAppDelegate : NSObject <NSApplicationDelegate>
@end

/// Paths from application:openURLs:, waiting for je_take_opened_paths.
static NSMutableArray<NSString *> *opened_paths;

@implementation JeAppDelegate
- (void)application:(NSApplication *)app openURLs:(NSArray<NSURL *> *)urls {
    if (!opened_paths) opened_paths = [NSMutableArray new];
    for (NSURL *url in urls) {
        if (url.isFileURL && url.path) [opened_paths addObject:url.path];
    }
}

- (NSApplicationTerminateReply)applicationShouldTerminate:(NSApplication *)app {
    for (NSWindow *win in app.windows) {
        if ([win.delegate isKindOfClass:[JeWindowDelegate class]]) [win performClose:nil];
    }
    return NSTerminateCancel;
}
@end

static JeAppDelegate *app_delegate;

/// Menu bar items that run an editor action. Their key equivalents only
/// label them: the view handles those keys first, through the keymap.
@interface JeMenuTarget : NSObject
- (void)run:(NSMenuItem *)item;
@end

@implementation JeMenuTarget
- (void)run:(NSMenuItem *)item {
    NSWindow *win = NSApp.keyWindow ?: NSApp.mainWindow;
    if (![win.delegate isKindOfClass:[JeWindowDelegate class]]) return;
    JeEvent ev = {.kind = JE_EV_COMMAND};
    push(((JeWindowDelegate *)win.delegate).owner, ev, utf8(item.representedObject));
}
@end

static JeMenuTarget *menu_target;

/// Adds an item that runs the editor action `action`.
static void addCommand(NSMenu *menu, NSString *title, NSString *action, NSString *key, NSEventModifierFlags mods) {
    NSMenuItem *item = [menu addItemWithTitle:title action:@selector(run:) keyEquivalent:key];
    item.keyEquivalentModifierMask = mods;
    item.target = menu_target;
    item.representedObject = action;
}

static void initApp(void) {
    static BOOL done = NO;
    if (done) return;
    done = YES;
    [NSApplication sharedApplication];
    [NSApp setActivationPolicy:NSApplicationActivationPolicyRegular];
    app_delegate = [JeAppDelegate new];
    NSApp.delegate = app_delegate;
    NSWindow.allowsAutomaticWindowTabbing = NO;

    NSString *name = @"CS Zig";
    NSMenu *bar = [NSMenu new];
    NSMenuItem *app_item = [NSMenuItem new];
    [bar addItem:app_item];
    NSMenu *app_menu = [NSMenu new];
    [app_menu addItemWithTitle:[@"Hide " stringByAppendingString:name] action:@selector(hide:) keyEquivalent:@"h"];
    NSMenuItem *others = [app_menu addItemWithTitle:@"Hide Others" action:@selector(hideOtherApplications:) keyEquivalent:@"h"];
    others.keyEquivalentModifierMask = NSEventModifierFlagCommand | NSEventModifierFlagOption;
    [app_menu addItemWithTitle:@"Show All" action:@selector(unhideAllApplications:) keyEquivalent:@""];
    [app_menu addItem:[NSMenuItem separatorItem]];
    [app_menu addItemWithTitle:[@"Quit " stringByAppendingString:name] action:@selector(terminate:) keyEquivalent:@"q"];
    app_item.submenu = app_menu;

    menu_target = [JeMenuTarget new];
    const NSEventModifierFlags cmd = NSEventModifierFlagCommand, shift = NSEventModifierFlagShift;

    NSMenuItem *file_item = [NSMenuItem new];
    [bar addItem:file_item];
    NSMenu *file_menu = [[NSMenu alloc] initWithTitle:@"File"];
    addCommand(file_menu, @"New File", @"new_file", @"n", cmd);
    addCommand(file_menu, @"Open…", @"open_file", @"o", cmd);
    addCommand(file_menu, @"Open Folder…", @"open_folder", @"", 0);
    [file_menu addItem:[NSMenuItem separatorItem]];
    addCommand(file_menu, @"Save", @"save", @"s", cmd);
    addCommand(file_menu, @"Save As…", @"save_as", @"s", cmd | shift);
    addCommand(file_menu, @"Save All", @"save_all", @"", 0);
    [file_menu addItem:[NSMenuItem separatorItem]];
    addCommand(file_menu, @"Close Tab", @"close_tab", @"w", cmd);
    file_item.submenu = file_menu;

    NSMenuItem *edit_item = [NSMenuItem new];
    [bar addItem:edit_item];
    NSMenu *edit_menu = [[NSMenu alloc] initWithTitle:@"Edit"];
    addCommand(edit_menu, @"Undo", @"undo", @"z", cmd);
    addCommand(edit_menu, @"Redo", @"redo", @"z", cmd | shift);
    [edit_menu addItem:[NSMenuItem separatorItem]];
    addCommand(edit_menu, @"Cut", @"cut", @"x", cmd);
    addCommand(edit_menu, @"Copy", @"copy", @"c", cmd);
    addCommand(edit_menu, @"Paste", @"paste", @"v", cmd);
    addCommand(edit_menu, @"Select All", @"select_all", @"a", cmd);
    [edit_menu addItem:[NSMenuItem separatorItem]];
    addCommand(edit_menu, @"Find…", @"show_find", @"f", cmd);
    addCommand(edit_menu, @"Replace…", @"show_replace", @"", 0);
    addCommand(edit_menu, @"Find in Files…", @"show_find_in_files", @"f", cmd | shift);
    edit_item.submenu = edit_menu;

    NSMenuItem *view_item = [NSMenuItem new];
    [bar addItem:view_item];
    NSMenu *view_menu = [[NSMenu alloc] initWithTitle:@"View"];
    addCommand(view_menu, @"Command Palette…", @"command_palette", @"p", cmd | shift);
    addCommand(view_menu, @"Goto Anything…", @"goto_anything", @"p", cmd);
    [view_menu addItem:[NSMenuItem separatorItem]];
    addCommand(view_menu, @"Toggle Side Bar", @"toggle_sidebar", @"", 0);
    addCommand(view_menu, @"Toggle Minimap", @"toggle_minimap", @"", 0);
    addCommand(view_menu, @"Enter Full Screen", @"toggle_fullscreen", @"f", cmd | NSEventModifierFlagControl);
    view_item.submenu = view_menu;

    NSMenuItem *window_item = [NSMenuItem new];
    [bar addItem:window_item];
    NSMenu *window_menu = [[NSMenu alloc] initWithTitle:@"Window"];
    [window_menu addItemWithTitle:@"Minimize" action:@selector(performMiniaturize:) keyEquivalent:@"m"];
    [window_menu addItemWithTitle:@"Zoom" action:@selector(performZoom:) keyEquivalent:@""];
    window_item.submenu = window_menu;
    NSApp.mainMenu = bar;
    NSApp.windowsMenu = window_menu;

    [NSApp finishLaunching];
}

// ---- Waiting on file descriptors ---------------------------------------------------

/// While the main thread waits for Cocoa events, this thread polls the
/// editor's descriptors and posts an event when one is ready.
static struct {
    pthread_mutex_t mu;
    pthread_cond_t cv;
    int ctl[2];
    struct pollfd *fds; // nfds descriptors, then the control pipe
    int nfds;
    int cap;
    enum { IDLE, REQUESTED, DONE } state;
} poller = {.mu = PTHREAD_MUTEX_INITIALIZER, .cv = PTHREAD_COND_INITIALIZER, .ctl = {-1, -1}};

static void *pollerMain(void *arg) {
    (void)arg;
    for (;;) {
        pthread_mutex_lock(&poller.mu);
        while (poller.state != REQUESTED) pthread_cond_wait(&poller.cv, &poller.mu);
        int n = poller.nfds;
        pthread_mutex_unlock(&poller.mu);

        int ready = poll(poller.fds, (nfds_t)n + 1, -1);
        BOOL wake = ready > 0 && !(poller.fds[n].revents & POLLIN);

        pthread_mutex_lock(&poller.mu);
        poller.state = DONE;
        pthread_cond_broadcast(&poller.cv);
        pthread_mutex_unlock(&poller.mu);
        if (wake) {
            @autoreleasepool {
                NSEvent *ev = [NSEvent otherEventWithType:NSEventTypeApplicationDefined location:NSZeroPoint modifierFlags:0
                                                timestamp:0 windowNumber:0 context:nil subtype:0 data1:0 data2:0];
                [NSApp postEvent:ev atStart:NO];
            }
        }
    }
    return NULL;
}

static void startPoller(void) {
    if (poller.ctl[0] >= 0) return;
    if (pipe(poller.ctl) != 0) return;
    for (int i = 0; i < 2; i++) fcntl(poller.ctl[i], F_SETFD, FD_CLOEXEC);
    fcntl(poller.ctl[0], F_SETFL, O_NONBLOCK);
    pthread_t thread;
    pthread_create(&thread, NULL, pollerMain, NULL);
    pthread_detach(thread);
}

static BOOL dispatchPending(void) {
    BOOL any = NO;
    for (;;) {
        NSEvent *e = [NSApp nextEventMatchingMask:NSEventMaskAny untilDate:[NSDate distantPast] inMode:NSDefaultRunLoopMode dequeue:YES];
        if (!e) return any;
        any = YES;
        if (e.type != NSEventTypeApplicationDefined) [NSApp sendEvent:e];
    }
}

/// Waits for Cocoa events or ready descriptors. Also used without a window
/// (by passing NULL), so a windowless session daemon keeps Cocoa serviced.
static void waitFor(int timeout_ms, struct pollfd *fds, int nfds) {
    if (poll(fds, (nfds_t)nfds, 0) > 0) return;
    startPoller();
    // The poller thread only touches fds between REQUESTED and DONE, so
    // growing it here, while IDLE, is safe.
    if (nfds + 1 > poller.cap) {
        int cap = poller.cap ? poller.cap : 64;
        while (cap < nfds + 1) cap *= 2;
        struct pollfd *grown = realloc(poller.fds, (size_t)cap * sizeof(struct pollfd));
        if (!grown) {
            // Without room to hand them over, wait on the descriptors here and
            // service Cocoa once they're ready or the timeout passes.
            poll(fds, (nfds_t)nfds, timeout_ms);
            dispatchPending();
            return;
        }
        poller.fds = grown;
        poller.cap = cap;
    }
    pthread_mutex_lock(&poller.mu);
    memcpy(poller.fds, fds, (size_t)nfds * sizeof(struct pollfd));
    poller.fds[nfds] = (struct pollfd){.fd = poller.ctl[0], .events = POLLIN};
    poller.nfds = nfds;
    poller.state = REQUESTED;
    pthread_cond_broadcast(&poller.cv);
    pthread_mutex_unlock(&poller.mu);

    NSDate *until = timeout_ms < 0 ? [NSDate distantFuture] : [NSDate dateWithTimeIntervalSinceNow:timeout_ms / 1000.0];
    NSEvent *e = [NSApp nextEventMatchingMask:NSEventMaskAny untilDate:until inMode:NSDefaultRunLoopMode dequeue:YES];
    if (e && e.type != NSEventTypeApplicationDefined) [NSApp sendEvent:e];

    char byte = 1;
    (void)!write(poller.ctl[1], &byte, 1);
    pthread_mutex_lock(&poller.mu);
    while (poller.state != DONE) pthread_cond_wait(&poller.cv, &poller.mu);
    poller.state = IDLE;
    pthread_mutex_unlock(&poller.mu);
    char drain[64];
    while (read(poller.ctl[0], drain, sizeof drain) > 0) {}
}

// ---- C interface ----------------------------------------------------------------

JeWindow *je_window_create(const char *title, int width, int height) {
    @autoreleasepool {
        initApp();
        NSOpenGLPixelFormatAttribute attrs[] = {
            NSOpenGLPFAOpenGLProfile, NSOpenGLProfileVersion4_1Core,
            NSOpenGLPFAColorSize, 24,
            NSOpenGLPFAAlphaSize, 8,
            NSOpenGLPFADoubleBuffer,
            NSOpenGLPFAAccelerated,
            0,
        };
        NSOpenGLPixelFormat *format = [[NSOpenGLPixelFormat alloc] initWithAttributes:attrs];
        if (!format) return NULL;
        NSOpenGLContext *gl = [[NSOpenGLContext alloc] initWithFormat:format shareContext:nil];
        if (!gl) return NULL;

        JeWindow *w = calloc(1, sizeof(JeWindow));
        w->keep = [NSMutableArray new];
        NSRect frame = NSMakeRect(0, 0, width, height);
        NSWindowStyleMask style = NSWindowStyleMaskTitled | NSWindowStyleMaskClosable | NSWindowStyleMaskMiniaturizable |
                                  NSWindowStyleMaskResizable;
        w->window = [[NSWindow alloc] initWithContentRect:frame styleMask:style backing:NSBackingStoreBuffered defer:NO];
        w->window.releasedWhenClosed = NO;
        w->window.title = [NSString stringWithUTF8String:title];
        w->window.collectionBehavior = NSWindowCollectionBehaviorFullScreenPrimary;
        w->window.acceptsMouseMovedEvents = YES;

        w->view = [[JeView alloc] initWithFrame:frame];
        w->view.wantsBestResolutionOpenGLSurface = YES;
        w->view.cursor = [NSCursor arrowCursor];
        w->view.pressedButton = -1;
        w->view.textInputEnabled = YES;
        w->view.gl = gl;
        w->window.contentView = w->view;
        w->view.owner = w;
        w->delegate = [JeWindowDelegate new];
        w->delegate.owner = w;
        w->window.delegate = w->delegate;

        [w->window center];
        [w->window makeKeyAndOrderFront:nil];
        [w->window makeFirstResponder:w->view];
        [NSApp activateIgnoringOtherApps:YES];

        GLint one = 1;
        [gl setValues:&one forParameter:NSOpenGLContextParameterSwapInterval];
        [gl setValues:&one forParameter:NSOpenGLContextParameterSurfaceOpacity];
        gl.view = w->view;
        [gl makeCurrentContext];
        [gl update];
        pushResize(w);
        return w;
    }
}

void je_window_destroy(JeWindow *w) {
    @autoreleasepool {
        [NSOpenGLContext clearCurrentContext];
        [w->view.gl clearDrawable];
        w->view.owner = NULL;
        w->delegate.owner = NULL;
        w->window.delegate = nil;
        [w->window orderOut:nil];
        [w->window close];
        w->view = nil;
        w->delegate = nil;
        w->window = nil;
        w->keep = nil;
        free(w->events);
        free(w);
        // Let Cocoa finish tearing the window down.
        dispatchPending();
    }
}

void je_window_size(JeWindow *w, double *width, double *height, double *scale) {
    NSSize size = w->view.bounds.size;
    *width = size.width;
    *height = size.height;
    *scale = w->window.backingScaleFactor;
}

int je_poll(JeWindow *w, int timeout_ms, struct pollfd *fds, int nfds) {
    @autoreleasepool {
        initApp();
        if (w) {
            // Drop what the previous call returned; keep events queued since
            // (by calls made outside je_poll).
            size_t rest = w->count - w->returned;
            memmove(w->events, w->events + w->returned, rest * sizeof(JeEvent));
            [w->keep removeObjectsInRange:NSMakeRange(0, w->returned)];
            w->count = rest;
            w->returned = 0;
        }
        for (int i = 0; i < nfds; i++) fds[i].revents = 0;

        dispatchPending();
        if (!(w && w->count > 0) && timeout_ms != 0) {
            waitFor(timeout_ms, fds, nfds);
            dispatchPending();
        }
        int ready = poll(fds, (nfds_t)nfds, 0);
        if (w) {
            w->returned = w->count;
            [w->view.gl makeCurrentContext];
        }
        return ready;
    }
}

size_t je_event_count(JeWindow *w) { return w->returned; }
const JeEvent *je_events(JeWindow *w) { return w->events; }

int je_can_render(JeWindow *w) {
    return !w->window.miniaturized && (w->window.occlusionState & NSWindowOcclusionStateVisible) != 0;
}

void je_present(JeWindow *w) { [w->view.gl flushBuffer]; }

void je_set_app_icon(const unsigned char *rgba, int size) {
    @autoreleasepool {
        initApp();
        NSBitmapImageRep *rep = [[NSBitmapImageRep alloc] initWithBitmapDataPlanes:NULL pixelsWide:size pixelsHigh:size
            bitsPerSample:8 samplesPerPixel:4 hasAlpha:YES isPlanar:NO colorSpaceName:NSDeviceRGBColorSpace
            bitmapFormat:NSBitmapFormatAlphaNonpremultiplied bytesPerRow:size * 4 bitsPerPixel:32];
        if (!rep) return;
        memcpy(rep.bitmapData, rgba, (size_t)size * size * 4);
        NSImage *image = [[NSImage alloc] initWithSize:NSMakeSize(size, size)];
        [image addRepresentation:rep];
        NSApp.applicationIconImage = image;
    }
}

int je_image_decode(const unsigned char *data, size_t len, int max_width, int *out_w, int *out_h, unsigned char **out_rgba) {
    @autoreleasepool {
        NSData *bytes = [NSData dataWithBytesNoCopy:(void *)data length:len freeWhenDone:NO];
        NSBitmapImageRep *src = [NSBitmapImageRep imageRepWithData:bytes];
        if (!src) return 0;
        NSInteger w = src.pixelsWide, h = src.pixelsHigh;
        if (w <= 0 || h <= 0 || w > 32768 || h > 32768) return 0;
        NSInteger tw = w, th = h;
        if (max_width > 0 && w > max_width) {
            tw = max_width;
            th = h * max_width / w;
            if (th < 1) th = 1;
        }
        NSBitmapImageRep *dst = [[NSBitmapImageRep alloc] initWithBitmapDataPlanes:NULL pixelsWide:tw pixelsHigh:th
            bitsPerSample:8 samplesPerPixel:4 hasAlpha:YES isPlanar:NO colorSpaceName:NSDeviceRGBColorSpace
            bytesPerRow:tw * 4 bitsPerPixel:32];
        if (!dst) return 0;
        memset(dst.bitmapData, 0, (size_t)dst.bytesPerRow * (size_t)th);
        NSGraphicsContext *ctx = [NSGraphicsContext graphicsContextWithBitmapImageRep:dst];
        if (!ctx) return 0;
        [NSGraphicsContext saveGraphicsState];
        [NSGraphicsContext setCurrentContext:ctx];
        ctx.imageInterpolation = NSImageInterpolationHigh;
        [src drawInRect:NSMakeRect(0, 0, tw, th) fromRect:NSZeroRect operation:NSCompositingOperationSourceOver
            fraction:1.0 respectFlipped:NO hints:nil];
        [NSGraphicsContext restoreGraphicsState];
        unsigned char *out = malloc((size_t)tw * (size_t)th * 4);
        if (!out) return 0;
        // The bitmap is premultiplied; hand back straight alpha.
        for (NSInteger y = 0; y < th; y++) {
            const unsigned char *row = dst.bitmapData + (size_t)y * dst.bytesPerRow;
            unsigned char *o = out + (size_t)y * tw * 4;
            for (NSInteger x = 0; x < tw; x++) {
                unsigned a = row[x * 4 + 3];
                for (int c = 0; c < 3; c++) {
                    unsigned v = row[x * 4 + c];
                    o[x * 4 + c] = a == 0 ? 0 : (a == 255 ? v : (unsigned char)((v * 255 + a / 2) / a > 255 ? 255 : (v * 255 + a / 2) / a));
                }
                o[x * 4 + 3] = (unsigned char)a;
            }
        }
        *out_w = (int)tw;
        *out_h = (int)th;
        *out_rgba = out;
        return 1;
    }
}

void je_set_title(JeWindow *w, const char *title) {
    @autoreleasepool {
        w->window.title = [NSString stringWithUTF8String:title] ?: @"";
    }
}

void je_set_cursor(JeWindow *w, int shape) {
    NSCursor *cursor;
    switch (shape) {
    case 1: cursor = [NSCursor IBeamCursor]; break;
    case 2: cursor = [NSCursor pointingHandCursor]; break;
    case 3: cursor = [NSCursor resizeLeftRightCursor]; break;
    case 4: cursor = [NSCursor resizeUpDownCursor]; break;
    case 5: cursor = [NSCursor openHandCursor]; break;
    case 6: cursor = [NSCursor closedHandCursor]; break;
    default: cursor = [NSCursor arrowCursor]; break;
    }
    if (cursor == w->view.cursor) return;
    w->view.cursor = cursor;
    [w->window invalidateCursorRectsForView:w->view];
    NSPoint p = [w->view convertPoint:w->window.mouseLocationOutsideOfEventStream fromView:nil];
    if (NSPointInRect(p, w->view.bounds)) [cursor set];
}

void je_toggle_fullscreen(JeWindow *w) { [w->window toggleFullScreen:nil]; }

void je_set_text_input_rect(JeWindow *w, double x, double y, double width, double height, int enabled) {
    w->view.caretRect = NSMakeRect(x, y, width, height);
    if (w->view.textInputEnabled && !enabled && w->view.hasMarkedText) {
        [w->view.inputContext discardMarkedText];
        w->view.marked = nil;
    }
    w->view.textInputEnabled = enabled != 0;
}

void je_set_clipboard(const char *text, size_t len) {
    @autoreleasepool {
        NSString *s = [[NSString alloc] initWithBytes:text length:len encoding:NSUTF8StringEncoding];
        if (!s) return;
        NSPasteboard *pb = [NSPasteboard generalPasteboard];
        [pb clearContents];
        [pb setString:s forType:NSPasteboardTypeString];
    }
}

char *je_get_clipboard(size_t *len) {
    @autoreleasepool {
        NSString *s = [[NSPasteboard generalPasteboard] stringForType:NSPasteboardTypeString];
        if (!s) return NULL;
        NSData *data = utf8(s);
        char *out = malloc(data.length + 1);
        if (!out) return NULL;
        memcpy(out, data.bytes, data.length);
        out[data.length] = 0;
        *len = data.length;
        return out;
    }
}

char *je_pick_folder(const char *start, const char *title, size_t *len) {
    @autoreleasepool {
        NSOpenPanel *panel = [NSOpenPanel openPanel];
        panel.canChooseFiles = NO;
        panel.canChooseDirectories = YES;
        panel.canCreateDirectories = YES;
        panel.allowsMultipleSelection = NO;
        if (title) panel.message = [NSString stringWithUTF8String:title];
        if (start && start[0]) panel.directoryURL = [NSURL fileURLWithPath:[NSString stringWithUTF8String:start] isDirectory:YES];
        if ([panel runModal] != NSModalResponseOK || !panel.URL) return NULL;
        NSData *data = utf8(panel.URL.path);
        char *out = malloc(data.length + 1);
        if (!out) return NULL;
        memcpy(out, data.bytes, data.length);
        out[data.length] = 0;
        *len = data.length;
        return out;
    }
}

char *je_take_opened_paths(size_t *len) {
    @autoreleasepool {
        if (opened_paths.count == 0) return NULL;
        NSData *data = utf8([opened_paths componentsJoinedByString:@"\n"]);
        [opened_paths removeAllObjects];
        char *out = malloc(data.length + 1);
        if (!out) return NULL;
        memcpy(out, data.bytes, data.length);
        out[data.length] = 0;
        *len = data.length;
        return out;
    }
}

int je_process_cwd(int pid, char *buf, size_t len) {
    struct proc_vnodepathinfo info;
    if (proc_pidinfo(pid, PROC_PIDVNODEPATHINFO, 0, &info, sizeof info) != sizeof info) return -1;
    size_t n = strnlen(info.pvi_cdir.vip_path, sizeof info.pvi_cdir.vip_path);
    if (n == 0 || n >= len) return -1;
    memcpy(buf, info.pvi_cdir.vip_path, n + 1);
    return 0;
}

const void *je_gl_proc_address(const char *name) {
    static void *lib;
    if (!lib) lib = dlopen("/System/Library/Frameworks/OpenGL.framework/OpenGL", RTLD_LAZY | RTLD_LOCAL);
    return lib ? dlsym(lib, name) : NULL;
}

void *je_headless_context_create(void) {
    CGLPixelFormatAttribute attrs[] = {
        kCGLPFAOpenGLProfile, (CGLPixelFormatAttribute)kCGLOGLPVersion_GL4_Core,
        kCGLPFAColorSize, (CGLPixelFormatAttribute)24,
        kCGLPFAAllowOfflineRenderers,
        (CGLPixelFormatAttribute)0,
    };
    CGLPixelFormatObj format = NULL;
    GLint count = 0;
    if (CGLChoosePixelFormat(attrs, &format, &count) != kCGLNoError || !format) return NULL;
    CGLContextObj ctx = NULL;
    CGLError err = CGLCreateContext(format, NULL, &ctx);
    CGLReleasePixelFormat(format);
    if (err != kCGLNoError) return NULL;
    CGLSetCurrentContext(ctx);
    return ctx;
}

void je_headless_context_destroy(void *ctx) {
    CGLSetCurrentContext(NULL);
    CGLReleaseContext(ctx);
}

// ---- File watching ------------------------------------------------------------------

#include <CoreServices/CoreServices.h>
#include <sys/stat.h>

struct JeWatcher {
    FSEventStreamRef stream;
    dispatch_queue_t queue;
    int pipe[2];
    pthread_mutex_t mu;
    NSMutableData *pending;
    /// Watched folders as the editor names them, and as FSEvents reports them
    /// (symlinks resolved: /tmp is /private/tmp).
    NSArray<NSString *> *dirs;
    NSArray<NSString *> *real_dirs;
};

static void watcherCallback(ConstFSEventStreamRef stream, void *info, size_t count, void *paths,
                            const FSEventStreamEventFlags *flags, const FSEventStreamEventId *ids) {
    JeWatcher *w = info;
    NSArray *list = (__bridge NSArray *)paths;
    NSMutableData *out = [NSMutableData data];
    for (size_t i = 0; i < count; i++) {
        if (flags[i] & kFSEventStreamEventFlagItemIsDir) continue;
        NSString *path = list[i];
        NSString *parent = path.stringByDeletingLastPathComponent;
        NSUInteger at = [w->real_dirs indexOfObject:parent];
        if (at == NSNotFound) continue;
        NSString *named = [w->dirs[at] stringByAppendingPathComponent:path.lastPathComponent];
        struct stat st;
        char kind = stat(path.fileSystemRepresentation, &st) == 0 ? 'c' : 'd';
        [out appendBytes:&kind length:1];
        const char *bytes = named.fileSystemRepresentation;
        [out appendBytes:bytes length:strlen(bytes) + 1];
    }
    if (out.length == 0) return;
    pthread_mutex_lock(&w->mu);
    [w->pending appendData:out];
    pthread_mutex_unlock(&w->mu);
    char byte = 1;
    (void)!write(w->pipe[1], &byte, 1);
}

JeWatcher *je_watcher_create(void) {
    JeWatcher *w = calloc(1, sizeof(JeWatcher));
    if (pipe(w->pipe) != 0) {
        free(w);
        return NULL;
    }
    for (int i = 0; i < 2; i++) {
        fcntl(w->pipe[i], F_SETFD, FD_CLOEXEC);
        fcntl(w->pipe[i], F_SETFL, O_NONBLOCK);
    }
    pthread_mutex_init(&w->mu, NULL);
    w->pending = [NSMutableData data];
    w->queue = dispatch_queue_create("cs-zig.watcher", DISPATCH_QUEUE_SERIAL);
    w->dirs = @[];
    w->real_dirs = @[];
    return w;
}

static void stopStream(JeWatcher *w) {
    if (!w->stream) return;
    FSEventStreamStop(w->stream);
    FSEventStreamInvalidate(w->stream);
    FSEventStreamRelease(w->stream);
    w->stream = NULL;
    // Wait for a callback in flight.
    dispatch_sync(w->queue, ^{});
}

void je_watcher_destroy(JeWatcher *w) {
    stopStream(w);
    close(w->pipe[0]);
    close(w->pipe[1]);
    pthread_mutex_destroy(&w->mu);
    w->pending = nil;
    w->dirs = nil;
    w->real_dirs = nil;
    w->queue = nil;
    free(w);
}

int je_watcher_fd(JeWatcher *w) { return w->pipe[0]; }

void je_watcher_set_dirs(JeWatcher *w, const char *const *dirs, const size_t *lens, size_t count) {
    @autoreleasepool {
        stopStream(w);
        NSMutableArray *named = [NSMutableArray array];
        NSMutableArray *real = [NSMutableArray array];
        for (size_t i = 0; i < count; i++) {
            NSString *dir = [[NSString alloc] initWithBytes:dirs[i] length:lens[i] encoding:NSUTF8StringEncoding];
            if (!dir) continue;
            char resolved[PATH_MAX];
            if (!realpath(dir.fileSystemRepresentation, resolved)) continue;
            [named addObject:dir];
            [real addObject:[NSString stringWithUTF8String:resolved]];
        }
        w->dirs = named;
        w->real_dirs = real;
        if (real.count == 0) return;
        FSEventStreamContext ctx = {.info = w};
        FSEventStreamCreateFlags flags = kFSEventStreamCreateFlagFileEvents | kFSEventStreamCreateFlagNoDefer |
                                         kFSEventStreamCreateFlagUseCFTypes;
        w->stream = FSEventStreamCreate(NULL, watcherCallback, &ctx, (__bridge CFArrayRef)real,
                                        kFSEventStreamEventIdSinceNow, 0.05, flags);
        if (!w->stream) return;
        FSEventStreamSetDispatchQueue(w->stream, w->queue);
        FSEventStreamStart(w->stream);
    }
}

char *je_watcher_read(JeWatcher *w, size_t *len) {
    char drain[64];
    while (read(w->pipe[0], drain, sizeof drain) > 0) {}
    pthread_mutex_lock(&w->mu);
    char *out = NULL;
    *len = w->pending.length;
    if (*len > 0) {
        out = malloc(*len);
        if (out) memcpy(out, w->pending.bytes, *len);
        w->pending.length = 0;
    }
    pthread_mutex_unlock(&w->mu);
    return out;
}
