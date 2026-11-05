//! Memory and CPU of a process, as the kernel counts them: macOS's
//! proc_pid_rusage. `resident` is the pages in RAM, `footprint` is what
//! Activity Monitor calls Memory (compressed pages count, shared and
//! file-backed ones that were never touched do not) and `peak_footprint` is
//! its high-water mark for the life of the process.

const std = @import("std");
const builtin = @import("builtin");

const macos = builtin.os.tag.isDarwin();

pub const Usage = struct {
    resident: u64,
    footprint: u64,
    peak_footprint: u64,
    /// User plus system CPU time of the process's threads.
    cpu_ms: u64,
};

/// macOS's rusage_info_v4.
const RusageInfoV4 = extern struct {
    uuid: [16]u8,
    user_time: u64,
    system_time: u64,
    pkg_idle_wkups: u64,
    interrupt_wkups: u64,
    pageins: u64,
    wired_size: u64,
    resident_size: u64,
    phys_footprint: u64,
    proc_start_abstime: u64,
    proc_exit_abstime: u64,
    child_user_time: u64,
    child_system_time: u64,
    child_pkg_idle_wkups: u64,
    child_interrupt_wkups: u64,
    child_pageins: u64,
    child_elapsed_abstime: u64,
    diskio_bytesread: u64,
    diskio_byteswritten: u64,
    cpu_time_qos: [7]u64,
    billed_system_time: u64,
    serviced_system_time: u64,
    logical_writes: u64,
    lifetime_max_phys_footprint: u64,
    instructions: u64,
    cycles: u64,
    billed_energy: u64,
    serviced_energy: u64,
    interval_max_phys_footprint: u64,
    runnable_time: u64,
};

const MachTimebase = extern struct { numer: u32, denom: u32 };

extern "c" fn proc_pid_rusage(pid: c_int, flavor: c_int, buffer: *RusageInfoV4) c_int;
extern "c" fn mach_timebase_info(info: *MachTimebase) c_int;

const rusage_info_v4 = 4;

/// The usage of `pid`, or null once it has exited (and always off macOS).
pub fn ofPid(pid: i32) ?Usage {
    if (!macos) return null;
    var info: RusageInfoV4 = undefined;
    if (proc_pid_rusage(pid, rusage_info_v4, &info) != 0) return null;
    // CPU times come in mach absolute time units (1 ns on Intel, 125/3 ns on Apple silicon).
    var tb: MachTimebase = .{ .numer = 1, .denom = 1 };
    _ = mach_timebase_info(&tb);
    const ticks = info.user_time + info.system_time;
    const ns = @as(u128, ticks) * tb.numer / @max(tb.denom, 1);
    return .{
        .resident = info.resident_size,
        .footprint = info.phys_footprint,
        .peak_footprint = info.lifetime_max_phys_footprint,
        .cpu_ms = @intCast(ns / std.time.ns_per_ms),
    };
}

/// This process's usage.
pub fn current() ?Usage {
    return ofPid(std.c.getpid());
}

comptime {
    // rusage_info_v4 in <sys/resource.h>: 16 bytes of uuid and 35 counters.
    std.debug.assert(@sizeOf(RusageInfoV4) == 16 + 35 * 8);
}

test "this process has a resident size" {
    if (!macos) return error.SkipZigTest;
    const u = current().?;
    try std.testing.expect(u.resident > 1 << 20);
    try std.testing.expect(u.peak_footprint >= u.footprint);
}
