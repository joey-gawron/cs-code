//! Root of the cs-zig-server executable; the code is in server/. It lives
//! here (not in server/) so it can import the model layer it shares with the
//! editor. `zig build layers` keeps it to framework + model imports.

const server = @import("server/main.zig");

pub const main = server.main;
pub const std_options = server.std_options;

test {
    _ = @import("server/tests.zig");
}
