//! A debug session: the Debug Adapter Protocol client's state machine and
//! the data the debug panels show (threads, call stack, scopes, variables,
//! watches, console).
//!
//! The session never touches a process or a thread: it writes requests to a
//! `Transport` and is fed the adapter's messages by `handleMessage`, always
//! on the main thread. A scripted fake adapter therefore drives it in tests,
//! and `process.zig` connects a real one.
//!
//! One `Session` object lives as long as the editor; `begin` starts a run.

const std = @import("std");
const Allocator = std.mem.Allocator;
const Value = std.json.Value;
const breakpoints = @import("breakpoints.zig");

pub const Transport = struct {
    ctx: *anyopaque,
    /// Sends one message body (framing is the transport's business).
    write: *const fn (ctx: *anyopaque, body: []const u8) void,
};

pub const State = enum {
    /// No run yet.
    idle,
    /// initialize … configurationDone.
    starting,
    running,
    stopped,
    /// The adapter or the program is gone.
    ended,
};

pub const Frame = struct {
    id: i64,
    name: []const u8,
    path: ?[]const u8,
    /// 1-based.
    line: u32,
    column: u32,
};

pub const Thread = struct { id: i64, name: []const u8 };

pub const Scope = struct { name: []const u8, reference: i64, expensive: bool };

pub const Variable = struct {
    name: []const u8,
    value: []const u8,
    type_name: []const u8,
    /// Non-zero when it has children.
    reference: i64,
};

pub const Children = union(enum) {
    loading,
    items: []const Variable,
    failed: []const u8,
};

pub const Category = enum { console, stdout, stderr, important, input, result };

pub const OutputLine = struct { category: Category, text: []u8 };

pub const Watch = struct {
    expr: []u8,
    /// The last result (an error's message when `is_error`); null before one.
    value: ?[]u8 = null,
    type_name: ?[]u8 = null,
    reference: i64 = 0,
    is_error: bool = false,
};

pub const Event = union(enum) {
    /// Something the panels show changed.
    changed,
    /// The code to show: a stop, or another frame picked.
    reveal: struct { path: ?[]const u8, line: u32 },
    /// The run is over.
    ended,
};

pub const Sink = struct {
    ctx: *anyopaque,
    notify: *const fn (ctx: *anyopaque, event: Event) void,
};

const Kind = enum {
    initialize,
    launch,
    set_breakpoints,
    config_done,
    threads,
    stack_trace,
    scopes,
    variables,
    evaluate_watch,
    evaluate_repl,
    disconnect,
    other,
};

const Pending = struct {
    kind: Kind,
    epoch: u32 = 0,
    ref: i64 = 0,
    index: usize = 0,
    path: ?[]u8 = null,
    lines: ?[]u32 = null,
    command: []const u8 = "",
};

const max_output_lines = 5000;
const stack_levels = 64;

pub const Session = struct {
    gpa: Allocator,
    sink: Sink,
    store: *breakpoints.Store,
    transport: ?Transport = null,
    state: State = .idle,
    seq: i64 = 0,
    pending: std.AutoHashMapUnmanaged(i64, Pending) = .empty,

    /// The request that starts the program ("launch" or "attach") and its
    /// arguments, held until the adapter has answered `initialize`.
    start_command: []const u8 = "launch",
    start_arguments: ?[]u8 = null,
    supports_config_done: bool = true,
    stopping: bool = false,

    /// Everything about the current stop, dropped when the program resumes.
    stop_arena: std.heap.ArenaAllocator,
    /// Counts stops and resumes: an answer to an earlier one is ignored.
    epoch: u32 = 0,
    threads: []const Thread = &.{},
    frames: []const Frame = &.{},
    scopes: []const Scope = &.{},
    variables: std.AutoHashMapUnmanaged(i64, Children) = .empty,
    thread_id: i64 = 0,
    frame_index: usize = 0,
    stop_reason: []const u8 = "",
    stop_text: []const u8 = "",

    output: std.ArrayList(OutputLine) = .empty,
    /// Bumped when `output` gains a line, for views that follow it.
    output_seq: u64 = 0,
    watches: std.ArrayList(Watch) = .empty,
    exit_code: ?i64 = null,
    /// The program's process id, from the adapter's `process` event.
    debuggee_pid: ?i64 = null,
    /// The last error worth telling the user (an adapter's refusal).
    error_text: ?[]u8 = null,

    pub fn init(gpa: Allocator, store: *breakpoints.Store, sink: Sink) Session {
        return .{ .gpa = gpa, .sink = sink, .store = store, .stop_arena = .init(gpa) };
    }

    pub fn deinit(self: *Session) void {
        self.clearPending();
        self.pending.deinit(self.gpa);
        if (self.start_arguments) |a| self.gpa.free(a);
        self.stop_arena.deinit();
        self.variables = .empty;
        for (self.output.items) |l| self.gpa.free(l.text);
        self.output.deinit(self.gpa);
        for (self.watches.items) |*w| freeWatchResult(self.gpa, w);
        for (self.watches.items) |w| self.gpa.free(w.expr);
        self.watches.deinit(self.gpa);
        if (self.error_text) |e| self.gpa.free(e);
    }

    pub fn isLive(self: *const Session) bool {
        return self.state == .starting or self.state == .running or self.state == .stopped;
    }

    fn notify(self: *Session, event: Event) void {
        self.sink.notify(self.sink.ctx, event);
    }

    // ---- Starting and ending ----------------------------------------------------

    /// Starts a run over `transport`: `command` is "launch" or "attach" and
    /// `arguments_json` the configuration.
    pub fn begin(self: *Session, transport: Transport, command: []const u8, arguments_json: []const u8) !void {
        std.debug.assert(!self.isLive());
        const args = try self.gpa.dupe(u8, arguments_json);
        errdefer self.gpa.free(args);
        self.clearPending();
        self.resetStop();
        if (self.start_arguments) |a| self.gpa.free(a);
        self.start_arguments = args;
        self.start_command = if (std.mem.eql(u8, command, "attach")) "attach" else "launch";
        self.transport = transport;
        self.state = .starting;
        self.stopping = false;
        self.exit_code = null;
        self.debuggee_pid = null;
        self.setError(null);
        self.store.clearVerdicts();
        self.addOutput(.console, "Starting the debug adapter…");
        self.request("initialize", .{
            .clientID = "cs-zig",
            .clientName = "cs-zig",
            .adapterID = "cs-zig",
            .linesStartAt1 = true,
            .columnsStartAt1 = true,
            .pathFormat = "path",
            .supportsVariableType = true,
            .supportsVariablePaging = false,
            .supportsRunInTerminalRequest = false,
        }, .{ .kind = .initialize });
        self.notify(.changed);
    }

    /// Asks the adapter to end the run; `adapterExited` (or the answer to
    /// the disconnect) finishes it.
    pub fn stop(self: *Session) void {
        if (!self.isLive() or self.stopping) return;
        self.stopping = true;
        self.request("disconnect", .{ .terminateDebuggee = !std.mem.eql(u8, self.start_command, "attach") }, .{ .kind = .disconnect });
    }

    /// The adapter's process or connection ended.
    pub fn adapterExited(self: *Session) void {
        self.finish();
    }

    fn finish(self: *Session) void {
        if (self.state == .ended or self.state == .idle) return;
        const was_live = self.isLive();
        self.state = .ended;
        self.transport = null;
        self.clearPending();
        self.resetStop();
        self.store.clearVerdicts();
        if (was_live) self.addOutput(.console, "The debug session has ended.");
        self.notify(.ended);
        self.notify(.changed);
    }

    // ---- Requests -----------------------------------------------------------------

    fn request(self: *Session, command: []const u8, args: anytype, pending: Pending) void {
        const text = std.json.Stringify.valueAlloc(self.gpa, args, .{}) catch return self.dropPending(pending);
        defer self.gpa.free(text);
        self.requestRaw(command, text, pending);
    }

    fn requestRaw(self: *Session, command: []const u8, arguments: []const u8, pending: Pending) void {
        const t = self.transport orelse return self.dropPending(pending);
        self.seq += 1;
        const body = std.fmt.allocPrint(self.gpa, "{{\"seq\":{d},\"type\":\"request\",\"command\":\"{s}\",\"arguments\":{s}}}", .{ self.seq, command, arguments }) catch return self.dropPending(pending);
        defer self.gpa.free(body);
        var p = pending;
        p.command = command;
        self.pending.put(self.gpa, self.seq, p) catch return self.dropPending(pending);
        t.write(t.ctx, body);
    }

    fn dropPending(self: *Session, p: Pending) void {
        if (p.path) |path| self.gpa.free(path);
        if (p.lines) |l| self.gpa.free(l);
    }

    fn clearPending(self: *Session) void {
        var it = self.pending.valueIterator();
        while (it.next()) |p| self.dropPending(p.*);
        self.pending.clearRetainingCapacity();
    }

    /// Sends the breakpoints of one file (an empty list clears it).
    pub fn sendBreakpoints(self: *Session, path: []const u8) void {
        if (!self.isLive() or self.state == .starting) return;
        self.sendBreakpointsNow(path);
    }

    fn sendBreakpointsNow(self: *Session, path: []const u8) void {
        const list = self.store.forFile(path);
        var lines: std.ArrayList(u32) = .empty;
        for (list) |bp| if (bp.enabled) lines.append(self.gpa, bp.line) catch {};
        const Bp = struct { line: u32 };
        const bps = self.gpa.alloc(Bp, lines.items.len) catch {
            lines.deinit(self.gpa);
            return;
        };
        defer self.gpa.free(bps);
        for (lines.items, bps) |l, *b| b.* = .{ .line = l };
        const owned_path = self.gpa.dupe(u8, path) catch {
            lines.deinit(self.gpa);
            return;
        };
        const owned_lines = lines.toOwnedSlice(self.gpa) catch {
            self.gpa.free(owned_path);
            return;
        };
        self.request("setBreakpoints", .{
            .source = .{ .path = path },
            .breakpoints = bps,
            .sourceModified = false,
        }, .{ .kind = .set_breakpoints, .path = owned_path, .lines = owned_lines });
    }

    fn sendAllBreakpoints(self: *Session) void {
        for (self.store.files.keys()) |path| self.sendBreakpointsNow(path);
    }

    // ---- Execution control --------------------------------------------------------

    fn resume_(self: *Session, command: []const u8) void {
        if (self.state != .stopped) return;
        self.state = .running;
        self.resetStop();
        self.request(command, .{ .threadId = self.thread_id }, .{ .kind = .other });
        self.notify(.changed);
    }

    pub fn continue_(self: *Session) void {
        self.resume_("continue");
    }

    pub fn stepOver(self: *Session) void {
        self.resume_("next");
    }

    pub fn stepInto(self: *Session) void {
        self.resume_("stepIn");
    }

    pub fn stepOut(self: *Session) void {
        self.resume_("stepOut");
    }

    pub fn pause(self: *Session) void {
        if (self.state != .running) return;
        self.request("pause", .{ .threadId = self.thread_id }, .{ .kind = .other });
    }

    // ---- Inspecting ---------------------------------------------------------------

    /// Makes another frame of the stop current.
    pub fn selectFrame(self: *Session, index: usize) void {
        if (self.state != .stopped or index >= self.frames.len) return;
        self.frame_index = index;
        self.scopes = &.{};
        self.variables = .empty;
        const f = self.frames[index];
        self.loadScopes();
        self.evaluateWatches();
        self.notify(.{ .reveal = .{ .path = f.path, .line = f.line } });
        self.notify(.changed);
    }

    fn currentFrameId(self: *const Session) ?i64 {
        if (self.state != .stopped or self.frame_index >= self.frames.len) return null;
        return self.frames[self.frame_index].id;
    }

    fn loadScopes(self: *Session) void {
        const id = self.currentFrameId() orelse return;
        self.request("scopes", .{ .frameId = id }, .{ .kind = .scopes, .epoch = self.epoch, .index = self.frame_index });
    }

    /// Asks for the children of a variable or scope, once per stop.
    pub fn expand(self: *Session, reference: i64) void {
        if (self.state != .stopped or reference == 0) return;
        const arena = self.stop_arena.allocator();
        const gop = self.variables.getOrPut(arena, reference) catch return;
        if (gop.found_existing) return;
        gop.value_ptr.* = .loading;
        self.request("variables", .{ .variablesReference = reference }, .{ .kind = .variables, .epoch = self.epoch, .ref = reference });
        self.notify(.changed);
    }

    pub fn childrenOf(self: *const Session, reference: i64) ?Children {
        return self.variables.get(reference);
    }

    // ---- Watches and the console --------------------------------------------------

    pub fn addWatch(self: *Session, expr: []const u8) !void {
        const owned = try self.gpa.dupe(u8, expr);
        errdefer self.gpa.free(owned);
        try self.watches.append(self.gpa, .{ .expr = owned });
        self.evaluateWatch(self.watches.items.len - 1);
        self.notify(.changed);
    }

    pub fn removeWatch(self: *Session, index: usize) void {
        if (index >= self.watches.items.len) return;
        var w = self.watches.orderedRemove(index);
        freeWatchResult(self.gpa, &w);
        self.gpa.free(w.expr);
        self.notify(.changed);
    }

    fn freeWatchResult(gpa: Allocator, w: *Watch) void {
        if (w.value) |v| gpa.free(v);
        if (w.type_name) |t| gpa.free(t);
        w.value = null;
        w.type_name = null;
        w.reference = 0;
    }

    fn evaluateWatches(self: *Session) void {
        for (0..self.watches.items.len) |i| self.evaluateWatch(i);
    }

    fn evaluateWatch(self: *Session, index: usize) void {
        const frame_id = self.currentFrameId() orelse return;
        self.request("evaluate", .{ .expression = self.watches.items[index].expr, .frameId = frame_id, .context = "watch" }, .{ .kind = .evaluate_watch, .epoch = self.epoch, .index = index });
    }

    /// Debug Console input: echoed, then evaluated in the current frame.
    pub fn evaluateRepl(self: *Session, expr: []const u8) void {
        if (expr.len == 0) return;
        self.addOutput(.input, expr);
        if (!self.isLive()) return self.addOutput(.console, "There is no debug session to evaluate in.");
        if (self.currentFrameId()) |id| {
            self.request("evaluate", .{ .expression = expr, .frameId = id, .context = "repl" }, .{ .kind = .evaluate_repl });
        } else {
            self.request("evaluate", .{ .expression = expr, .context = "repl" }, .{ .kind = .evaluate_repl });
        }
    }

    pub fn addOutput(self: *Session, category: Category, text: []const u8) void {
        var it = std.mem.splitScalar(u8, text, '\n');
        while (it.next()) |piece| {
            if (it.peek() == null and piece.len == 0 and text.len > 0) break;
            const line = std.mem.trimEnd(u8, piece, "\r");
            const owned = self.gpa.dupe(u8, line) catch return;
            self.output.append(self.gpa, .{ .category = category, .text = owned }) catch {
                self.gpa.free(owned);
                return;
            };
            self.output_seq +%= 1;
        }
        if (self.output.items.len > max_output_lines + 500) {
            for (self.output.items[0..500]) |l| self.gpa.free(l.text);
            const rest = self.output.items.len - 500;
            std.mem.copyForwards(OutputLine, self.output.items[0..rest], self.output.items[500..]);
            self.output.shrinkRetainingCapacity(rest);
        }
    }

    pub fn clearOutput(self: *Session) void {
        for (self.output.items) |l| self.gpa.free(l.text);
        self.output.clearRetainingCapacity();
        self.output_seq +%= 1;
        self.notify(.changed);
    }

    fn setError(self: *Session, text: ?[]const u8) void {
        if (self.error_text) |e| self.gpa.free(e);
        self.error_text = if (text) |t| self.gpa.dupe(u8, t) catch null else null;
    }

    fn resetStop(self: *Session) void {
        self.epoch +%= 1;
        _ = self.stop_arena.reset(.retain_capacity);
        self.threads = &.{};
        self.frames = &.{};
        self.scopes = &.{};
        self.variables = .empty;
        self.frame_index = 0;
        self.stop_reason = "";
        self.stop_text = "";
    }

    // ---- Incoming ------------------------------------------------------------------

    pub fn handleMessage(self: *Session, msg: Value) void {
        const kind = str(msg, "type") orelse return;
        if (std.mem.eql(u8, kind, "response")) {
            self.handleResponse(msg);
        } else if (std.mem.eql(u8, kind, "event")) {
            self.handleEvent(msg);
        } else if (std.mem.eql(u8, kind, "request")) {
            self.refuseRequest(msg);
        }
    }

    /// A request from the adapter (runInTerminal, startDebugging): none is supported.
    fn refuseRequest(self: *Session, msg: Value) void {
        const t = self.transport orelse return;
        const id = int(msg, "seq") orelse return;
        const command = str(msg, "command") orelse "";
        self.seq += 1;
        const body = std.json.Stringify.valueAlloc(self.gpa, .{
            .seq = self.seq,
            .type = "response",
            .request_seq = id,
            .success = false,
            .command = command,
            .message = "not supported by this client",
        }, .{}) catch return;
        defer self.gpa.free(body);
        t.write(t.ctx, body);
    }

    fn handleResponse(self: *Session, msg: Value) void {
        const id = int(msg, "request_seq") orelse return;
        const entry = self.pending.fetchRemove(id) orelse return;
        const p = entry.value;
        defer self.dropPending(p);
        const ok = boolean(msg, "success");
        const body = field(msg, "body");
        if (!ok) {
            const message = str(msg, "message") orelse "request failed";
            self.requestFailed(p, message, body);
            return;
        }
        switch (p.kind) {
            .initialize => {
                if (body) |b| if (field(b, "supportsConfigurationDoneRequest")) |v| {
                    self.supports_config_done = v == .bool and v.bool;
                };
                const args = self.start_arguments orelse "{}";
                self.requestRaw(self.start_command, args, .{ .kind = .launch });
            },
            .launch => {},
            .config_done => {
                if (self.state == .starting) self.state = .running;
                self.notify(.changed);
            },
            .set_breakpoints => self.breakpointsSet(p, body),
            .threads => self.threadsListed(p, body),
            .stack_trace => self.stackTraced(p, body),
            .scopes => self.scopesListed(p, body),
            .variables => self.variablesListed(p, body),
            .evaluate_watch => self.watchEvaluated(p, body),
            .evaluate_repl => {
                const text = if (body) |b| str(b, "result") orelse "" else "";
                self.addOutput(.result, text);
                self.notify(.changed);
            },
            .disconnect => self.finish(),
            .other => {},
        }
    }

    fn requestFailed(self: *Session, p: Pending, message: []const u8, body: ?Value) void {
        _ = body;
        switch (p.kind) {
            .launch => {
                self.setError(message);
                self.addOutput(.important, message);
                self.stop();
                self.finish();
            },
            .set_breakpoints => {
                if (p.path) |path| if (p.lines) |lines| for (lines) |line| self.store.setVerdict(path, line, false, message);
                self.notify(.changed);
            },
            .evaluate_watch => {
                if (p.epoch == self.epoch and p.index < self.watches.items.len) {
                    const w = &self.watches.items[p.index];
                    freeWatchResult(self.gpa, w);
                    w.value = self.gpa.dupe(u8, message) catch null;
                    w.is_error = true;
                    self.notify(.changed);
                }
            },
            .evaluate_repl => {
                self.addOutput(.stderr, message);
                self.notify(.changed);
            },
            .variables => {
                if (p.epoch == self.epoch) {
                    const arena = self.stop_arena.allocator();
                    const text = arena.dupe(u8, message) catch "";
                    self.variables.put(arena, p.ref, .{ .failed = text }) catch {};
                    self.notify(.changed);
                }
            },
            .disconnect => self.finish(),
            else => {
                self.addOutput(.stderr, message);
                self.notify(.changed);
            },
        }
    }

    fn breakpointsSet(self: *Session, p: Pending, body: ?Value) void {
        const path = p.path orelse return;
        const lines = p.lines orelse return;
        const list = if (body) |b| field(b, "breakpoints") else null;
        for (lines, 0..) |line, i| {
            var verified = false;
            var message: ?[]const u8 = null;
            if (list) |l| if (l == .array and i < l.array.items.len) {
                const item = l.array.items[i];
                verified = boolean(item, "verified");
                message = str(item, "message");
            };
            self.store.setVerdict(path, line, verified, message);
        }
        self.notify(.changed);
    }

    fn threadsListed(self: *Session, p: Pending, body: ?Value) void {
        _ = p;
        const list = (if (body) |b| field(b, "threads") else null) orelse return;
        if (list != .array) return;
        const arena = self.stop_arena.allocator();
        var out: std.ArrayList(Thread) = .empty;
        for (list.array.items) |t| {
            out.append(arena, .{
                .id = int(t, "id") orelse continue,
                .name = arena.dupe(u8, str(t, "name") orelse "") catch continue,
            }) catch {};
        }
        self.threads = out.toOwnedSlice(arena) catch &.{};
        self.notify(.changed);
    }

    fn stackTraced(self: *Session, p: Pending, body: ?Value) void {
        if (p.epoch != self.epoch or self.state != .stopped) return;
        const list = (if (body) |b| field(b, "stackFrames") else null) orelse return;
        if (list != .array) return;
        const arena = self.stop_arena.allocator();
        var out: std.ArrayList(Frame) = .empty;
        for (list.array.items) |f| {
            const source = field(f, "source");
            const path: ?[]const u8 = if (source) |s| (if (str(s, "path")) |sp| arena.dupe(u8, sp) catch null else null) else null;
            out.append(arena, .{
                .id = int(f, "id") orelse continue,
                .name = arena.dupe(u8, str(f, "name") orelse "") catch continue,
                .path = path,
                .line = @intCast(@max(0, int(f, "line") orelse 0)),
                .column = @intCast(@max(0, int(f, "column") orelse 0)),
            }) catch {};
        }
        self.frames = out.toOwnedSlice(arena) catch &.{};
        self.frame_index = 0;
        if (self.frames.len > 0) {
            const f = self.frames[0];
            self.notify(.{ .reveal = .{ .path = f.path, .line = f.line } });
            self.loadScopes();
            self.evaluateWatches();
        }
        self.notify(.changed);
    }

    fn scopesListed(self: *Session, p: Pending, body: ?Value) void {
        if (p.epoch != self.epoch or self.state != .stopped or p.index != self.frame_index) return;
        const list = (if (body) |b| field(b, "scopes") else null) orelse return;
        if (list != .array) return;
        const arena = self.stop_arena.allocator();
        var out: std.ArrayList(Scope) = .empty;
        for (list.array.items) |s| {
            out.append(arena, .{
                .name = arena.dupe(u8, str(s, "name") orelse "") catch continue,
                .reference = int(s, "variablesReference") orelse 0,
                .expensive = boolean(s, "expensive"),
            }) catch {};
        }
        self.scopes = out.toOwnedSlice(arena) catch &.{};
        // The scopes that are cheap to read are read at once, the first
        // (the locals) always.
        for (self.scopes, 0..) |s, i| {
            if (s.reference != 0 and (i == 0 or !s.expensive)) self.expand(s.reference);
        }
        self.notify(.changed);
    }

    fn variablesListed(self: *Session, p: Pending, body: ?Value) void {
        if (p.epoch != self.epoch or self.state != .stopped) return;
        const list = (if (body) |b| field(b, "variables") else null) orelse return;
        if (list != .array) return;
        const arena = self.stop_arena.allocator();
        var out: std.ArrayList(Variable) = .empty;
        for (list.array.items) |v| {
            out.append(arena, .{
                .name = arena.dupe(u8, str(v, "name") orelse "") catch continue,
                .value = arena.dupe(u8, str(v, "value") orelse "") catch continue,
                .type_name = arena.dupe(u8, str(v, "type") orelse "") catch continue,
                .reference = int(v, "variablesReference") orelse 0,
            }) catch {};
        }
        const items = out.toOwnedSlice(arena) catch return;
        self.variables.put(arena, p.ref, .{ .items = items }) catch return;
        self.notify(.changed);
    }

    fn watchEvaluated(self: *Session, p: Pending, body: ?Value) void {
        if (p.epoch != self.epoch or p.index >= self.watches.items.len) return;
        const w = &self.watches.items[p.index];
        freeWatchResult(self.gpa, w);
        w.is_error = false;
        if (body) |b| {
            w.value = self.gpa.dupe(u8, str(b, "result") orelse "") catch null;
            if (str(b, "type")) |t| w.type_name = self.gpa.dupe(u8, t) catch null;
            w.reference = int(b, "variablesReference") orelse 0;
        }
        self.notify(.changed);
    }

    fn handleEvent(self: *Session, msg: Value) void {
        const name = str(msg, "event") orelse return;
        const body = field(msg, "body");
        const eql = std.mem.eql;
        if (eql(u8, name, "initialized")) {
            self.sendAllBreakpoints();
            if (self.supports_config_done) {
                self.request("configurationDone", .{}, .{ .kind = .config_done });
            } else {
                self.state = .running;
                self.notify(.changed);
            }
        } else if (eql(u8, name, "stopped")) {
            self.stopped(body);
        } else if (eql(u8, name, "continued")) {
            if (self.state == .stopped) {
                self.state = .running;
                self.resetStop();
                self.notify(.changed);
            }
        } else if (eql(u8, name, "output")) {
            const b = body orelse return;
            const cat = str(b, "category") orelse "console";
            if (eql(u8, cat, "telemetry")) return;
            const category: Category = if (eql(u8, cat, "stdout")) .stdout else if (eql(u8, cat, "stderr")) .stderr else if (eql(u8, cat, "important")) .important else .console;
            self.addOutput(category, str(b, "output") orelse "");
            self.notify(.changed);
        } else if (eql(u8, name, "exited")) {
            self.exit_code = if (body) |b| int(b, "exitCode") else null;
            var buf: [64]u8 = undefined;
            const text = std.fmt.bufPrint(&buf, "The program exited with code {d}.", .{self.exit_code orelse 0}) catch "The program exited.";
            self.addOutput(.console, text);
            self.notify(.changed);
        } else if (eql(u8, name, "terminated")) {
            self.stop();
            if (self.transport == null) self.finish();
        } else if (eql(u8, name, "process")) {
            if (body) |b| self.debuggee_pid = int(b, "systemProcessId");
        } else if (eql(u8, name, "thread")) {
            if (self.state == .stopped) self.request("threads", .{}, .{ .kind = .threads, .epoch = self.epoch });
        }
    }

    fn stopped(self: *Session, body: ?Value) void {
        if (self.state == .ended or self.state == .idle) return;
        self.state = .stopped;
        self.resetStop();
        const arena = self.stop_arena.allocator();
        if (body) |b| {
            if (int(b, "threadId")) |t| self.thread_id = t;
            self.stop_reason = arena.dupe(u8, str(b, "reason") orelse "") catch "";
            self.stop_text = arena.dupe(u8, str(b, "description") orelse str(b, "text") orelse "") catch "";
        }
        self.request("threads", .{}, .{ .kind = .threads, .epoch = self.epoch });
        self.request("stackTrace", .{ .threadId = self.thread_id, .startFrame = 0, .levels = stack_levels }, .{ .kind = .stack_trace, .epoch = self.epoch });
        self.notify(.changed);
    }
};

// ---- JSON access ----------------------------------------------------------------

fn field(v: Value, key: []const u8) ?Value {
    if (v != .object) return null;
    return v.object.get(key);
}

fn str(v: Value, key: []const u8) ?[]const u8 {
    const f = field(v, key) orelse return null;
    return if (f == .string) f.string else null;
}

fn int(v: Value, key: []const u8) ?i64 {
    const f = field(v, key) orelse return null;
    return switch (f) {
        .integer => |i| i,
        .float => |x| @intFromFloat(x),
        else => null,
    };
}

fn boolean(v: Value, key: []const u8) bool {
    const f = field(v, key) orelse return false;
    return f == .bool and f.bool;
}

// ---- Tests: a scripted fake adapter ------------------------------------------------

const testing = std.testing;

const Fake = struct {
    gpa: Allocator,
    sent: std.ArrayList([]u8) = .empty,
    events: std.ArrayList(std.meta.Tag(Event)) = .empty,
    reveal_path: ?[]u8 = null,
    reveal_line: u32 = 0,
    session: Session = undefined,
    store: breakpoints.Store,

    fn create(gpa: Allocator) !*Fake {
        const f = try gpa.create(Fake);
        f.* = .{ .gpa = gpa, .store = .init(gpa) };
        f.session = .init(gpa, &f.store, .{ .ctx = f, .notify = onEvent });
        return f;
    }

    fn destroy(self: *Fake) void {
        self.session.deinit();
        self.store.deinit();
        for (self.sent.items) |s| self.gpa.free(s);
        self.sent.deinit(self.gpa);
        self.events.deinit(self.gpa);
        if (self.reveal_path) |p| self.gpa.free(p);
        self.gpa.destroy(self);
    }

    fn onEvent(ctx: *anyopaque, e: Event) void {
        const self: *Fake = @ptrCast(@alignCast(ctx));
        self.events.append(self.gpa, e) catch {};
        if (e == .reveal) {
            if (self.reveal_path) |p| self.gpa.free(p);
            self.reveal_path = if (e.reveal.path) |p| self.gpa.dupe(u8, p) catch null else null;
            self.reveal_line = e.reveal.line;
        }
    }

    fn write(ctx: *anyopaque, body: []const u8) void {
        const self: *Fake = @ptrCast(@alignCast(ctx));
        self.sent.append(self.gpa, self.gpa.dupe(u8, body) catch return) catch {};
    }

    fn transport(self: *Fake) Transport {
        return .{ .ctx = self, .write = write };
    }

    /// Feeds the adapter's message to the session.
    fn feed(self: *Fake, json: []const u8) !void {
        const parsed = try std.json.parseFromSlice(Value, self.gpa, json, .{});
        defer parsed.deinit();
        self.session.handleMessage(parsed.value);
    }

    /// The seq of the newest request to `command`.
    fn seqOf(self: *Fake, command: []const u8) !i64 {
        var i = self.sent.items.len;
        while (i > 0) {
            i -= 1;
            const parsed = try std.json.parseFromSlice(Value, self.gpa, self.sent.items[i], .{});
            defer parsed.deinit();
            if (str(parsed.value, "command")) |c| if (std.mem.eql(u8, c, command)) return int(parsed.value, "seq").?;
        }
        return error.NotSent;
    }

    fn sentCount(self: *Fake, command: []const u8) usize {
        var n: usize = 0;
        for (self.sent.items) |s| {
            const needle = std.fmt.allocPrint(self.gpa, "\"command\":\"{s}\"", .{command}) catch return n;
            defer self.gpa.free(needle);
            if (std.mem.indexOf(u8, s, needle) != null) n += 1;
        }
        return n;
    }

    /// Answers the newest request to `command` with `body_json`.
    fn answer(self: *Fake, command: []const u8, body_json: []const u8) !void {
        const seq = try self.seqOf(command);
        const text = try std.fmt.allocPrint(self.gpa, "{{\"type\":\"response\",\"request_seq\":{d},\"success\":true,\"command\":\"{s}\",\"body\":{s}}}", .{ seq, command, body_json });
        defer self.gpa.free(text);
        try self.feed(text);
    }

    fn fail(self: *Fake, command: []const u8, message: []const u8) !void {
        const seq = try self.seqOf(command);
        const text = try std.fmt.allocPrint(self.gpa, "{{\"type\":\"response\",\"request_seq\":{d},\"success\":false,\"command\":\"{s}\",\"message\":\"{s}\"}}", .{ seq, command, message });
        defer self.gpa.free(text);
        try self.feed(text);
    }

    /// A session through the handshake, with a breakpoint set, running.
    fn started(gpa: Allocator) !*Fake {
        const f = try create(gpa);
        errdefer f.destroy();
        _ = try f.store.toggle("/p/main.c", 7);
        try f.session.begin(f.transport(), "launch", "{\"program\":\"/p/a.out\"}");
        try f.answer("initialize", "{\"supportsConfigurationDoneRequest\":true}");
        try f.feed("{\"type\":\"event\",\"event\":\"initialized\"}");
        try f.answer("setBreakpoints", "{\"breakpoints\":[{\"verified\":true,\"line\":7}]}");
        try f.answer("configurationDone", "{}");
        return f;
    }

    /// Stops at /p/main.c:7 with two frames and two locals.
    fn stoppedAtBreakpoint(self: *Fake) !void {
        try self.feed("{\"type\":\"event\",\"event\":\"stopped\",\"body\":{\"reason\":\"breakpoint\",\"threadId\":5}}");
        try self.answer("threads", "{\"threads\":[{\"id\":5,\"name\":\"main\"}]}");
        try self.answer("stackTrace",
            \\{"stackFrames":[
            \\ {"id":100,"name":"compute","source":{"path":"/p/main.c"},"line":7,"column":3},
            \\ {"id":101,"name":"main","source":{"path":"/p/main.c"},"line":20,"column":1}]}
        );
        try self.answer("scopes", "{\"scopes\":[{\"name\":\"Locals\",\"variablesReference\":11,\"expensive\":false},{\"name\":\"Registers\",\"variablesReference\":12,\"expensive\":true}]}");
        try self.answer("variables",
            \\{"variables":[
            \\ {"name":"x","value":"42","type":"int","variablesReference":0},
            \\ {"name":"item","value":"{...}","type":"Item","variablesReference":13}]}
        );
    }
};

test "the handshake sends launch, then the breakpoints, then configurationDone" {
    const f = try Fake.create(testing.allocator);
    defer f.destroy();
    _ = try f.store.toggle("/p/main.c", 7);
    _ = try f.store.toggle("/p/main.c", 12);
    try f.session.begin(f.transport(), "launch", "{\"program\":\"/p/a.out\"}");
    try testing.expectEqual(State.starting, f.session.state);
    try testing.expectEqual(@as(usize, 1), f.sentCount("initialize"));
    try testing.expectEqual(@as(usize, 0), f.sentCount("launch"));

    try f.answer("initialize", "{\"supportsConfigurationDoneRequest\":true}");
    try testing.expectEqual(@as(usize, 1), f.sentCount("launch"));
    const launch = f.sent.items[f.sent.items.len - 1];
    try testing.expect(std.mem.indexOf(u8, launch, "\"arguments\":{\"program\":\"/p/a.out\"}") != null);
    try testing.expectEqual(@as(usize, 0), f.sentCount("setBreakpoints"));

    try f.feed("{\"type\":\"event\",\"event\":\"initialized\"}");
    try testing.expectEqual(@as(usize, 1), f.sentCount("setBreakpoints"));
    try testing.expectEqual(@as(usize, 1), f.sentCount("configurationDone"));
    const bp_msg = f.sent.items[f.sent.items.len - 2];
    try testing.expect(std.mem.indexOf(u8, bp_msg, "\"path\":\"/p/main.c\"") != null);
    try testing.expect(std.mem.indexOf(u8, bp_msg, "{\"line\":7},{\"line\":12}") != null);

    try f.answer("setBreakpoints", "{\"breakpoints\":[{\"verified\":true,\"line\":7},{\"verified\":false,\"line\":12,\"message\":\"no code\"}]}");
    try f.answer("configurationDone", "{}");
    try testing.expectEqual(State.running, f.session.state);
    try testing.expect(f.store.get("/p/main.c", 7).?.verified);
    try testing.expect(!f.store.get("/p/main.c", 12).?.verified);
    try testing.expectEqualStrings("no code", f.store.get("/p/main.c", 12).?.message.?);
}

test "a stop loads threads, frames, scopes and locals and reveals the top frame" {
    const f = try Fake.started(testing.allocator);
    defer f.destroy();
    try f.stoppedAtBreakpoint();
    const s = &f.session;
    try testing.expectEqual(State.stopped, s.state);
    try testing.expectEqualStrings("breakpoint", s.stop_reason);
    try testing.expectEqual(@as(i64, 5), s.thread_id);
    try testing.expectEqual(@as(usize, 1), s.threads.len);
    try testing.expectEqual(@as(usize, 2), s.frames.len);
    try testing.expectEqualStrings("compute", s.frames[0].name);
    try testing.expectEqualStrings("/p/main.c", f.reveal_path.?);
    try testing.expectEqual(@as(u32, 7), f.reveal_line);
    try testing.expectEqual(@as(usize, 2), s.scopes.len);
    // The locals are read at once; the expensive scope waits to be opened.
    try testing.expectEqual(@as(usize, 1), f.sentCount("variables"));
    const locals = s.childrenOf(11).?.items;
    try testing.expectEqual(@as(usize, 2), locals.len);
    try testing.expectEqualStrings("42", locals[0].value);
    try testing.expectEqualStrings("int", locals[0].type_name);
    try testing.expectEqual(@as(i64, 13), locals[1].reference);
}

test "expanding a variable asks once; a failure shows the adapter's message" {
    const f = try Fake.started(testing.allocator);
    defer f.destroy();
    try f.stoppedAtBreakpoint();
    const s = &f.session;
    s.expand(13);
    s.expand(13);
    try testing.expectEqual(@as(usize, 2), f.sentCount("variables"));
    try testing.expect(s.childrenOf(13).? == .loading);
    try f.answer("variables", "{\"variables\":[{\"name\":\"Name\",\"value\":\"\\\"widget\\\"\",\"type\":\"string\",\"variablesReference\":0}]}");
    try testing.expectEqualStrings("Name", s.childrenOf(13).?.items[0].name);
    s.expand(12);
    try f.fail("variables", "cannot read registers");
    try testing.expectEqualStrings("cannot read registers", s.childrenOf(12).?.failed);
}

test "stepping clears the stop, and a late answer for it is ignored" {
    const f = try Fake.started(testing.allocator);
    defer f.destroy();
    try f.stoppedAtBreakpoint();
    const s = &f.session;
    s.stepOver();
    try testing.expectEqual(State.running, s.state);
    try testing.expectEqual(@as(usize, 0), s.frames.len);
    try testing.expectEqual(@as(usize, 1), f.sentCount("next"));
    // The step's answer is an ordinary success.
    try f.answer("next", "{}");

    // A stop's stackTrace that arrives after the program resumed again.
    try f.feed("{\"type\":\"event\",\"event\":\"stopped\",\"body\":{\"reason\":\"step\",\"threadId\":5}}");
    const old_trace = try f.seqOf("stackTrace");
    s.continue_();
    const text = try std.fmt.allocPrint(testing.allocator, "{{\"type\":\"response\",\"request_seq\":{d},\"success\":true,\"command\":\"stackTrace\",\"body\":{{\"stackFrames\":[{{\"id\":1,\"name\":\"f\",\"line\":1,\"column\":1}}]}}}}", .{old_trace});
    defer testing.allocator.free(text);
    try f.feed(text);
    try testing.expectEqual(@as(usize, 0), s.frames.len);
    try testing.expectEqual(State.running, s.state);
}

test "stepping while the program runs sends nothing" {
    const f = try Fake.started(testing.allocator);
    defer f.destroy();
    f.session.stepInto();
    f.session.continue_();
    try testing.expectEqual(@as(usize, 0), f.sentCount("stepIn"));
    try testing.expectEqual(@as(usize, 0), f.sentCount("continue"));
}

test "picking another frame reloads scopes and reveals its line" {
    const f = try Fake.started(testing.allocator);
    defer f.destroy();
    try f.stoppedAtBreakpoint();
    const s = &f.session;
    s.selectFrame(1);
    try testing.expectEqual(@as(u32, 20), f.reveal_line);
    try testing.expectEqual(@as(usize, 2), f.sentCount("scopes"));
    try f.answer("scopes", "{\"scopes\":[{\"name\":\"Locals\",\"variablesReference\":21,\"expensive\":false}]}");
    try f.answer("variables", "{\"variables\":[{\"name\":\"argc\",\"value\":\"1\",\"type\":\"int\",\"variablesReference\":0}]}");
    try testing.expectEqualStrings("argc", s.childrenOf(21).?.items[0].name);
    try testing.expect(s.childrenOf(11) == null);
}

test "watches evaluate in the current frame at each stop" {
    const f = try Fake.started(testing.allocator);
    defer f.destroy();
    const s = &f.session;
    try s.addWatch("x * 2");
    try testing.expectEqual(@as(usize, 0), f.sentCount("evaluate"));
    try f.stoppedAtBreakpoint();
    try testing.expectEqual(@as(usize, 1), f.sentCount("evaluate"));
    try f.answer("evaluate", "{\"result\":\"84\",\"type\":\"int\",\"variablesReference\":0}");
    try testing.expectEqualStrings("84", s.watches.items[0].value.?);
    try testing.expectEqualStrings("int", s.watches.items[0].type_name.?);
    s.stepOver();
    try f.feed("{\"type\":\"event\",\"event\":\"stopped\",\"body\":{\"reason\":\"step\",\"threadId\":5}}");
    try f.answer("stackTrace", "{\"stackFrames\":[{\"id\":200,\"name\":\"compute\",\"source\":{\"path\":\"/p/main.c\"},\"line\":8,\"column\":1}]}");
    try testing.expectEqual(@as(usize, 2), f.sentCount("evaluate"));
    try f.fail("evaluate", "undeclared identifier 'x'");
    try testing.expect(s.watches.items[0].is_error);
    try testing.expectEqualStrings("undeclared identifier 'x'", s.watches.items[0].value.?);
}

test "output events fill the console by category and telemetry is dropped" {
    const f = try Fake.started(testing.allocator);
    defer f.destroy();
    const s = &f.session;
    const before = s.output.items.len;
    try f.feed("{\"type\":\"event\",\"event\":\"output\",\"body\":{\"category\":\"stdout\",\"output\":\"hello\\nworld\\n\"}}");
    try f.feed("{\"type\":\"event\",\"event\":\"output\",\"body\":{\"category\":\"telemetry\",\"output\":\"x\"}}");
    try f.feed("{\"type\":\"event\",\"event\":\"output\",\"body\":{\"category\":\"stderr\",\"output\":\"oops\\n\"}}");
    try testing.expectEqual(before + 3, s.output.items.len);
    try testing.expectEqual(Category.stdout, s.output.items[before].category);
    try testing.expectEqualStrings("world", s.output.items[before + 1].text);
    try testing.expectEqual(Category.stderr, s.output.items[before + 2].category);
}

test "Debug Console input is echoed and its result printed" {
    const f = try Fake.started(testing.allocator);
    defer f.destroy();
    try f.stoppedAtBreakpoint();
    const s = &f.session;
    s.evaluateRepl("x + 1");
    try f.answer("evaluate", "{\"result\":\"43\",\"variablesReference\":0}");
    const out = s.output.items;
    try testing.expectEqual(Category.input, out[out.len - 2].category);
    try testing.expectEqualStrings("x + 1", out[out.len - 2].text);
    try testing.expectEqual(Category.result, out[out.len - 1].category);
    try testing.expectEqualStrings("43", out[out.len - 1].text);
}

test "a request from the adapter is refused, not left waiting" {
    const f = try Fake.started(testing.allocator);
    defer f.destroy();
    try f.feed("{\"type\":\"request\",\"seq\":9,\"command\":\"runInTerminal\",\"arguments\":{}}");
    const reply = f.sent.items[f.sent.items.len - 1];
    try testing.expect(std.mem.indexOf(u8, reply, "\"request_seq\":9") != null);
    try testing.expect(std.mem.indexOf(u8, reply, "\"success\":false") != null);
}

test "a refused launch ends the session with the adapter's reason" {
    const f = try Fake.create(testing.allocator);
    defer f.destroy();
    try f.session.begin(f.transport(), "launch", "{}");
    try f.answer("initialize", "{}");
    try f.fail("launch", "program does not exist");
    try testing.expectEqual(State.ended, f.session.state);
    try testing.expectEqualStrings("program does not exist", f.session.error_text.?);
}

test "stop sends disconnect and the answer ends the session" {
    const f = try Fake.started(testing.allocator);
    defer f.destroy();
    f.session.stop();
    try testing.expectEqual(@as(usize, 1), f.sentCount("disconnect"));
    f.session.stop();
    try testing.expectEqual(@as(usize, 1), f.sentCount("disconnect"));
    try f.answer("disconnect", "{}");
    try testing.expectEqual(State.ended, f.session.state);
    try testing.expect(!f.session.isLive());
}

test "the adapter dying ends a running session once" {
    const f = try Fake.started(testing.allocator);
    defer f.destroy();
    f.session.adapterExited();
    f.session.adapterExited();
    try testing.expectEqual(State.ended, f.session.state);
    var ended: usize = 0;
    for (f.events.items) |e| if (e == .ended) {
        ended += 1;
    };
    try testing.expectEqual(@as(usize, 1), ended);
}

test "breakpoints toggled during a run are sent for that file only" {
    const f = try Fake.started(testing.allocator);
    defer f.destroy();
    _ = try f.store.toggle("/p/other.c", 3);
    const before = f.sentCount("setBreakpoints");
    f.session.sendBreakpoints("/p/other.c");
    try testing.expectEqual(before + 1, f.sentCount("setBreakpoints"));
    const msg = f.sent.items[f.sent.items.len - 1];
    try testing.expect(std.mem.indexOf(u8, msg, "other.c") != null);
    try testing.expect(std.mem.indexOf(u8, msg, "main.c") == null);
}

test "a disabled breakpoint is not sent" {
    const f = try Fake.started(testing.allocator);
    defer f.destroy();
    f.store.setEnabled("/p/main.c", 7, false);
    f.session.sendBreakpoints("/p/main.c");
    const msg = f.sent.items[f.sent.items.len - 1];
    try testing.expect(std.mem.indexOf(u8, msg, "\"breakpoints\":[]") != null);
}
