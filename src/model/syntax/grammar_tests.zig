//! Every bundled grammar highlights a sample of its language: key tokens must
//! get the colour of their role in the Visual Studio Dark scheme, so a grammar
//! or query that stops loading (or captures something else) fails here.
//!
//! The grammar farm comes from tools/bundle-grammars.sh (build.zig points the
//! tests at .grammars, or at $CS_ZIG_GRAMMARS); without it these skip.
//! CS_ZIG_DUMP_PAINT=1 prints each sample's colour runs, to write new cases.

const std = @import("std");
const Allocator = std.mem.Allocator;
const languages = @import("languages.zig");
const syntax = @import("syntax.zig");
const theme_mod = @import("../../framework/ui/theme.zig");
const Rope = @import("../text/rope.zig").Rope;
const Color = @import("../../framework/gpu/scene.zig").Color;

/// Roles the expectations name; `control`, `constant_language`, `heading` and
/// `type_primitive` fall back to the plain role in schemes that have no
/// colour of their own for them.
const Role = enum { comment, string, number, keyword, control, function, type, property, tag, attribute, constant, constant_language, escape, heading, type_primitive, operator, parameter };

/// `needle` (first occurrence after `after`, if given) is painted with the
/// scheme's colour for `role`.
const Expect = struct { needle: []const u8, role: Role, after: []const u8 = "" };

const Case = struct {
    path: []const u8,
    text: []const u8,
    expect: []const Expect,
};

fn e(needle: []const u8, role: Role) Expect {
    return .{ .needle = needle, .role = role };
}

const cases = [_]Case{
    .{ .path = "a.zig", .text =
    \\const std = @import("std");
    \\// note
    \\pub fn main() !void {
    \\    const n: u32 = 42;
    \\    if (n > 1) return error.Bad;
    \\    std.debug.print("hi\n", .{});
    \\}
    \\
    , .expect = &.{ e("// note", .comment), e("\"hi", .string), e("42", .number), e("const", .keyword), e("main", .function), e("if", .control), e("return", .control), e("error", .keyword) } },
    .{ .path = "a.c", .text =
    \\#include <stdio.h>
    \\/* c */
    \\static int add(int a, int b) { return a + b; }
    \\int main(void) { printf("%d\n", add(1, 2)); return 0; }
    \\
    , .expect = &.{ e("/* c */", .comment), e("\"%d", .string), e("int", .keyword), e("add", .function), e("1", .number), e("return", .control) } },
    .{ .path = "a.cpp", .text =
    \\#include <vector>
    \\// cpp
    \\template <typename T> class Box { public: T value; };
    \\int main() { std::vector<int> v{1, 2}; return v.size(); }
    \\
    , .expect = &.{ e("// cpp", .comment), e("class", .keyword), e("template", .keyword), e("1", .number) } },
    .{ .path = "a.cs", .text =
    \\using System;
    \\namespace Demo {
    \\  // c#
    \\  public class Program {
    \\    public static int Main(string[] args) {
    \\      var s = "hello"; foreach (var a in args) { if (a == null) continue; }
    \\      return 42;
    \\    }
    \\  }
    \\}
    \\
    , .expect = &.{ e("// c#", .comment), e("\"hello", .string), e("class", .keyword), e("Program", .type), e("42", .number), e("Main", .function), e("foreach", .control), e("if", .control), e("return", .control), e("null", .constant_language), e("int", .keyword) } },
    .{ .path = "a.rs", .text =
    \\// rust
    \\use std::fmt;
    \\struct Point { x: i32 }
    \\fn main() { let p = Point { x: 1 }; println!("{}", p.x); }
    \\
    , .expect = &.{ e("// rust", .comment), e("\"{}", .string), e("struct", .keyword), e("Point", .type), e("1", .number), e("main", .function), e("i32", .type_primitive) } },
    .{ .path = "a.py", .text =
    \\# py
    \\import os
    \\class A:
    \\    def f(self, x):
    \\        return "s" + str(42)
    \\
    , .expect = &.{ e("# py", .comment), e("\"s", .string), e("class", .keyword), e("42", .number), e("def", .keyword), e("return", .control) } },
    .{ .path = "a.js", .text =
    \\// js
    \\const x = 42;
    \\function f(a) { return `t${a}` + "s"; }
    \\
    , .expect = &.{ e("// js", .comment), e("\"s", .string), e("42", .number), e("function", .keyword), e("return", .control) } },
    .{ .path = "a.ts", .text =
    \\// ts
    \\interface P { x: number }
    \\const f = (p: P): string => "s" + p.x;
    \\
    , .expect = &.{ e("// ts", .comment), e("\"s", .string), e("interface", .keyword), e("number", .type_primitive) } },
    .{ .path = "a.tsx", .text =
    \\// tsx
    \\const A = () => <div className="c">hi</div>;
    \\
    , .expect = &.{ e("// tsx", .comment), e("\"c", .string), e("div", .tag) } },
    .{ .path = "a.json", .text =
    \\{"a": [1, true, null], "b": "s"}
    \\
    , .expect = &.{ e("\"s", .string), e("1", .number), e("\"a\"", .property), e("null", .constant_language) } },
    .{ .path = "a.yaml", .text =
    \\# yaml
    \\key: value
    \\list:
    \\  - 1
    \\  - "two"
    \\
    , .expect = &.{ e("# yaml", .comment), e("\"two", .string), e("1", .number), e("key", .property) } },
    .{ .path = "a.toml", .text =
    \\# toml
    \\[pkg]
    \\name = "x"
    \\n = 3
    \\
    , .expect = &.{ e("# toml", .comment), e("\"x", .string), e("3", .number) } },
    .{ .path = "a.sh", .text =
    \\#!/bin/bash
    \\# sh
    \\for f in *.txt; do echo "$f"; done
    \\if [ -f x ]; then exit 1; fi
    \\
    , .expect = &.{ e("# sh", .comment), e("\"$f", .string), e("for", .control), e("if", .control), e("echo", .function) } },
    .{ .path = "a.html", .text =
    \\<!-- h -->
    \\<div class="c"><p>hi</p><script>var x = 42;</script></div>
    \\
    , .expect = &.{ e("<!-- h -->", .comment), e("\"c", .string), e("div", .tag), e("class", .attribute), e("42", .number) } },
    .{ .path = "a.css", .text =
    \\/* css */
    \\.a { color: #fff; margin: 4px; }
    \\
    , .expect = &.{ e("/* css */", .comment), e("4px", .number) } },
    .{ .path = "a.go", .text =
    \\package main
    \\// go
    \\func main() { s := "x"; fmt.Println(s, 42) }
    \\
    , .expect = &.{ e("// go", .comment), e("\"x", .string), e("func", .keyword), e("42", .number) } },
    .{ .path = "a.lua", .text =
    \\-- lua
    \\local function f(a) return "s" .. 42 end
    \\
    , .expect = &.{ e("-- lua", .comment), e("\"s", .string), e("function", .keyword), e("42", .number) } },
    .{ .path = "a.java", .text =
    \\// java
    \\public class A { int f(String s) { return 42; } }
    \\
    , .expect = &.{ e("// java", .comment), e("class", .keyword), e("42", .number) } },
    .{ .path = "a.rb", .text =
    \\# ruby
    \\class A
    \\  def f(x) = "s#{x}"
    \\end
    \\
    , .expect = &.{ e("# ruby", .comment), e("class", .keyword), e("\"s", .string) } },
    .{ .path = "Makefile", .text = "# make\nall: main.o\n\tcc -o main main.o\n", .expect = &.{e("# make", .comment)} },
    .{ .path = "CMakeLists.txt", .text =
    \\# cmake
    \\project(demo)
    \\add_executable(demo "main.c")
    \\
    , .expect = &.{ e("# cmake", .comment), e("\"main", .string) } },
    .{ .path = "a.nix", .text =
    \\# nix
    \\{ pkgs }: pkgs.mkShell { name = "x"; n = 1; }
    \\
    , .expect = &.{ e("# nix", .comment), e("\"x", .string), e("1", .number) } },
    .{ .path = "a.sql", .text =
    \\-- sql
    \\SELECT id, 'x' FROM t WHERE n > 42;
    \\
    , .expect = &.{ e("-- sql", .comment), e("'x", .string), e("SELECT", .keyword), e("42", .number) } },
    .{ .path = "Dockerfile", .text =
    \\# docker
    \\FROM alpine:3.19
    \\RUN echo "hi"
    \\
    , .expect = &.{ e("# docker", .comment), e("FROM", .keyword) } },
    .{ .path = "a.xml", .text =
    \\<?xml version="1.0"?>
    \\<!-- x -->
    \\<root a="1"><item/></root>
    \\
    , .expect = &.{ e("<!-- x -->", .comment), e("root", .tag) } },
    .{ .path = "a.md", .text =
    \\# Title
    \\
    \\Some *emph* and `code` text.
    \\
    \\```zig
    \\const x = 42;
    \\```
    \\
    , .expect = &.{ e("Title", .heading), e("42", .number), e("const", .keyword) } },
};

fn schemeTheme() theme_mod.Theme {
    return theme_mod.build(theme_mod.schemeByName("Visual Studio Dark").?);
}

fn roleColor(theme: *const theme_mod.Theme, role: Role) Color {
    const sx = &theme.syntax;
    return switch (role) {
        .comment => sx.comment,
        .string => sx.string,
        .number => sx.number,
        .keyword => sx.keyword,
        .control => sx.keyword_control orelse sx.keyword,
        .function => sx.function,
        .type => sx.type,
        .property => sx.property,
        .tag => sx.tag,
        .attribute => sx.attribute,
        .constant => sx.constant,
        .constant_language => sx.constant_language orelse sx.constant,
        .escape => sx.escape,
        .heading => sx.heading orelse sx.function,
        .type_primitive => sx.type_primitive orelse sx.keyword,
        .operator => sx.operator,
        .parameter => sx.parameter,
    };
}

/// A role whose colour is `color` (the first of several sharing it).
fn roleName(theme: *const theme_mod.Theme, color: Color) []const u8 {
    inline for (@typeInfo(Role).@"enum".field_names) |name| {
        if (std.meta.eql(roleColor(theme, @field(Role, name)), color)) return name;
    }
    return "?";
}

const Paint = ?Color;

fn paintOf(gpa: Allocator, syn: *syntax.Syntax, rope: *const Rope) ![]Paint {
    var spans: std.ArrayList(syntax.HighlightSpan) = .empty;
    defer spans.deinit(gpa);
    syn.highlight(rope, 0, rope.len(), &spans);
    const paint = try gpa.alloc(Paint, rope.len());
    @memset(paint, null);
    for (spans.items) |s| @memset(paint[s.start..s.end], s.color);
    return paint;
}

fn dump(theme: *const theme_mod.Theme, text: []const u8, paint: []const Paint, path: []const u8) void {
    std.debug.print("--- {s}\n", .{path});
    var i: usize = 0;
    while (i < text.len) {
        var j = i + 1;
        while (j < text.len and std.meta.eql(paint[j], paint[i])) j += 1;
        const name = if (paint[i]) |c| roleName(theme, c) else "-";
        const run = std.mem.trim(u8, text[i..j], " \n\t");
        if (run.len > 0) std.debug.print("  {s:<18} {s}\n", .{ name, run[0..@min(run.len, 40)] });
        i = j;
    }
}

fn grammarFarmPresent(registry: *const syntax.Registry) bool {
    var buf: [std.fs.max_path_bytes]u8 = undefined;
    const z = std.fmt.bufPrintSentinel(&buf, "{s}", .{registry.grammar_dir}, 0) catch return false;
    return registry.grammar_dir.len > 0 and std.c.access(z, std.c.F_OK) == 0;
}

test "every bundled grammar colours its sample's key tokens" {
    const gpa = std.testing.allocator;
    const theme = schemeTheme();
    var registry = syntax.Registry.init(gpa, &theme);
    defer registry.deinit();
    if (!grammarFarmPresent(&registry)) return error.SkipZigTest;
    const dumping = std.c.getenv("CS_ZIG_DUMP_PAINT") != null;

    var failures: usize = 0;
    for (cases) |case| {
        const config = languages.forPath(case.path);
        const lang = registry.get(config) orelse {
            std.debug.print("grammar for {s} ({s}) did not load\n", .{ case.path, config.name });
            failures += 1;
            continue;
        };
        var rope = try Rope.fromSlice(gpa, case.text);
        defer rope.deinit(gpa);
        var syn = try syntax.Syntax.init(gpa, lang);
        defer syn.deinit();
        syn.update(&rope);
        const paint = try paintOf(gpa, &syn, &rope);
        defer gpa.free(paint);
        if (dumping) dump(&theme, case.text, paint, case.path);
        for (case.expect) |want| {
            const from = if (want.after.len > 0) (std.mem.indexOf(u8, case.text, want.after) orelse 0) else 0;
            const at = (std.mem.indexOfPos(u8, case.text, from, want.needle) orelse {
                std.debug.print("{s}: sample has no '{s}'\n", .{ case.path, want.needle });
                failures += 1;
                continue;
            });
            const want_color = roleColor(&theme, want.role);
            if (paint[at]) |got| {
                if (std.meta.eql(got, want_color)) continue;
                std.debug.print("{s}: '{s}' is {s}, want {s}\n", .{ case.path, want.needle, roleName(&theme, got), @tagName(want.role) });
            } else {
                std.debug.print("{s}: '{s}' is uncoloured, want {s}\n", .{ case.path, want.needle, @tagName(want.role) });
            }
            failures += 1;
        }
    }
    try std.testing.expectEqual(0, failures);
}
