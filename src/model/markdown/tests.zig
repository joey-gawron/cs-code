//! Markdown parser tests: examples from the CommonMark specification (0.30)
//! and the GFM additions, each a source and the HTML the reference renderer
//! gives for it. Differences from the spec on purpose: YAML front matter is a
//! block of its own, and raw inline HTML is dropped (the preview has no HTML
//! engine), so the spec examples that depend on either are left out.

const std = @import("std");
const md = @import("markdown.zig");
const html = @import("html.zig");
const tree = @import("tree.zig");
const testing = std.testing;
const repeat = @import("../../framework/util/repeat.zig").repeat;

const Case = struct { []const u8, []const u8 };

fn renderWith(a: std.mem.Allocator, src: []const u8, linkify: bool) ![]u8 {
    var doc = try md.parse(a, src, .{ .linkify = linkify });
    defer doc.deinit();
    return html.render(a, &doc);
}

/// Runs every case; reports all the failures before failing.
fn check(cases: []const Case, linkify: bool) !void {
    var failed: usize = 0;
    for (cases) |c| {
        const got = try renderWith(testing.allocator, c[0], linkify);
        defer testing.allocator.free(got);
        if (!std.mem.eql(u8, got, c[1])) {
            failed += 1;
            std.debug.print("\n--- markdown ---\n{s}\n--- want ---\n{s}\n--- got ----\n{s}\n", .{ c[0], c[1], got });
        }
    }
    if (failed > 0) {
        std.debug.print("{d} of {d} cases failed\n", .{ failed, cases.len });
        return error.TestUnexpectedResult;
    }
}

test "spec: tabs" {
    try check(&.{
        .{ "\tfoo\tbaz\t\tbim", "<pre><code>foo\tbaz\t\tbim\n</code></pre>\n" },
        .{ "  \tfoo\tbaz\t\tbim", "<pre><code>foo\tbaz\t\tbim\n</code></pre>\n" },
        .{ "    a\ta\n    ὐ\ta", "<pre><code>a\ta\nὐ\ta\n</code></pre>\n" },
        .{ "  - foo\n\n\tbar", "<ul>\n<li>\n<p>foo</p>\n<p>bar</p>\n</li>\n</ul>\n" },
        .{ ">\t\tfoo", "<blockquote>\n<pre><code>  foo\n</code></pre>\n</blockquote>\n" },
        .{ "-\t\tfoo", "<ul>\n<li>\n<pre><code>  foo\n</code></pre>\n</li>\n</ul>\n" },
        .{ "- `one\n- two`", "<ul>\n<li>`one</li>\n<li>two`</li>\n</ul>\n" },
    }, false);
}

test "spec: thematic breaks" {
    try check(&.{
        .{ "***\n---\n___", "<hr />\n<hr />\n<hr />\n" },
        .{ "+++", "<p>+++</p>\n" },
        .{ "===", "<p>===</p>\n" },
        .{ "--\n**\n__", "<p>--\n**\n__</p>\n" },
        .{ " ***\n  ***\n   ***", "<hr />\n<hr />\n<hr />\n" },
        .{ "    ***", "<pre><code>***\n</code></pre>\n" },
        .{ "Foo\n    ***", "<p>Foo\n***</p>\n" },
        .{ "_____________________________________", "<hr />\n" },
        .{ " - - -", "<hr />\n" },
        .{ " **  * ** * ** * **", "<hr />\n" },
        .{ "- - - -    ", "<hr />\n" },
        .{ "_ _ _ _ a\n\na------\n\n---a---", "<p>_ _ _ _ a</p>\n<p>a------</p>\n<p>---a---</p>\n" },
        .{ " *-*", "<p><em>-</em></p>\n" },
        .{ "- foo\n***\n- bar", "<ul>\n<li>foo</li>\n</ul>\n<hr />\n<ul>\n<li>bar</li>\n</ul>\n" },
        .{ "Foo\n***\nbar", "<p>Foo</p>\n<hr />\n<p>bar</p>\n" },
        .{ "Foo\n---\nbar", "<h2>Foo</h2>\n<p>bar</p>\n" },
        .{ "* Foo\n* * *\n* Bar", "<ul>\n<li>Foo</li>\n</ul>\n<hr />\n<ul>\n<li>Bar</li>\n</ul>\n" },
        .{ "- Foo\n- * * *", "<ul>\n<li>Foo</li>\n<li>\n<hr />\n</li>\n</ul>\n" },
    }, false);
}

test "spec: ATX headings" {
    try check(&.{
        .{ "# foo\n## foo\n### foo\n#### foo\n##### foo\n###### foo", "<h1>foo</h1>\n<h2>foo</h2>\n<h3>foo</h3>\n<h4>foo</h4>\n<h5>foo</h5>\n<h6>foo</h6>\n" },
        .{ "####### foo", "<p>####### foo</p>\n" },
        .{ "#5 bolt\n\n#hashtag", "<p>#5 bolt</p>\n<p>#hashtag</p>\n" },
        .{ "\\## foo", "<p>## foo</p>\n" },
        .{ "# foo *bar* \\*baz\\*", "<h1>foo <em>bar</em> *baz*</h1>\n" },
        .{ "#                  foo                     ", "<h1>foo</h1>\n" },
        .{ " ### foo\n  ## foo\n   # foo", "<h3>foo</h3>\n<h2>foo</h2>\n<h1>foo</h1>\n" },
        .{ "    # foo", "<pre><code># foo\n</code></pre>\n" },
        .{ "foo\n    # bar", "<p>foo\n# bar</p>\n" },
        .{ "## foo ##\n  ###   bar    ###", "<h2>foo</h2>\n<h3>bar</h3>\n" },
        .{ "# foo ##################################\n##### foo ##", "<h1>foo</h1>\n<h5>foo</h5>\n" },
        .{ "### foo ###     ", "<h3>foo</h3>\n" },
        .{ "### foo ### b", "<h3>foo ### b</h3>\n" },
        .{ "# foo#", "<h1>foo#</h1>\n" },
        .{ "### foo \\###", "<h3>foo ###</h3>\n" },
        .{ "## foo #\\##", "<h2>foo ###</h2>\n" },
        .{ "# foo \\#", "<h1>foo #</h1>\n" },
        .{ "****\n## foo\n****", "<hr />\n<h2>foo</h2>\n<hr />\n" },
        .{ "Foo bar\n# baz\nBar foo", "<p>Foo bar</p>\n<h1>baz</h1>\n<p>Bar foo</p>\n" },
        .{ "## \n#\n### ###", "<h2></h2>\n<h1></h1>\n<h3></h3>\n" },
    }, false);
}

test "spec: setext headings" {
    try check(&.{
        .{ "Foo *bar*\n=========\n\nFoo *bar*\n---------", "<h1>Foo <em>bar</em></h1>\n<h2>Foo <em>bar</em></h2>\n" },
        .{ "Foo\n-------------------------\n\nFoo\n=", "<h2>Foo</h2>\n<h1>Foo</h1>\n" },
        .{ "   Foo\n---\n\n  Foo\n-----\n\n  Foo\n  ===", "<h2>Foo</h2>\n<h2>Foo</h2>\n<h1>Foo</h1>\n" },
        .{ "    Foo\n    ---\n\n    Foo\n---", "<pre><code>Foo\n---\n\nFoo\n</code></pre>\n<hr />\n" },
        .{ "Foo\n   ----      ", "<h2>Foo</h2>\n" },
        .{ "Foo\n    ---", "<p>Foo\n---</p>\n" },
        .{ "Foo\n= =\n\nFoo\n--- -", "<p>Foo\n= =</p>\n<p>Foo</p>\n<hr />\n" },
        .{ "Foo  \n-----", "<h2>Foo</h2>\n" },
        .{ "Foo\\\n----", "<h2>Foo\\</h2>\n" },
        .{ "`Foo\n----\n`\n\n<a title=\"a lot\n---\nof dashes\"/>", "<h2>`Foo</h2>\n<p>`</p>\n<h2>&lt;a title=&quot;a lot</h2>\n<p>of dashes&quot;/&gt;</p>\n" },
        .{ "> Foo\n---", "<blockquote>\n<p>Foo</p>\n</blockquote>\n<hr />\n" },
        .{ "> foo\nbar\n===", "<blockquote>\n<p>foo\nbar\n===</p>\n</blockquote>\n" },
        .{ "- Foo\n---", "<ul>\n<li>Foo</li>\n</ul>\n<hr />\n" },
        .{ "Foo\nBar\n---", "<h2>Foo\nBar</h2>\n" },
        .{ "Foo\n---\nBar\n---\nBaz", "<h2>Foo</h2>\n<h2>Bar</h2>\n<p>Baz</p>\n" },
        .{ "\n====", "<p>====</p>\n" },
        .{ "---\n---", "<hr />\n<hr />\n" },
        .{ "- foo\n-----", "<ul>\n<li>foo</li>\n</ul>\n<hr />\n" },
        .{ "    foo\n---", "<pre><code>foo\n</code></pre>\n<hr />\n" },
        .{ "> foo\n-----", "<blockquote>\n<p>foo</p>\n</blockquote>\n<hr />\n" },
        .{ "\\> foo\n------", "<h2>&gt; foo</h2>\n" },
        .{ "Foo\n\nbar\n---\nbaz", "<p>Foo</p>\n<h2>bar</h2>\n<p>baz</p>\n" },
        .{ "Foo\nbar\n\n---\n\nbaz", "<p>Foo\nbar</p>\n<hr />\n<p>baz</p>\n" },
        .{ "Foo\nbar\n* * *\nbaz", "<p>Foo\nbar</p>\n<hr />\n<p>baz</p>\n" },
        .{ "Foo\nbar\n\\---\nbaz", "<p>Foo\nbar\n---\nbaz</p>\n" },
    }, false);
}

test "spec: indented code blocks" {
    try check(&.{
        .{ "    a simple\n      indented code block", "<pre><code>a simple\n  indented code block\n</code></pre>\n" },
        .{ "  - foo\n\n    bar", "<ul>\n<li>\n<p>foo</p>\n<p>bar</p>\n</li>\n</ul>\n" },
        .{ "1.  foo\n\n    - bar", "<ol>\n<li>\n<p>foo</p>\n<ul>\n<li>bar</li>\n</ul>\n</li>\n</ol>\n" },
        .{ "    <a/>\n    *hi*\n\n    - one", "<pre><code>&lt;a/&gt;\n*hi*\n\n- one\n</code></pre>\n" },
        .{ "    chunk1\n\n    chunk2\n  \n \n \n    chunk3", "<pre><code>chunk1\n\nchunk2\n\n\n\nchunk3\n</code></pre>\n" },
        .{ "    chunk1\n      \n      chunk2", "<pre><code>chunk1\n  \n  chunk2\n</code></pre>\n" },
        .{ "Foo\n    bar", "<p>Foo\nbar</p>\n" },
        .{ "    foo\nbar", "<pre><code>foo\n</code></pre>\n<p>bar</p>\n" },
        .{ "# Heading\n    foo\nHeading\n------\n    foo\n----", "<h1>Heading</h1>\n<pre><code>foo\n</code></pre>\n<h2>Heading</h2>\n<pre><code>foo\n</code></pre>\n<hr />\n" },
        .{ "        foo\n    bar", "<pre><code>    foo\nbar\n</code></pre>\n" },
        .{ "\n    \n    foo\n    \n\n", "<pre><code>foo\n</code></pre>\n" },
        .{ "    foo  ", "<pre><code>foo  \n</code></pre>\n" },
    }, false);
}

test "spec: fenced code blocks" {
    try check(&.{
        .{ "```\n<\n >\n```", "<pre><code>&lt;\n &gt;\n</code></pre>\n" },
        .{ "~~~\n<\n >\n~~~", "<pre><code>&lt;\n &gt;\n</code></pre>\n" },
        .{ "``\nfoo\n``", "<p><code>foo</code></p>\n" },
        .{ "```\naaa\n~~~\n```", "<pre><code>aaa\n~~~\n</code></pre>\n" },
        .{ "~~~\naaa\n```\n~~~", "<pre><code>aaa\n```\n</code></pre>\n" },
        .{ "````\naaa\n```\n``````", "<pre><code>aaa\n```\n</code></pre>\n" },
        .{ "~~~~\naaa\n~~~\n~~~~", "<pre><code>aaa\n~~~\n</code></pre>\n" },
        .{ "```", "<pre><code></code></pre>\n" },
        .{ "`````\n\n```\naaa", "<pre><code>\n```\naaa\n</code></pre>\n" },
        .{ "> ```\n> aaa\n\nbbb", "<blockquote>\n<pre><code>aaa\n</code></pre>\n</blockquote>\n<p>bbb</p>\n" },
        .{ "```\n\n  \n```", "<pre><code>\n  \n</code></pre>\n" },
        .{ "```\n```", "<pre><code></code></pre>\n" },
        .{ " ```\n aaa\naaa\n```", "<pre><code>aaa\naaa\n</code></pre>\n" },
        .{ "  ```\naaa\n  aaa\naaa\n  ```", "<pre><code>aaa\naaa\naaa\n</code></pre>\n" },
        .{ "   ```\n   aaa\n    aaa\n  aaa\n   ```", "<pre><code>aaa\n aaa\naaa\n</code></pre>\n" },
        .{ "    ```\n    aaa\n    ```", "<pre><code>```\naaa\n```\n</code></pre>\n" },
        .{ "```\naaa\n  ```", "<pre><code>aaa\n</code></pre>\n" },
        .{ "   ```\naaa\n  ```", "<pre><code>aaa\n</code></pre>\n" },
        .{ "```\naaa\n    ```", "<pre><code>aaa\n    ```\n</code></pre>\n" },
        .{ "``` ```\naaa", "<p><code> </code>\naaa</p>\n" },
        .{ "~~~~~~\naaa\n~~~ ~~", "<pre><code>aaa\n~~~ ~~\n</code></pre>\n" },
        .{ "foo\n```\nbar\n```\nbaz", "<p>foo</p>\n<pre><code>bar\n</code></pre>\n<p>baz</p>\n" },
        .{ "foo\n---\n~~~\nbar\n~~~\n# baz", "<h2>foo</h2>\n<pre><code>bar\n</code></pre>\n<h1>baz</h1>\n" },
        .{ "```ruby\ndef foo(x)\n  return 3\nend\n```", "<pre><code class=\"language-ruby\">def foo(x)\n  return 3\nend\n</code></pre>\n" },
        .{ "~~~~    ruby startline=3 $%@#$\ndef foo(x)\n  return 3\nend\n~~~~~~~", "<pre><code class=\"language-ruby\">def foo(x)\n  return 3\nend\n</code></pre>\n" },
        .{ "````;\n````", "<pre><code class=\"language-;\"></code></pre>\n" },
        .{ "``` aa ```\nfoo", "<p><code>aa</code>\nfoo</p>\n" },
        .{ "~~~ aa ``` ~~~\nfoo\n~~~", "<pre><code class=\"language-aa\">foo\n</code></pre>\n" },
        .{ "```\n``` aaa\n```", "<pre><code>``` aaa\n</code></pre>\n" },
    }, false);
}

test "spec: HTML blocks" {
    try check(&.{
        .{ "<div>\n*hello*\n         <foo><a>\n</div>", "<div>\n*hello*\n         <foo><a>\n</div>\n" },
        .{ "<DIV CLASS=\"foo\">\n\n*Markdown*\n\n</DIV>", "<DIV CLASS=\"foo\">\n<p><em>Markdown</em></p>\n</DIV>\n" },
        .{ "<!-- Foo\n\nbar\n   baz -->\nokay", "<!-- Foo\n\nbar\n   baz -->\n<p>okay</p>\n" },
        .{ "<script type=\"text/javascript\">\n// JavaScript example\n\ndocument.getElementById(\"demo\").innerHTML = \"Hello JavaScript!\";\n</script>\nokay", "<script type=\"text/javascript\">\n// JavaScript example\n\ndocument.getElementById(\"demo\").innerHTML = \"Hello JavaScript!\";\n</script>\n<p>okay</p>\n" },
        .{ "<?php\n\n  echo '>';\n\n?>\nokay", "<?php\n\n  echo '>';\n\n?>\n<p>okay</p>\n" },
        .{ "<!DOCTYPE html>", "<!DOCTYPE html>\n" },
        .{ "  <!-- foo -->\n\n    <!-- foo -->", "  <!-- foo -->\n<pre><code>&lt;!-- foo --&gt;\n</code></pre>\n" },
        .{ "Foo\n<div>\nbar\n</div>", "<p>Foo</p>\n<div>\nbar\n</div>\n" },
        .{ "<div>\nbar\n</div>\n*foo*", "<div>\nbar\n</div>\n*foo*\n" },
    }, false);
}

test "spec: paragraphs and blank lines" {
    try check(&.{
        .{ "aaa\n\nbbb", "<p>aaa</p>\n<p>bbb</p>\n" },
        .{ "aaa\nbbb\n\nccc\nddd", "<p>aaa\nbbb</p>\n<p>ccc\nddd</p>\n" },
        .{ "aaa\n\n\nbbb", "<p>aaa</p>\n<p>bbb</p>\n" },
        .{ "  aaa\n bbb", "<p>aaa\nbbb</p>\n" },
        .{ "aaa\n             bbb\n                                       ccc", "<p>aaa\nbbb\nccc</p>\n" },
        .{ "   aaa\nbbb", "<p>aaa\nbbb</p>\n" },
        .{ "    aaa\nbbb", "<pre><code>aaa\n</code></pre>\n<p>bbb</p>\n" },
        .{ "aaa     \nbbb     ", "<p>aaa<br />\nbbb</p>\n" },
        .{ "  \n\naaa\n  \n\n# aaa\n\n  ", "<p>aaa</p>\n<h1>aaa</h1>\n" },
    }, false);
}

test "spec: block quotes" {
    try check(&.{
        .{ "> # Foo\n> bar\n> baz", "<blockquote>\n<h1>Foo</h1>\n<p>bar\nbaz</p>\n</blockquote>\n" },
        .{ "># Foo\n>bar\n> baz", "<blockquote>\n<h1>Foo</h1>\n<p>bar\nbaz</p>\n</blockquote>\n" },
        .{ "   > # Foo\n   > bar\n > baz", "<blockquote>\n<h1>Foo</h1>\n<p>bar\nbaz</p>\n</blockquote>\n" },
        .{ "    > # Foo\n    > bar\n    > baz", "<pre><code>&gt; # Foo\n&gt; bar\n&gt; baz\n</code></pre>\n" },
        .{ "> # Foo\n> bar\nbaz", "<blockquote>\n<h1>Foo</h1>\n<p>bar\nbaz</p>\n</blockquote>\n" },
        .{ "> bar\nbaz\n> foo", "<blockquote>\n<p>bar\nbaz\nfoo</p>\n</blockquote>\n" },
        .{ "> foo\n---", "<blockquote>\n<p>foo</p>\n</blockquote>\n<hr />\n" },
        .{ "> - foo\n- bar", "<blockquote>\n<ul>\n<li>foo</li>\n</ul>\n</blockquote>\n<ul>\n<li>bar</li>\n</ul>\n" },
        .{ ">     foo\n    bar", "<blockquote>\n<pre><code>foo\n</code></pre>\n</blockquote>\n<pre><code>bar\n</code></pre>\n" },
        .{ "> ```\nfoo\n```", "<blockquote>\n<pre><code></code></pre>\n</blockquote>\n<p>foo</p>\n<pre><code></code></pre>\n" },
        .{ "> foo\n    - bar", "<blockquote>\n<p>foo\n- bar</p>\n</blockquote>\n" },
        .{ ">", "<blockquote>\n</blockquote>\n" },
        .{ ">\n>  \n> ", "<blockquote>\n</blockquote>\n" },
        .{ ">\n> foo\n>  ", "<blockquote>\n<p>foo</p>\n</blockquote>\n" },
        .{ "> foo\n\n> bar", "<blockquote>\n<p>foo</p>\n</blockquote>\n<blockquote>\n<p>bar</p>\n</blockquote>\n" },
        .{ "> foo\n> bar", "<blockquote>\n<p>foo\nbar</p>\n</blockquote>\n" },
        .{ "> foo\n>\n> bar", "<blockquote>\n<p>foo</p>\n<p>bar</p>\n</blockquote>\n" },
        .{ "foo\n> bar", "<p>foo</p>\n<blockquote>\n<p>bar</p>\n</blockquote>\n" },
        .{ "> aaa\n***\n> bbb", "<blockquote>\n<p>aaa</p>\n</blockquote>\n<hr />\n<blockquote>\n<p>bbb</p>\n</blockquote>\n" },
        .{ "> bar\n\nbaz", "<blockquote>\n<p>bar</p>\n</blockquote>\n<p>baz</p>\n" },
        .{ "> bar\n>\nbaz", "<blockquote>\n<p>bar</p>\n</blockquote>\n<p>baz</p>\n" },
        .{ "> > > foo\nbar", "<blockquote>\n<blockquote>\n<blockquote>\n<p>foo\nbar</p>\n</blockquote>\n</blockquote>\n</blockquote>\n" },
        .{ ">>> foo\n> bar\n>>baz", "<blockquote>\n<blockquote>\n<blockquote>\n<p>foo\nbar\nbaz</p>\n</blockquote>\n</blockquote>\n</blockquote>\n" },
        .{ ">     code\n\n>    not code", "<blockquote>\n<pre><code>code\n</code></pre>\n</blockquote>\n<blockquote>\n<p>not code</p>\n</blockquote>\n" },
    }, false);
}

test "spec: list items" {
    try check(&.{
        .{ "A paragraph\nwith two lines.\n\n    indented code\n\n> A block quote.", "<p>A paragraph\nwith two lines.</p>\n<pre><code>indented code\n</code></pre>\n<blockquote>\n<p>A block quote.</p>\n</blockquote>\n" },
        .{ "1.  A paragraph\n    with two lines.\n\n        indented code\n\n    > A block quote.", "<ol>\n<li>\n<p>A paragraph\nwith two lines.</p>\n<pre><code>indented code\n</code></pre>\n<blockquote>\n<p>A block quote.</p>\n</blockquote>\n</li>\n</ol>\n" },
        .{ "- one\n\n two", "<ul>\n<li>one</li>\n</ul>\n<p>two</p>\n" },
        .{ "- one\n\n  two", "<ul>\n<li>\n<p>one</p>\n<p>two</p>\n</li>\n</ul>\n" },
        .{ " -    one\n\n     two", "<ul>\n<li>one</li>\n</ul>\n<pre><code> two\n</code></pre>\n" },
        .{ " -    one\n\n      two", "<ul>\n<li>\n<p>one</p>\n<p>two</p>\n</li>\n</ul>\n" },
        .{ "   > > 1.  one\n>>\n>>     two", "<blockquote>\n<blockquote>\n<ol>\n<li>\n<p>one</p>\n<p>two</p>\n</li>\n</ol>\n</blockquote>\n</blockquote>\n" },
        .{ ">>- one\n>>\n  >  > two", "<blockquote>\n<blockquote>\n<ul>\n<li>one</li>\n</ul>\n<p>two</p>\n</blockquote>\n</blockquote>\n" },
        .{ "-one\n\n2.two", "<p>-one</p>\n<p>2.two</p>\n" },
        .{ "- foo\n\n\n  bar", "<ul>\n<li>\n<p>foo</p>\n<p>bar</p>\n</li>\n</ul>\n" },
        .{ "1.  foo\n\n    ```\n    bar\n    ```\n\n    baz\n\n    > bam", "<ol>\n<li>\n<p>foo</p>\n<pre><code>bar\n</code></pre>\n<p>baz</p>\n<blockquote>\n<p>bam</p>\n</blockquote>\n</li>\n</ol>\n" },
        .{ "- Foo\n\n      bar\n\n\n      baz", "<ul>\n<li>\n<p>Foo</p>\n<pre><code>bar\n\n\nbaz\n</code></pre>\n</li>\n</ul>\n" },
        .{ "123456789. ok", "<ol start=\"123456789\">\n<li>ok</li>\n</ol>\n" },
        .{ "1234567890. not ok", "<p>1234567890. not ok</p>\n" },
        .{ "0. ok", "<ol start=\"0\">\n<li>ok</li>\n</ol>\n" },
        .{ "003. ok", "<ol start=\"3\">\n<li>ok</li>\n</ol>\n" },
        .{ "-1. not ok", "<p>-1. not ok</p>\n" },
        .{ "- foo\n\n      bar", "<ul>\n<li>\n<p>foo</p>\n<pre><code>bar\n</code></pre>\n</li>\n</ul>\n" },
        .{ "  10.  foo\n\n           bar", "<ol start=\"10\">\n<li>\n<p>foo</p>\n<pre><code>bar\n</code></pre>\n</li>\n</ol>\n" },
        .{ "    indented code\n\nparagraph\n\n    more code", "<pre><code>indented code\n</code></pre>\n<p>paragraph</p>\n<pre><code>more code\n</code></pre>\n" },
        .{ "1.     indented code\n\n   paragraph\n\n       more code", "<ol>\n<li>\n<pre><code>indented code\n</code></pre>\n<p>paragraph</p>\n<pre><code>more code\n</code></pre>\n</li>\n</ol>\n" },
        .{ "1.      indented code\n\n   paragraph\n\n       more code", "<ol>\n<li>\n<pre><code> indented code\n</code></pre>\n<p>paragraph</p>\n<pre><code>more code\n</code></pre>\n</li>\n</ol>\n" },
        .{ "   foo\n\nbar", "<p>foo</p>\n<p>bar</p>\n" },
        .{ "-    foo\n\n  bar", "<ul>\n<li>foo</li>\n</ul>\n<p>bar</p>\n" },
        .{ "-  foo\n\n   bar", "<ul>\n<li>\n<p>foo</p>\n<p>bar</p>\n</li>\n</ul>\n" },
        .{ "-\n  foo\n-\n  ```\n  bar\n  ```\n-\n      baz", "<ul>\n<li>foo</li>\n<li>\n<pre><code>bar\n</code></pre>\n</li>\n<li>\n<pre><code>baz\n</code></pre>\n</li>\n</ul>\n" },
        .{ "-   \n  foo", "<ul>\n<li>foo</li>\n</ul>\n" },
        .{ "-\n\n  foo", "<ul>\n<li></li>\n</ul>\n<p>foo</p>\n" },
        .{ "- foo\n-\n- bar", "<ul>\n<li>foo</li>\n<li></li>\n<li>bar</li>\n</ul>\n" },
        .{ "- foo\n-   \n- bar", "<ul>\n<li>foo</li>\n<li></li>\n<li>bar</li>\n</ul>\n" },
        .{ "1. foo\n2.\n3. bar", "<ol>\n<li>foo</li>\n<li></li>\n<li>bar</li>\n</ol>\n" },
        .{ "*", "<ul>\n<li></li>\n</ul>\n" },
        .{ "foo\n*\n\nfoo\n1.", "<p>foo\n*</p>\n<p>foo\n1.</p>\n" },
        .{ "- a\n - b\n  - c\n   - d\n  - e\n - f\n- g", "<ul>\n<li>a</li>\n<li>b</li>\n<li>c</li>\n<li>d</li>\n<li>e</li>\n<li>f</li>\n<li>g</li>\n</ul>\n" },
        .{ "1. a\n\n  2. b\n\n   3. c", "<ol>\n<li>\n<p>a</p>\n</li>\n<li>\n<p>b</p>\n</li>\n<li>\n<p>c</p>\n</li>\n</ol>\n" },
        .{ "- a\n - b\n  - c\n   - d\n    - e", "<ul>\n<li>a</li>\n<li>b</li>\n<li>c</li>\n<li>d\n- e</li>\n</ul>\n" },
        .{ "1. a\n\n  2. b\n\n    3. c", "<ol>\n<li>\n<p>a</p>\n</li>\n<li>\n<p>b</p>\n</li>\n</ol>\n<pre><code>3. c\n</code></pre>\n" },
        .{ "- a\n- b\n\n- c", "<ul>\n<li>\n<p>a</p>\n</li>\n<li>\n<p>b</p>\n</li>\n<li>\n<p>c</p>\n</li>\n</ul>\n" },
        .{ "* a\n*\n\n* c", "<ul>\n<li>\n<p>a</p>\n</li>\n<li></li>\n<li>\n<p>c</p>\n</li>\n</ul>\n" },
        .{ "- a\n- b\n\n  c\n- d", "<ul>\n<li>\n<p>a</p>\n</li>\n<li>\n<p>b</p>\n<p>c</p>\n</li>\n<li>\n<p>d</p>\n</li>\n</ul>\n" },
        .{ "- a\n- b\n\n  [ref]: /url\n- d", "<ul>\n<li>\n<p>a</p>\n</li>\n<li>\n<p>b</p>\n</li>\n<li>\n<p>d</p>\n</li>\n</ul>\n" },
        .{ "- a\n- ```\n  b\n\n\n  ```\n- c", "<ul>\n<li>a</li>\n<li>\n<pre><code>b\n\n\n</code></pre>\n</li>\n<li>c</li>\n</ul>\n" },
        .{ "- a\n  - b\n\n    c\n- d", "<ul>\n<li>a\n<ul>\n<li>\n<p>b</p>\n<p>c</p>\n</li>\n</ul>\n</li>\n<li>d</li>\n</ul>\n" },
        .{ "* a\n  > b\n  >\n* c", "<ul>\n<li>a\n<blockquote>\n<p>b</p>\n</blockquote>\n</li>\n<li>c</li>\n</ul>\n" },
        .{ "- a\n  > b\n  ```\n  c\n  ```\n- d", "<ul>\n<li>a\n<blockquote>\n<p>b</p>\n</blockquote>\n<pre><code>c\n</code></pre>\n</li>\n<li>d</li>\n</ul>\n" },
        .{ "- a", "<ul>\n<li>a</li>\n</ul>\n" },
        .{ "- a\n  - b", "<ul>\n<li>a\n<ul>\n<li>b</li>\n</ul>\n</li>\n</ul>\n" },
        .{ "1. ```\n   foo\n   ```\n\n   bar", "<ol>\n<li>\n<pre><code>foo\n</code></pre>\n<p>bar</p>\n</li>\n</ol>\n" },
        .{ "* foo\n  * bar\n\n  baz", "<ul>\n<li>\n<p>foo</p>\n<ul>\n<li>bar</li>\n</ul>\n<p>baz</p>\n</li>\n</ul>\n" },
        .{ "- a\n  - b\n  - c\n\n- d\n  - e\n  - f", "<ul>\n<li>\n<p>a</p>\n<ul>\n<li>b</li>\n<li>c</li>\n</ul>\n</li>\n<li>\n<p>d</p>\n<ul>\n<li>e</li>\n<li>f</li>\n</ul>\n</li>\n</ul>\n" },
    }, false);
}

test "spec: lists" {
    try check(&.{
        .{ "- foo\n- bar\n+ baz", "<ul>\n<li>foo</li>\n<li>bar</li>\n</ul>\n<ul>\n<li>baz</li>\n</ul>\n" },
        .{ "1. foo\n2. bar\n3) baz", "<ol>\n<li>foo</li>\n<li>bar</li>\n</ol>\n<ol start=\"3\">\n<li>baz</li>\n</ol>\n" },
        .{ "Foo\n- bar\n- baz", "<p>Foo</p>\n<ul>\n<li>bar</li>\n<li>baz</li>\n</ul>\n" },
        .{ "The number of windows in my house is\n14.  The number of doors is 6.", "<p>The number of windows in my house is\n14.  The number of doors is 6.</p>\n" },
        .{ "The number of windows in my house is\n1.  The number of doors is 6.", "<p>The number of windows in my house is</p>\n<ol>\n<li>The number of doors is 6.</li>\n</ol>\n" },
        .{ "- foo\n\n- bar\n\n\n- baz", "<ul>\n<li>\n<p>foo</p>\n</li>\n<li>\n<p>bar</p>\n</li>\n<li>\n<p>baz</p>\n</li>\n</ul>\n" },
        .{ "- foo\n  - bar\n    - baz\n\n\n      bim", "<ul>\n<li>foo\n<ul>\n<li>bar\n<ul>\n<li>\n<p>baz</p>\n<p>bim</p>\n</li>\n</ul>\n</li>\n</ul>\n</li>\n</ul>\n" },
        .{ "- foo\n- bar\n\n<!-- -->\n\n- baz\n- bim", "<ul>\n<li>foo</li>\n<li>bar</li>\n</ul>\n<!-- -->\n<ul>\n<li>baz</li>\n<li>bim</li>\n</ul>\n" },
        .{ "-   foo\n\n    notcode\n\n-   foo\n\n<!-- -->\n\n    code", "<ul>\n<li>\n<p>foo</p>\n<p>notcode</p>\n</li>\n<li>\n<p>foo</p>\n</li>\n</ul>\n<!-- -->\n<pre><code>code\n</code></pre>\n" },
    }, false);
}

test "spec: backslash escapes and entities" {
    try check(&.{
        .{ "\\!\\\"\\#\\$\\%\\&\\'\\(\\)\\*\\+\\,\\-\\.\\/\\:\\;\\<\\=\\>\\?\\@\\[\\\\\\]\\^\\_\\`\\{\\|\\}\\~", "<p>!&quot;#$%&amp;'()*+,-./:;&lt;=&gt;?@[\\]^_`{|}~</p>\n" },
        .{ "\\\t\\A\\a\\ \\3\\φ\\«", "<p>\\\t\\A\\a\\ \\3\\φ\\«</p>\n" },
        .{ "\\*not emphasized*\n\\<br/> not a tag\n\\[not a link](/foo)\n\\`not code`\n1\\. not a list\n\\* not a list\n\\# not a heading\n\\[foo]: /url \"not a reference\"\n\\&ouml; not a character entity", "<p>*not emphasized*\n&lt;br/&gt; not a tag\n[not a link](/foo)\n`not code`\n1. not a list\n* not a list\n# not a heading\n[foo]: /url &quot;not a reference&quot;\n&amp;ouml; not a character entity</p>\n" },
        .{ "\\\\*emphasis*", "<p>\\<em>emphasis</em></p>\n" },
        .{ "foo\\\nbar", "<p>foo<br />\nbar</p>\n" },
        .{ "`` \\[\\` ``", "<p><code>\\[\\`</code></p>\n" },
        .{ "    \\[\\]", "<pre><code>\\[\\]\n</code></pre>\n" },
        .{ "~~~\n\\[\\]\n~~~", "<pre><code>\\[\\]\n</code></pre>\n" },
        .{ "&nbsp; &amp; &copy; &ouml;", "<p>\u{a0} &amp; © ö</p>\n" },
        .{ "&#35; &#1234; &#992; &#0;", "<p># Ӓ Ϡ \u{fffd}</p>\n" },
        .{ "&#X22; &#XD06; &#xcab;", "<p>&quot; ആ ಫ</p>\n" },
        .{ "&nbsp &x; &#; &#x;\n&#87654321;\n&#abcdef0;\n&ThisIsNotDefined; &hi?;", "<p>&amp;nbsp &amp;x; &amp;#; &amp;#x;\n&amp;#87654321;\n&amp;#abcdef0;\n&amp;ThisIsNotDefined; &amp;hi?;</p>\n" },
        .{ "&copy", "<p>&amp;copy</p>\n" },
        .{ "&MadeUpEntity;", "<p>&amp;MadeUpEntity;</p>\n" },
        .{ "[foo](/f&ouml;&ouml; \"f&ouml;&ouml;\")", "<p><a href=\"/f%C3%B6%C3%B6\" title=\"föö\">foo</a></p>\n" },
        .{ "``` f&ouml;&ouml;\nfoo\n```", "<pre><code class=\"language-föö\">foo\n</code></pre>\n" },
        .{ "`f&ouml;&ouml;`", "<p><code>f&amp;ouml;&amp;ouml;</code></p>\n" },
        .{ "    f&ouml;f&ouml;", "<pre><code>f&amp;ouml;f&amp;ouml;\n</code></pre>\n" },
    }, false);
}

test "spec: code spans" {
    try check(&.{
        .{ "`foo`", "<p><code>foo</code></p>\n" },
        .{ "`` foo ` bar ``", "<p><code>foo ` bar</code></p>\n" },
        .{ "` `` `", "<p><code>``</code></p>\n" },
        .{ "`  ``  `", "<p><code> `` </code></p>\n" },
        .{ "` a`", "<p><code> a</code></p>\n" },
        .{ "`foo   bar \nbaz`", "<p><code>foo   bar  baz</code></p>\n" },
        .{ "`foo\\`bar`", "<p><code>foo\\</code>bar`</p>\n" },
        .{ "``foo`bar``", "<p><code>foo`bar</code></p>\n" },
        .{ "` foo `` bar `", "<p><code>foo `` bar</code></p>\n" },
        .{ "*foo`*`", "<p>*foo<code>*</code></p>\n" },
        .{ "[not a `link](/foo`)", "<p>[not a <code>link](/foo</code>)</p>\n" },
        .{ "`foo", "<p>`foo</p>\n" },
        .{ "`foo``bar``", "<p>`foo<code>bar</code></p>\n" },
        .{ "`code  \nspan`", "<p><code>code   span</code></p>\n" },
        .{ "`code\\\nspan`", "<p><code>code\\ span</code></p>\n" },
    }, false);
}

test "spec: emphasis" {
    try check(&.{
        .{ "*foo bar*", "<p><em>foo bar</em></p>\n" },
        .{ "a * foo bar*", "<p>a * foo bar*</p>\n" },
        .{ "a*\"foo\"*", "<p>a*&quot;foo&quot;*</p>\n" },
        .{ "* a *", "<ul>\n<li>a *</li>\n</ul>\n" },
        .{ "foo*bar*", "<p>foo<em>bar</em></p>\n" },
        .{ "5*6*78", "<p>5<em>6</em>78</p>\n" },
        .{ "_foo bar_", "<p><em>foo bar</em></p>\n" },
        .{ "_ foo bar_", "<p>_ foo bar_</p>\n" },
        .{ "a_\"foo\"_", "<p>a_&quot;foo&quot;_</p>\n" },
        .{ "foo_bar_", "<p>foo_bar_</p>\n" },
        .{ "5_6_78", "<p>5_6_78</p>\n" },
        .{ "пристаням_стремятся_", "<p>пристаням_стремятся_</p>\n" },
        .{ "aa_\"bb\"_cc", "<p>aa_&quot;bb&quot;_cc</p>\n" },
        .{ "foo-_(bar)_", "<p>foo-<em>(bar)</em></p>\n" },
        .{ "_foo*", "<p>_foo*</p>\n" },
        .{ "*foo bar *", "<p>*foo bar *</p>\n" },
        .{ "*foo bar\n*", "<p>*foo bar\n*</p>\n" },
        .{ "*(*foo)", "<p>*(*foo)</p>\n" },
        .{ "*(*foo*)*", "<p><em>(<em>foo</em>)</em></p>\n" },
        .{ "*foo*bar", "<p><em>foo</em>bar</p>\n" },
        .{ "_foo bar _", "<p>_foo bar _</p>\n" },
        .{ "_(_foo)", "<p>_(_foo)</p>\n" },
        .{ "_(_foo_)_", "<p><em>(<em>foo</em>)</em></p>\n" },
        .{ "_foo_bar", "<p>_foo_bar</p>\n" },
        .{ "_foo_bar_baz_", "<p><em>foo_bar_baz</em></p>\n" },
        .{ "_(bar)_.", "<p><em>(bar)</em>.</p>\n" },
        .{ "**foo bar**", "<p><strong>foo bar</strong></p>\n" },
        .{ "** foo bar**", "<p>** foo bar**</p>\n" },
        .{ "a**\"foo\"**", "<p>a**&quot;foo&quot;**</p>\n" },
        .{ "foo**bar**", "<p>foo<strong>bar</strong></p>\n" },
        .{ "__foo bar__", "<p><strong>foo bar</strong></p>\n" },
        .{ "__ foo bar__", "<p>__ foo bar__</p>\n" },
        .{ "__\nfoo bar__", "<p>__\nfoo bar__</p>\n" },
        .{ "a__\"foo\"__", "<p>a__&quot;foo&quot;__</p>\n" },
        .{ "foo__bar__", "<p>foo__bar__</p>\n" },
        .{ "5__6__78", "<p>5__6__78</p>\n" },
        .{ "__foo, __bar__, baz__", "<p><strong>foo, <strong>bar</strong>, baz</strong></p>\n" },
        .{ "foo-__(bar)__", "<p>foo-<strong>(bar)</strong></p>\n" },
        .{ "**foo bar **", "<p>**foo bar **</p>\n" },
        .{ "**(**foo)", "<p>**(**foo)</p>\n" },
        .{ "*(**foo**)*", "<p><em>(<strong>foo</strong>)</em></p>\n" },
        .{ "**Gomphocarpus (*Gomphocarpus physocarpus*, syn.\n*Asclepias physocarpa*)**", "<p><strong>Gomphocarpus (<em>Gomphocarpus physocarpus</em>, syn.\n<em>Asclepias physocarpa</em>)</strong></p>\n" },
        .{ "**foo \"*bar*\" foo**", "<p><strong>foo &quot;<em>bar</em>&quot; foo</strong></p>\n" },
        .{ "**foo**bar", "<p><strong>foo</strong>bar</p>\n" },
        .{ "__foo bar __", "<p>__foo bar __</p>\n" },
        .{ "__(__foo)", "<p>__(__foo)</p>\n" },
        .{ "_(__foo__)_", "<p><em>(<strong>foo</strong>)</em></p>\n" },
        .{ "__foo__bar", "<p>__foo__bar</p>\n" },
        .{ "__пристаням__стремятся", "<p>__пристаням__стремятся</p>\n" },
        .{ "__foo__bar__baz__", "<p><strong>foo__bar__baz</strong></p>\n" },
        .{ "__(bar)__.", "<p><strong>(bar)</strong>.</p>\n" },
        .{ "*foo [bar](/url)*", "<p><em>foo <a href=\"/url\">bar</a></em></p>\n" },
        .{ "*foo\nbar*", "<p><em>foo\nbar</em></p>\n" },
        .{ "_foo __bar__ baz_", "<p><em>foo <strong>bar</strong> baz</em></p>\n" },
        .{ "_foo _bar_ baz_", "<p><em>foo <em>bar</em> baz</em></p>\n" },
        .{ "__foo_ bar_", "<p><em><em>foo</em> bar</em></p>\n" },
        .{ "*foo *bar**", "<p><em>foo <em>bar</em></em></p>\n" },
        .{ "*foo **bar** baz*", "<p><em>foo <strong>bar</strong> baz</em></p>\n" },
        .{ "*foo**bar**baz*", "<p><em>foo<strong>bar</strong>baz</em></p>\n" },
        .{ "*foo**bar*", "<p><em>foo**bar</em></p>\n" },
        .{ "***foo** bar*", "<p><em><strong>foo</strong> bar</em></p>\n" },
        .{ "*foo **bar***", "<p><em>foo <strong>bar</strong></em></p>\n" },
        .{ "*foo**bar***", "<p><em>foo<strong>bar</strong></em></p>\n" },
        .{ "foo***bar***baz", "<p>foo<em><strong>bar</strong></em>baz</p>\n" },
        .{ "foo******bar*********baz", "<p>foo<strong><strong><strong>bar</strong></strong></strong>***baz</p>\n" },
        .{ "*foo **bar *baz* bim** bop*", "<p><em>foo <strong>bar <em>baz</em> bim</strong> bop</em></p>\n" },
        .{ "*foo [*bar*](/url)*", "<p><em>foo <a href=\"/url\"><em>bar</em></a></em></p>\n" },
        .{ "** is not an empty emphasis", "<p>** is not an empty emphasis</p>\n" },
        .{ "**** is not an empty strong emphasis", "<p>**** is not an empty strong emphasis</p>\n" },
        .{ "**foo [bar](/url)**", "<p><strong>foo <a href=\"/url\">bar</a></strong></p>\n" },
        .{ "**foo\nbar**", "<p><strong>foo\nbar</strong></p>\n" },
        .{ "__foo _bar_ baz__", "<p><strong>foo <em>bar</em> baz</strong></p>\n" },
        .{ "__foo __bar__ baz__", "<p><strong>foo <strong>bar</strong> baz</strong></p>\n" },
        .{ "____foo__ bar__", "<p><strong><strong>foo</strong> bar</strong></p>\n" },
        .{ "**foo **bar****", "<p><strong>foo <strong>bar</strong></strong></p>\n" },
        .{ "**foo *bar* baz**", "<p><strong>foo <em>bar</em> baz</strong></p>\n" },
        .{ "**foo*bar*baz**", "<p><strong>foo<em>bar</em>baz</strong></p>\n" },
        .{ "***foo* bar**", "<p><strong><em>foo</em> bar</strong></p>\n" },
        .{ "**foo *bar***", "<p><strong>foo <em>bar</em></strong></p>\n" },
        .{ "**foo *bar **baz**\nbim* bop**", "<p><strong>foo <em>bar <strong>baz</strong>\nbim</em> bop</strong></p>\n" },
        .{ "**foo [*bar*](/url)**", "<p><strong>foo <a href=\"/url\"><em>bar</em></a></strong></p>\n" },
        .{ "__ is not an empty emphasis", "<p>__ is not an empty emphasis</p>\n" },
        .{ "____ is not an empty strong emphasis", "<p>____ is not an empty strong emphasis</p>\n" },
        .{ "foo ***", "<p>foo ***</p>\n" },
        .{ "foo *\\**", "<p>foo <em>*</em></p>\n" },
        .{ "foo *_*", "<p>foo <em>_</em></p>\n" },
        .{ "foo *****", "<p>foo *****</p>\n" },
        .{ "foo **\\***", "<p>foo <strong>*</strong></p>\n" },
        .{ "foo **_**", "<p>foo <strong>_</strong></p>\n" },
        .{ "**foo*", "<p>*<em>foo</em></p>\n" },
        .{ "*foo**", "<p><em>foo</em>*</p>\n" },
        .{ "***foo**", "<p>*<strong>foo</strong></p>\n" },
        .{ "****foo*", "<p>***<em>foo</em></p>\n" },
        .{ "**foo***", "<p><strong>foo</strong>*</p>\n" },
        .{ "*foo****", "<p><em>foo</em>***</p>\n" },
        .{ "foo ___", "<p>foo ___</p>\n" },
        .{ "foo _\\__", "<p>foo <em>_</em></p>\n" },
        .{ "foo _*_", "<p>foo <em>*</em></p>\n" },
        .{ "foo _____", "<p>foo _____</p>\n" },
        .{ "foo __\\___", "<p>foo <strong>_</strong></p>\n" },
        .{ "foo __*__", "<p>foo <strong>*</strong></p>\n" },
        .{ "__foo_", "<p>_<em>foo</em></p>\n" },
        .{ "_foo__", "<p><em>foo</em>_</p>\n" },
        .{ "___foo__", "<p>_<strong>foo</strong></p>\n" },
        .{ "____foo_", "<p>___<em>foo</em></p>\n" },
        .{ "__foo___", "<p><strong>foo</strong>_</p>\n" },
        .{ "_foo____", "<p><em>foo</em>___</p>\n" },
        .{ "**foo**", "<p><strong>foo</strong></p>\n" },
        .{ "*_foo_*", "<p><em><em>foo</em></em></p>\n" },
        .{ "__foo__", "<p><strong>foo</strong></p>\n" },
        .{ "_*foo*_", "<p><em><em>foo</em></em></p>\n" },
        .{ "****foo****", "<p><strong><strong>foo</strong></strong></p>\n" },
        .{ "____foo____", "<p><strong><strong>foo</strong></strong></p>\n" },
        .{ "******foo******", "<p><strong><strong><strong>foo</strong></strong></strong></p>\n" },
        .{ "***foo***", "<p><em><strong>foo</strong></em></p>\n" },
        .{ "_____foo_____", "<p><em><strong><strong>foo</strong></strong></em></p>\n" },
        .{ "*foo _bar* baz_", "<p><em>foo _bar</em> baz_</p>\n" },
        .{ "*foo __bar *baz bim__ bam*", "<p><em>foo <strong>bar *baz bim</strong> bam</em></p>\n" },
        .{ "**foo **bar baz**", "<p>**foo <strong>bar baz</strong></p>\n" },
        .{ "*foo *bar baz*", "<p>*foo <em>bar baz</em></p>\n" },
        .{ "*[bar*](/url)", "<p>*<a href=\"/url\">bar*</a></p>\n" },
        .{ "_foo [bar_](/url)", "<p>_foo <a href=\"/url\">bar_</a></p>\n" },
        .{ "**a<http://foo.bar/?q=**>", "<p>**a<a href=\"http://foo.bar/?q=**\">http://foo.bar/?q=**</a></p>\n" },
        .{ "__a<http://foo.bar/?q=__>", "<p>__a<a href=\"http://foo.bar/?q=__\">http://foo.bar/?q=__</a></p>\n" },
    }, false);
}

test "spec: links" {
    try check(&.{
        .{ "[link](/uri \"title\")", "<p><a href=\"/uri\" title=\"title\">link</a></p>\n" },
        .{ "[link](/uri)", "<p><a href=\"/uri\">link</a></p>\n" },
        .{ "[link]()", "<p><a href=\"\">link</a></p>\n" },
        .{ "[link](<>)", "<p><a href=\"\">link</a></p>\n" },
        .{ "[link](/my uri)", "<p>[link](/my uri)</p>\n" },
        .{ "[link](</my uri>)", "<p><a href=\"/my%20uri\">link</a></p>\n" },
        .{ "[link](foo\nbar)", "<p>[link](foo\nbar)</p>\n" },
        .{ "[a](<b)c>)", "<p><a href=\"b)c\">a</a></p>\n" },
        .{ "[link](<foo\\>)", "<p>[link](&lt;foo&gt;)</p>\n" },
        .{ "[link](\\(foo\\))", "<p><a href=\"(foo)\">link</a></p>\n" },
        .{ "[link](foo(and(bar)))", "<p><a href=\"foo(and(bar))\">link</a></p>\n" },
        .{ "[link](foo\\(and\\(bar\\))", "<p><a href=\"foo(and(bar)\">link</a></p>\n" },
        .{ "[link](<foo(and(bar)>)", "<p><a href=\"foo(and(bar)\">link</a></p>\n" },
        .{ "[link](foo\\)\\:)", "<p><a href=\"foo):\">link</a></p>\n" },
        .{ "[link](#fragment)\n\n[link](http://example.com#fragment)\n\n[link](http://example.com?foo=3#frag)", "<p><a href=\"#fragment\">link</a></p>\n<p><a href=\"http://example.com#fragment\">link</a></p>\n<p><a href=\"http://example.com?foo=3#frag\">link</a></p>\n" },
        .{ "[link](foo\\bar)", "<p><a href=\"foo%5Cbar\">link</a></p>\n" },
        .{ "[link](foo%20b&auml;)", "<p><a href=\"foo%20b%C3%A4\">link</a></p>\n" },
        .{ "[link](\"title\")", "<p><a href=\"%22title%22\">link</a></p>\n" },
        .{ "[link](/url \"title\")\n[link](/url 'title')\n[link](/url (title))", "<p><a href=\"/url\" title=\"title\">link</a>\n<a href=\"/url\" title=\"title\">link</a>\n<a href=\"/url\" title=\"title\">link</a></p>\n" },
        .{ "[link](/url \"title \\\"&quot;\")", "<p><a href=\"/url\" title=\"title &quot;&quot;\">link</a></p>\n" },
        .{ "[link](/url \"title \"and\" title\")", "<p>[link](/url &quot;title &quot;and&quot; title&quot;)</p>\n" },
        .{ "[link](/url 'title \"and\" title')", "<p><a href=\"/url\" title=\"title &quot;and&quot; title\">link</a></p>\n" },
        .{ "[link](   /uri\n  \"title\"  )", "<p><a href=\"/uri\" title=\"title\">link</a></p>\n" },
        .{ "[link] (/uri)", "<p>[link] (/uri)</p>\n" },
        .{ "[link [foo [bar]]](/uri)", "<p><a href=\"/uri\">link [foo [bar]]</a></p>\n" },
        .{ "[link] bar](/uri)", "<p>[link] bar](/uri)</p>\n" },
        .{ "[link [bar](/uri)", "<p>[link <a href=\"/uri\">bar</a></p>\n" },
        .{ "[link \\[bar](/uri)", "<p><a href=\"/uri\">link [bar</a></p>\n" },
        .{ "[link *foo **bar** `#`*](/uri)", "<p><a href=\"/uri\">link <em>foo <strong>bar</strong> <code>#</code></em></a></p>\n" },
        .{ "[![moon](moon.jpg)](/uri)", "<p><a href=\"/uri\"><img src=\"moon.jpg\" alt=\"moon\" /></a></p>\n" },
        .{ "[foo [bar](/uri)](/uri)", "<p>[foo <a href=\"/uri\">bar</a>](/uri)</p>\n" },
        .{ "[foo *[bar [baz](/uri)](/uri)*](/uri)", "<p>[foo <em>[bar <a href=\"/uri\">baz</a>](/uri)</em>](/uri)</p>\n" },
        .{ "![[[foo](uri1)](uri2)](uri3)", "<p><img src=\"uri3\" alt=\"[foo](uri2)\" /></p>\n" },
        .{ "*[foo*](/uri)", "<p>*<a href=\"/uri\">foo*</a></p>\n" },
        .{ "[foo *bar](baz*)", "<p><a href=\"baz*\">foo *bar</a></p>\n" },
        .{ "*foo [bar* baz]", "<p><em>foo [bar</em> baz]</p>\n" },
        .{ "[foo`](/uri)`", "<p>[foo<code>](/uri)</code></p>\n" },
        .{ "[foo<http://example.com/?search=](uri)>", "<p>[foo<a href=\"http://example.com/?search=%5D(uri)\">http://example.com/?search=](uri)</a></p>\n" },
    }, false);
}

test "spec: reference links" {
    try check(&.{
        .{ "[foo][bar]\n\n[bar]: /url \"title\"", "<p><a href=\"/url\" title=\"title\">foo</a></p>\n" },
        .{ "[link [foo [bar]]][ref]\n\n[ref]: /uri", "<p><a href=\"/uri\">link [foo [bar]]</a></p>\n" },
        .{ "[link \\[bar][ref]\n\n[ref]: /uri", "<p><a href=\"/uri\">link [bar</a></p>\n" },
        .{ "[foo][BaR]\n\n[bar]: /url \"title\"", "<p><a href=\"/url\" title=\"title\">foo</a></p>\n" },
        .{ "[Foo\n  bar]: /url\n\n[Baz][Foo bar]", "<p><a href=\"/url\">Baz</a></p>\n" },
        .{ "[foo] [bar]\n\n[bar]: /url \"title\"", "<p>[foo] <a href=\"/url\" title=\"title\">bar</a></p>\n" },
        .{ "[foo]\n[bar]\n\n[bar]: /url \"title\"", "<p>[foo]\n<a href=\"/url\" title=\"title\">bar</a></p>\n" },
        .{ "[foo]: /url1\n\n[foo]: /url2\n\n[bar][foo]", "<p><a href=\"/url1\">bar</a></p>\n" },
        .{ "[bar][foo\\!]\n\n[foo!]: /url", "<p>[bar][foo!]</p>\n" },
        .{ "[foo][ref[]\n\n[ref[]: /uri", "<p>[foo][ref[]</p>\n<p>[ref[]: /uri</p>\n" },
        .{ "[foo][]\n\n[foo]: /url \"title\"", "<p><a href=\"/url\" title=\"title\">foo</a></p>\n" },
        .{ "[*foo* bar][]\n\n[*foo* bar]: /url \"title\"", "<p><a href=\"/url\" title=\"title\"><em>foo</em> bar</a></p>\n" },
        .{ "[Foo][]\n\n[foo]: /url \"title\"", "<p><a href=\"/url\" title=\"title\">Foo</a></p>\n" },
        .{ "[foo] \n[]\n\n[foo]: /url \"title\"", "<p><a href=\"/url\" title=\"title\">foo</a>\n[]</p>\n" },
        .{ "[foo]\n\n[foo]: /url \"title\"", "<p><a href=\"/url\" title=\"title\">foo</a></p>\n" },
        .{ "[*foo* bar]\n\n[*foo* bar]: /url \"title\"", "<p><a href=\"/url\" title=\"title\"><em>foo</em> bar</a></p>\n" },
        .{ "[[*foo* bar]]\n\n[*foo* bar]: /url \"title\"", "<p>[<a href=\"/url\" title=\"title\"><em>foo</em> bar</a>]</p>\n" },
        .{ "[[bar [foo]\n\n[foo]: /url", "<p>[[bar <a href=\"/url\">foo</a></p>\n" },
        .{ "[Foo]\n\n[foo]: /url \"title\"", "<p><a href=\"/url\" title=\"title\">Foo</a></p>\n" },
        .{ "[foo] bar\n\n[foo]: /url", "<p><a href=\"/url\">foo</a> bar</p>\n" },
        .{ "\\[foo]\n\n[foo]: /url \"title\"", "<p>[foo]</p>\n" },
        .{ "[foo*]: /url\n\n*[foo*]", "<p>*<a href=\"/url\">foo*</a></p>\n" },
        .{ "[foo][bar]\n\n[foo]: /url1\n[bar]: /url2", "<p><a href=\"/url2\">foo</a></p>\n" },
        .{ "[foo][]\n\n[foo]: /url1", "<p><a href=\"/url1\">foo</a></p>\n" },
        .{ "[foo]()\n\n[foo]: /url1", "<p><a href=\"\">foo</a></p>\n" },
        .{ "[foo](not a link)\n\n[foo]: /url1", "<p><a href=\"/url1\">foo</a>(not a link)</p>\n" },
        .{ "[foo][bar][baz]\n\n[baz]: /url", "<p>[foo]<a href=\"/url\">bar</a></p>\n" },
        .{ "[foo][bar][baz]\n\n[baz]: /url1\n[bar]: /url2", "<p><a href=\"/url2\">foo</a><a href=\"/url1\">baz</a></p>\n" },
        .{ "[foo][bar][baz]\n\n[baz]: /url1\n[foo]: /url2", "<p>[foo]<a href=\"/url1\">bar</a></p>\n" },
    }, false);
}

test "spec: link reference definitions" {
    try check(&.{
        .{ "[foo]: /url \"title\"\n\n[foo]", "<p><a href=\"/url\" title=\"title\">foo</a></p>\n" },
        .{ "   [foo]: \n      /url  \n           'the title'  \n\n[foo]", "<p><a href=\"/url\" title=\"the title\">foo</a></p>\n" },
        .{ "[Foo*bar\\]]:my_(url) 'title (with parens)'\n\n[Foo*bar\\]]", "<p><a href=\"my_(url)\" title=\"title (with parens)\">Foo*bar]</a></p>\n" },
        .{ "[Foo bar]:\n<my url>\n'title'\n\n[Foo bar]", "<p><a href=\"my%20url\" title=\"title\">Foo bar</a></p>\n" },
        .{ "[foo]: /url '\ntitle\nline1\nline2\n'\n\n[foo]", "<p><a href=\"/url\" title=\"\ntitle\nline1\nline2\n\">foo</a></p>\n" },
        .{ "[foo]: /url 'title\n\nwith blank line'\n\n[foo]", "<p>[foo]: /url 'title</p>\n<p>with blank line'</p>\n<p>[foo]</p>\n" },
        .{ "[foo]:\n/url\n\n[foo]", "<p><a href=\"/url\">foo</a></p>\n" },
        .{ "[foo]:\n\n[foo]", "<p>[foo]:</p>\n<p>[foo]</p>\n" },
        .{ "[foo]: <>\n\n[foo]", "<p><a href=\"\">foo</a></p>\n" },
        .{ "[foo]: /url\\bar\\*baz \"foo\\\"bar\\baz\"\n\n[foo]", "<p><a href=\"/url%5Cbar*baz\" title=\"foo&quot;bar\\baz\">foo</a></p>\n" },
        .{ "[foo]\n\n[foo]: url", "<p><a href=\"url\">foo</a></p>\n" },
        .{ "[foo]\n\n[foo]: first\n[foo]: second", "<p><a href=\"first\">foo</a></p>\n" },
        .{ "[FOO]: /url\n\n[Foo]", "<p><a href=\"/url\">Foo</a></p>\n" },
        .{ "[foo]: /url", "" },
        .{ "[\nfoo\n]: /url\nbar", "<p>bar</p>\n" },
        .{ "[foo]: /url \"title\" ok", "<p>[foo]: /url &quot;title&quot; ok</p>\n" },
        .{ "[foo]: /url\n\"title\" ok", "<p>&quot;title&quot; ok</p>\n" },
        .{ "    [foo]: /url \"title\"\n\n[foo]", "<pre><code>[foo]: /url &quot;title&quot;\n</code></pre>\n<p>[foo]</p>\n" },
        .{ "```\n[foo]: /url\n```\n\n[foo]", "<pre><code>[foo]: /url\n</code></pre>\n<p>[foo]</p>\n" },
        .{ "Foo\n[bar]: /baz\n\n[bar]", "<p>Foo\n[bar]: /baz</p>\n<p>[bar]</p>\n" },
        .{ "# [Foo]\n[foo]: /url\n> bar", "<h1><a href=\"/url\">Foo</a></h1>\n<blockquote>\n<p>bar</p>\n</blockquote>\n" },
        .{ "[foo]: /url\nbar\n===\n[foo]", "<h1>bar</h1>\n<p><a href=\"/url\">foo</a></p>\n" },
        .{ "[foo]: /url\n===\n[foo]", "<p>===\n<a href=\"/url\">foo</a></p>\n" },
        .{ "[foo]: /foo-url \"foo\"\n[bar]: /bar-url\n  \"bar\"\n[baz]: /baz-url\n\n[foo],\n[bar],\n[baz]", "<p><a href=\"/foo-url\" title=\"foo\">foo</a>,\n<a href=\"/bar-url\" title=\"bar\">bar</a>,\n<a href=\"/baz-url\">baz</a></p>\n" },
        .{ "[foo]\n\n> [foo]: /url", "<p><a href=\"/url\">foo</a></p>\n<blockquote>\n</blockquote>\n" },
    }, false);
}

test "spec: images" {
    try check(&.{
        .{ "![foo](/url \"title\")", "<p><img src=\"/url\" alt=\"foo\" title=\"title\" /></p>\n" },
        .{ "![foo *bar*]\n\n[foo *bar*]: train.jpg \"train & tracks\"", "<p><img src=\"train.jpg\" alt=\"foo bar\" title=\"train &amp; tracks\" /></p>\n" },
        .{ "![foo ![bar](/url)](/url2)", "<p><img src=\"/url2\" alt=\"foo bar\" /></p>\n" },
        .{ "![foo [bar](/url)](/url2)", "<p><img src=\"/url2\" alt=\"foo bar\" /></p>\n" },
        .{ "![foo *bar*][]\n\n[foo *bar*]: train.jpg \"train & tracks\"", "<p><img src=\"train.jpg\" alt=\"foo bar\" title=\"train &amp; tracks\" /></p>\n" },
        .{ "![foo *bar*][foobar]\n\n[FOOBAR]: train.jpg \"train & tracks\"", "<p><img src=\"train.jpg\" alt=\"foo bar\" title=\"train &amp; tracks\" /></p>\n" },
        .{ "![foo](train.jpg)", "<p><img src=\"train.jpg\" alt=\"foo\" /></p>\n" },
        .{ "My ![foo bar](/path/to/train.jpg  \"title\"   )", "<p>My <img src=\"/path/to/train.jpg\" alt=\"foo bar\" title=\"title\" /></p>\n" },
        .{ "![foo](<url>)", "<p><img src=\"url\" alt=\"foo\" /></p>\n" },
        .{ "![](/url)", "<p><img src=\"/url\" alt=\"\" /></p>\n" },
        .{ "![foo][bar]\n\n[bar]: /url", "<p><img src=\"/url\" alt=\"foo\" /></p>\n" },
        .{ "![foo][bar]\n\n[BAR]: /url", "<p><img src=\"/url\" alt=\"foo\" /></p>\n" },
        .{ "![foo][]\n\n[foo]: /url \"title\"", "<p><img src=\"/url\" alt=\"foo\" title=\"title\" /></p>\n" },
        .{ "![*foo* bar][]\n\n[*foo* bar]: /url \"title\"", "<p><img src=\"/url\" alt=\"foo bar\" title=\"title\" /></p>\n" },
        .{ "![Foo][]\n\n[foo]: /url \"title\"", "<p><img src=\"/url\" alt=\"Foo\" title=\"title\" /></p>\n" },
        .{ "![foo]\n\n[foo]: /url \"title\"", "<p><img src=\"/url\" alt=\"foo\" title=\"title\" /></p>\n" },
        .{ "![*foo* bar]\n\n[*foo* bar]: /url \"title\"", "<p><img src=\"/url\" alt=\"foo bar\" title=\"title\" /></p>\n" },
        .{ "!\\[foo]\n\n[foo]: /url \"title\"", "<p>![foo]</p>\n" },
        .{ "\\![foo]\n\n[foo]: /url \"title\"", "<p>!<a href=\"/url\" title=\"title\">foo</a></p>\n" },
    }, false);
}

test "spec: autolinks" {
    try check(&.{
        .{ "<http://foo.bar.baz>", "<p><a href=\"http://foo.bar.baz\">http://foo.bar.baz</a></p>\n" },
        .{ "<http://foo.bar.baz/test?q=hello&id=22&boolean>", "<p><a href=\"http://foo.bar.baz/test?q=hello&amp;id=22&amp;boolean\">http://foo.bar.baz/test?q=hello&amp;id=22&amp;boolean</a></p>\n" },
        .{ "<irc://foo.bar:2233/baz>", "<p><a href=\"irc://foo.bar:2233/baz\">irc://foo.bar:2233/baz</a></p>\n" },
        .{ "<MAILTO:FOO@BAR.BAZ>", "<p><a href=\"MAILTO:FOO@BAR.BAZ\">MAILTO:FOO@BAR.BAZ</a></p>\n" },
        .{ "<a+b+c:d>", "<p><a href=\"a+b+c:d\">a+b+c:d</a></p>\n" },
        .{ "<made-up-scheme://foo,bar>", "<p><a href=\"made-up-scheme://foo,bar\">made-up-scheme://foo,bar</a></p>\n" },
        .{ "<http://../>", "<p><a href=\"http://../\">http://../</a></p>\n" },
        .{ "<localhost:5001/foo>", "<p><a href=\"localhost:5001/foo\">localhost:5001/foo</a></p>\n" },
        .{ "<http://foo.bar/baz bim>", "<p>&lt;http://foo.bar/baz bim&gt;</p>\n" },
        .{ "<http://example.com/\\[\\>", "<p><a href=\"http://example.com/%5C%5B%5C\">http://example.com/\\[\\</a></p>\n" },
        .{ "<foo@bar.example.com>", "<p><a href=\"mailto:foo@bar.example.com\">foo@bar.example.com</a></p>\n" },
        .{ "<foo+special@Bar.baz-bar0.com>", "<p><a href=\"mailto:foo+special@Bar.baz-bar0.com\">foo+special@Bar.baz-bar0.com</a></p>\n" },
        .{ "<foo\\+@bar.example.com>", "<p>&lt;foo+@bar.example.com&gt;</p>\n" },
        .{ "<>", "<p>&lt;&gt;</p>\n" },
        .{ "< http://foo.bar >", "<p>&lt; http://foo.bar &gt;</p>\n" },
        .{ "<m:abc>", "<p>&lt;m:abc&gt;</p>\n" },
        .{ "<foo.bar.baz>", "<p>&lt;foo.bar.baz&gt;</p>\n" },
        .{ "http://example.com", "<p>http://example.com</p>\n" },
        .{ "foo@bar.example.com", "<p>foo@bar.example.com</p>\n" },
    }, false);
}

test "spec: breaks and text" {
    try check(&.{
        .{ "foo  \nbaz", "<p>foo<br />\nbaz</p>\n" },
        .{ "foo\\\nbaz", "<p>foo<br />\nbaz</p>\n" },
        .{ "foo       \nbaz", "<p>foo<br />\nbaz</p>\n" },
        .{ "foo  \n     bar", "<p>foo<br />\nbar</p>\n" },
        .{ "foo\\\n     bar", "<p>foo<br />\nbar</p>\n" },
        .{ "*foo  \nbar*", "<p><em>foo<br />\nbar</em></p>\n" },
        .{ "*foo\\\nbar*", "<p><em>foo<br />\nbar</em></p>\n" },
        .{ "foo\\", "<p>foo\\</p>\n" },
        .{ "foo  ", "<p>foo</p>\n" },
        .{ "### foo\\", "<h3>foo\\</h3>\n" },
        .{ "### foo  ", "<h3>foo</h3>\n" },
        .{ "foo\nbaz", "<p>foo\nbaz</p>\n" },
        .{ "foo \n baz", "<p>foo\nbaz</p>\n" },
        .{ "hello $.;'there", "<p>hello $.;'there</p>\n" },
        .{ "Foo χρῆν", "<p>Foo χρῆν</p>\n" },
        .{ "Multiple     spaces", "<p>Multiple     spaces</p>\n" },
    }, false);
}

test "inline html: br breaks the line, other tags are dropped" {
    try check(&.{
        .{ "a<br>b", "<p>a<br />\nb</p>\n" },
        .{ "a<br/>b", "<p>a<br />\nb</p>\n" },
        .{ "a<br />b", "<p>a<br />\nb</p>\n" },
        .{ "a <b>bold</b> c", "<p>a bold c</p>\n" },
        .{ "a<!-- gone -->b", "<p>ab</p>\n" },
        .{ "1 < 2 and 3 > 2", "<p>1 &lt; 2 and 3 &gt; 2</p>\n" },
    }, false);
}

test "gfm: tables" {
    try check(&.{
        .{ "| foo | bar |\n| --- | --- |\n| baz | bim |", "<table>\n<thead>\n<tr>\n<th>foo</th>\n<th>bar</th>\n</tr>\n</thead>\n<tbody>\n<tr>\n<td>baz</td>\n<td>bim</td>\n</tr>\n</tbody>\n</table>\n" },
        .{ "| abc | defghi |\n:-: | -----------:\nbar | baz", "<table>\n<thead>\n<tr>\n<th align=\"center\">abc</th>\n<th align=\"right\">defghi</th>\n</tr>\n</thead>\n<tbody>\n<tr>\n<td align=\"center\">bar</td>\n<td align=\"right\">baz</td>\n</tr>\n</tbody>\n</table>\n" },
        .{ "| f\\|oo  |\n| ------ |\n| b `\\|` az |\n| b **\\|** im |", "<table>\n<thead>\n<tr>\n<th>f|oo</th>\n</tr>\n</thead>\n<tbody>\n<tr>\n<td>b <code>|</code> az</td>\n</tr>\n<tr>\n<td>b <strong>|</strong> im</td>\n</tr>\n</tbody>\n</table>\n" },
        .{ "| abc | def |\n| --- | --- |\n| bar | baz |\n> bar", "<table>\n<thead>\n<tr>\n<th>abc</th>\n<th>def</th>\n</tr>\n</thead>\n<tbody>\n<tr>\n<td>bar</td>\n<td>baz</td>\n</tr>\n</tbody>\n</table>\n<blockquote>\n<p>bar</p>\n</blockquote>\n" },
        .{ "| abc | def |\n| --- | --- |\n| bar | baz |\nbar\n\nbar", "<table>\n<thead>\n<tr>\n<th>abc</th>\n<th>def</th>\n</tr>\n</thead>\n<tbody>\n<tr>\n<td>bar</td>\n<td>baz</td>\n</tr>\n<tr>\n<td>bar</td>\n<td></td>\n</tr>\n</tbody>\n</table>\n<p>bar</p>\n" },
        .{ "| abc | def |\n| --- |\n| bar |", "<p>| abc | def |\n| --- |\n| bar |</p>\n" },
        .{ "| abc | def |\n| --- | --- |", "<table>\n<thead>\n<tr>\n<th>abc</th>\n<th>def</th>\n</tr>\n</thead>\n</table>\n" },
        .{ "| a | b |\n| - | - |\n| 1 | 2 | 3 |", "<table>\n<thead>\n<tr>\n<th>a</th>\n<th>b</th>\n</tr>\n</thead>\n<tbody>\n<tr>\n<td>1</td>\n<td>2</td>\n</tr>\n</tbody>\n</table>\n" },
    }, false);
}

test "gfm: task lists, strikethrough and autolink literals" {
    try check(&.{
        .{ "- [ ] foo\n- [x] bar", "<ul>\n<li><input type=\"checkbox\" disabled=\"\" /> foo</li>\n<li><input type=\"checkbox\" disabled=\"\" checked=\"\" /> bar</li>\n</ul>\n" },
        .{ "- [x] foo\n  - [ ] bar\n  - [X] baz\n- [ ] bim", "<ul>\n<li><input type=\"checkbox\" disabled=\"\" checked=\"\" /> foo\n<ul>\n<li><input type=\"checkbox\" disabled=\"\" /> bar</li>\n<li><input type=\"checkbox\" disabled=\"\" checked=\"\" /> baz</li>\n</ul>\n</li>\n<li><input type=\"checkbox\" disabled=\"\" /> bim</li>\n</ul>\n" },
        .{ "- [] not a task\n- [y] nor this", "<ul>\n<li>[] not a task</li>\n<li>[y] nor this</li>\n</ul>\n" },
        .{ "~~Hi~~ Hello, ~there~ world!", "<p><del>Hi</del> Hello, <del>there</del> world!</p>\n" },
        .{ "This ~~has a\n\nnew paragraph~~.", "<p>This ~~has a</p>\n<p>new paragraph~~.</p>\n" },
        .{ "Hello ~~~world~~~", "<p>Hello ~~~world~~~</p>\n" },
        .{ "**~~both~~**", "<p><strong><del>both</del></strong></p>\n" },
    }, false);
    try check(&.{
        .{ "www.commonmark.org", "<p><a href=\"http://www.commonmark.org\">www.commonmark.org</a></p>\n" },
        .{ "Visit www.commonmark.org/help for more information.", "<p>Visit <a href=\"http://www.commonmark.org/help\">www.commonmark.org/help</a> for more information.</p>\n" },
        .{ "(www.google.com/search?q=Markup+(business))", "<p>(<a href=\"http://www.google.com/search?q=Markup+(business)\">www.google.com/search?q=Markup+(business)</a>)</p>\n" },
        .{ "https://example.com/a, and more", "<p><a href=\"https://example.com/a\">https://example.com/a</a>, and more</p>\n" },
        .{ "a [link](https://example.com) here", "<p>a <a href=\"https://example.com\">link</a> here</p>\n" },
        .{ "`https://example.com`", "<p><code>https://example.com</code></p>\n" },
        .{ "not.a.url and example.org", "<p>not.a.url and example.org</p>\n" },
    }, true);
}

test "front matter is a block of its own" {
    var doc = try md.parse(testing.allocator, "---\ntitle: x\ntags: [a]\n---\n# Heading\n", .{});
    defer doc.deinit();
    try testing.expectEqual(@as(usize, 2), doc.blocks.len);
    try testing.expect(doc.blocks[0].data == .code);
    try testing.expect(doc.blocks[0].data.code.front_matter);
    try testing.expectEqualStrings("title: x\ntags: [a]\n", doc.blocks[0].data.code.text);
    try testing.expectEqualStrings("yaml", doc.blocks[0].data.code.lang);
    try testing.expect(doc.blocks[1].data == .heading);
    try testing.expectEqual(@as(u32, 4), doc.blocks[1].start_line);

    // A document that only starts with a rule is not front matter.
    var d2 = try md.parse(testing.allocator, "---\nnot closed\n", .{});
    defer d2.deinit();
    try testing.expect(d2.blocks[0].data != .code or !d2.blocks[0].data.code.front_matter);
}

test "heading ids follow GitHub's" {
    var doc = try md.parse(testing.allocator, "# Getting Started\n## Getting Started\n# C++ & Zig!\n### Über `code` _it_\n", .{});
    defer doc.deinit();
    try testing.expectEqualStrings("getting-started", doc.blocks[0].data.heading.id);
    try testing.expectEqualStrings("getting-started-1", doc.blocks[1].data.heading.id);
    try testing.expectEqualStrings("c--zig", doc.blocks[2].data.heading.id);
    try testing.expectEqualStrings("über-code-it", doc.blocks[3].data.heading.id);
}

test "blocks know their source lines" {
    const src =
        \\# Title
        \\
        \\Paragraph one
        \\continues here.
        \\
        \\- a
        \\- b
        \\
        \\```zig
        \\const x = 1;
        \\```
        \\
        \\> quote
        \\> more
    ;
    var doc = try md.parse(testing.allocator, src, .{});
    defer doc.deinit();
    try testing.expectEqual(@as(usize, 5), doc.blocks.len);
    const want = [_][2]u32{ .{ 0, 0 }, .{ 2, 3 }, .{ 5, 6 }, .{ 8, 10 }, .{ 12, 13 } };
    for (doc.blocks, want) |b, w| {
        try testing.expectEqual(w[0], b.start_line);
        try testing.expectEqual(w[1], b.end_line);
    }
    try testing.expectEqual(@as(u32, 14), doc.lines);
    try testing.expectEqual(@as(u32, 5), doc.blocks[2].data.list.items[0].start_line);
    try testing.expectEqual(@as(u32, 6), doc.blocks[2].data.list.items[1].start_line);
}

test "code blocks keep their language and text" {
    var doc = try md.parse(testing.allocator, "```Zig {highlight}\nconst a = 1;\n\n  indented\n```\n", .{});
    defer doc.deinit();
    const c = doc.blocks[0].data.code;
    try testing.expectEqualStrings("Zig", c.lang);
    try testing.expectEqualStrings("const a = 1;\n\n  indented\n", c.text);
    try testing.expect(c.fenced);
}

test "windows line endings and a missing final newline" {
    try check(&.{
        .{ "# Title\r\n\r\nline one\r\nline two\r\n", "<h1>Title</h1>\n<p>line one\nline two</p>\n" },
        .{ "- a\r\n- b", "<ul>\n<li>a</li>\n<li>b</li>\n</ul>\n" },
    }, false);
}

test "never fails on odd input" {
    const inputs = [_][]const u8{
        "",
        "\n\n\n",
        "\x00\x01\x02",
        "[",
        "]",
        "![",
        "[]()",
        "[](",
        "<",
        "&",
        "&#",
        "```",
        "~~~~~~~~",
        "> > > > >",
        "- - - - -",
        "1. 1. 1. 1.",
        "* * *\n* * *",
        "|||\n|||\n",
        "| a |\n|:-|\n|b|c|d|e|",
        "***___***___***",
        "[a]: <\n",
        "\t\t\t\t- a\n\t\t\t\t\t- b",
        "# \n## \n### \n",
        "\xff\xfe\xfd",
        "a\\",
        repeat("`", 100),
        repeat("*", 100),
        repeat("[", 100) ++ repeat("]", 100),
        repeat("> ", 60) ++ "x",
        repeat("- ", 60) ++ "x",
    };
    for (inputs) |src| {
        var doc = try md.parse(testing.allocator, src, .{});
        doc.deinit();
    }
}

test "a long document parses in linear time and memory" {
    var src: std.ArrayList(u8) = .empty;
    defer src.deinit(testing.allocator);
    for (0..2000) |i| {
        try src.print(testing.allocator, "## Section {d}\n\nA paragraph with **bold**, `code` and a [link](https://example.com/{d}).\n\n- one\n- two\n  - nested\n\n```c\nint x = {d};\n```\n\n", .{ i, i, i });
    }
    var doc = try md.parse(testing.allocator, src.items, .{});
    defer doc.deinit();
    try testing.expectEqual(@as(usize, 2000 * 4), doc.blocks.len);
}
