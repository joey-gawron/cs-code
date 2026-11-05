//! Background work with results delivered on the main thread (a tiny
//! version of GPUI's background/foreground executors). Workers run on
//! detached threads; completions are queued and the event loop is woken so
//! it can `drain` them.

const std = @import("std");
const Allocator = std.mem.Allocator;

pub const SpinLock = struct {
    locked: std.atomic.Value(bool) = .init(false),

    pub fn lock(self: *SpinLock) void {
        while (self.locked.cmpxchgWeak(false, true, .acquire, .monotonic) != null) std.atomic.spinLoopHint();
    }

    pub fn unlock(self: *SpinLock) void {
        self.locked.store(false, .release);
    }
};

pub const Completion = struct {
    ctx: *anyopaque,
    run: *const fn (ctx: *anyopaque) void,
};

pub const Executor = struct {
    gpa: Allocator,
    lock: SpinLock = .{},
    pending: std.ArrayList(Completion) = .empty,
    /// `drain` has taken the completions before this one.
    head: usize = 0,
    waker_ctx: ?*anyopaque = null,
    waker: ?*const fn (ctx: *anyopaque) void = null,
    /// Number of background tasks still running.
    active: std.atomic.Value(u32) = .init(0),
    /// The main thread (the one that drains): the one that made the executor.
    main: std.Thread.Id,

    pub fn init(gpa: Allocator) Executor {
        return .{ .gpa = gpa, .main = std.Thread.getCurrentId() };
    }

    pub fn deinit(self: *Executor) void {
        self.pending.deinit(self.gpa);
    }

    /// Runs `function(args...)` on a new thread.
    pub fn spawn(self: *Executor, comptime function: anytype, args: anytype) !void {
        _ = self.active.fetchAdd(1, .monotonic);
        const thread = std.Thread.spawn(.{}, function, args) catch |err| {
            _ = self.active.fetchSub(1, .monotonic);
            return err;
        };
        thread.detach();
    }

    /// Called by background tasks when they finish, as their last touch of
    /// the executor and the app: once `active` is 0, both can go. A task may
    /// still store to its own job after it (a flag its owner waits for).
    pub fn taskDone(self: *Executor) void {
        _ = self.active.fetchSub(1, .release);
    }

    /// Thread-safe: schedule `completion` to run on the main thread.
    pub fn post(self: *Executor, completion: Completion) void {
        while (true) {
            self.lock.lock();
            const queued = if (self.pending.append(self.gpa, completion)) true else |_| false;
            self.lock.unlock();
            if (queued) break;
            // Out of memory. Owners wait for their workers' completions, so a
            // worker tries again once some is freed. The main thread can't
            // wait for itself, and nothing waits for what it posts (wake-ups,
            // connection events): drop it, as before.
            if (std.Thread.getCurrentId() == self.main) break;
            std.Thread.yield() catch {};
        }
        if (self.waker) |w| w(self.waker_ctx.?);
    }

    /// Main thread: drops queued copies of `completion` (its owner is going away).
    pub fn cancel(self: *Executor, completion: Completion) void {
        _ = self.tryCancel(completion);
    }

    /// Main thread: like `cancel`, and says whether a queued copy was
    /// dropped (false: it already ran, or is running).
    pub fn tryCancel(self: *Executor, completion: Completion) bool {
        self.lock.lock();
        defer self.lock.unlock();
        const before = self.pending.items.len;
        var i: usize = self.head;
        while (i < self.pending.items.len) {
            const p = self.pending.items[i];
            if (p.ctx == completion.ctx and p.run == completion.run) _ = self.pending.orderedRemove(i) else i += 1;
        }
        return self.pending.items.len < before;
    }

    /// Main thread: run queued completions. They are taken one at a time,
    /// so one that drains while it waits for another (a project waiting
    /// for its scan) runs those queued after it too.
    pub fn drain(self: *Executor) void {
        while (true) {
            self.lock.lock();
            if (self.head == self.pending.items.len) {
                self.pending.clearRetainingCapacity();
                self.head = 0;
                self.lock.unlock();
                return;
            }
            const c = self.pending.items[self.head];
            self.head += 1;
            self.lock.unlock();
            c.run(c.ctx);
        }
    }
};

test "a completion can wait for one queued after it" {
    // Project.destroy, run from a completion (opening a remote folder),
    // drains until its scan's completion has run; that one may be queued
    // behind it in the same batch.
    const gpa = std.testing.allocator;
    var executor: Executor = .init(gpa);
    defer executor.deinit();
    const Waiter = struct {
        executor: *Executor,
        done: bool = false,
        waited: bool = false,

        fn wait(ctx: *anyopaque) void {
            const self: *@This() = @ptrCast(@alignCast(ctx));
            var tries: u32 = 0;
            while (!self.done and tries < 100) : (tries += 1) self.executor.drain();
            self.waited = self.done;
        }

        fn finish(ctx: *anyopaque) void {
            const self: *@This() = @ptrCast(@alignCast(ctx));
            self.done = true;
        }
    };
    var w: Waiter = .{ .executor = &executor };
    executor.post(.{ .ctx = &w, .run = Waiter.wait });
    executor.post(.{ .ctx = &w, .run = Waiter.finish });
    executor.drain();
    try std.testing.expect(w.waited);
}

test "out of memory, a worker's post waits and the main thread's is dropped" {
    // An owner waits for its worker's completion, so that one can't be lost;
    // the main thread can't wait for memory (here only it would free some).
    var failing: std.testing.FailingAllocator = .init(std.testing.allocator, .{ .fail_index = 0 });
    var executor: Executor = .init(failing.allocator());
    defer executor.deinit();
    const Flag = struct {
        fn set(ctx: *anyopaque) void {
            const done: *bool = @ptrCast(@alignCast(ctx));
            done.* = true;
        }

        fn post(e: *Executor, done: *bool) void {
            e.post(.{ .ctx = done, .run = set });
        }
    };
    var worker_done = false;
    var main_done = false;
    const thread = try std.Thread.spawn(.{}, Flag.post, .{ &executor, &worker_done });
    // `post` uses the allocator under the lock only. Wait for the worker to fail.
    while (true) : (std.Thread.yield() catch {}) {
        executor.lock.lock();
        defer executor.lock.unlock();
        if (failing.has_induced_failure) break;
    }
    executor.post(.{ .ctx = &main_done, .run = Flag.set });
    {
        executor.lock.lock();
        defer executor.lock.unlock();
        failing.fail_index = std.math.maxInt(usize);
    }
    thread.join();
    executor.drain();
    try std.testing.expect(worker_done);
    try std.testing.expect(!main_done);
}
