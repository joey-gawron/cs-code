using System.Text.Json;
using CsCode.Platform.Context;
using CsCode.Platform.Tests.Support;
using CsCode.TestDoubles;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CsCode.Platform.Tests;

public class WhenClauseTests
{
    private static readonly JsonElement JsonArr = JsonDocument.Parse("""["a", 2, true]""").RootElement.Clone();
    private static readonly JsonElement JsonObj = JsonDocument.Parse("""{"k": 1, "z": null}""").RootElement.Clone();

    /// <summary>The shared context every case in this file is evaluated against.</summary>
    private static ContextKeyService Ctx(TestConfigurationService? cfg = null)
    {
        var c = new ContextKeyService(cfg);
        c.Set("a", true);
        c.Set("b", false);
        c.Set("c", true);
        c.Set("n", 5);
        c.Set("neg", -3);
        c.Set("pi", 3.14);
        c.Set("zero", 0);
        c.Set("s", "hello");
        c.Set("empty", "");
        c.Set("lang", "csharp");
        c.Set("ext", ".cs");
        c.Set("fn", "Program.cs");
        c.Set("ver", "10");
        c.Set("x", "a");
        c.Set("y", "z");
        c.Set("frag", "ell");
        c.Set("path", "a/b");
        c.Set("multi", "first\nsecond");
        c.Set("uni", "héllo wörld");
        c.Set("jp", "日本語");
        c.Set("größe", true);
        c.Set("view.some-thing:x", true);
        c.Set("dbg", "stopped");
        c.Set("list", new[] { "a", "b" });
        c.Set("ids", new List<string> { "a", "q" });
        c.Set("set", new HashSet<string> { "a", "m" });
        c.Set("dict", new Dictionary<string, bool> { ["a"] = true });
        c.Set("nums", new object?[] { 1, 5 });
        c.Set("jarr", JsonArr);
        c.Set("jobj", JsonObj);
        c.Set("jstr", JsonDocument.Parse("\"hello\"").RootElement.Clone());
        c.Set("jnum", JsonDocument.Parse("7").RootElement.Clone());
        c.Set("jtrue", JsonDocument.Parse("true").RootElement.Clone());
        c.Set("jfalse", JsonDocument.Parse("false").RootElement.Clone());
        return c;
    }

    // ---- boolean structure -------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("a", true)]
    [InlineData("b", false)]
    [InlineData("unknown", false)]
    [InlineData("!a", false)]
    [InlineData("!b", true)]
    [InlineData("!unknown", true)]
    [InlineData("!!a", true)]
    [InlineData("!!b", false)]
    [InlineData("a && c", true)]
    [InlineData("a && b", false)]
    [InlineData("b && a", false)]
    [InlineData("a || b", true)]
    [InlineData("b || a", true)]
    [InlineData("b || b", false)]
    [InlineData("a&&c", true)]
    [InlineData("a||b", true)]
    [InlineData("  a   &&   c  ", true)]
    [InlineData("a &&\n c", true)]
    [InlineData("a\t&&\tc", true)]
    [InlineData("b || a && b", false)]            // && binds tighter than ||: b || (a && b)
    [InlineData("a || b && b", true)]             // a || (b && b)
    [InlineData("b && a || a", true)]             // (b && a) || a
    [InlineData("(a || b) && b", false)]
    [InlineData("(a || b) && c", true)]
    [InlineData("a && (b || c)", true)]
    [InlineData("!(a && b)", true)]
    [InlineData("!(a || c)", false)]
    [InlineData("!a || c", true)]
    [InlineData("!a && !b", false)]
    [InlineData("!b && a", true)]
    [InlineData("((a))", true)]
    [InlineData("(((!b)))", true)]
    [InlineData("a && b && c", false)]
    [InlineData("a && c && a && c", true)]
    [InlineData("b || b || b || a", true)]
    [InlineData("!(!a)", true)]
    [InlineData("!unknown && !alsoUnknown", true)]
    public void Boolean_structure_and_precedence(string clause, bool expected) => Assert.Equal(expected, Ctx().Evaluate(clause));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void Empty_clause_is_true(string? clause) => Assert.True(Ctx().Evaluate(clause));

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("!false", true)]
    [InlineData("true && a", true)]
    [InlineData("false || a", true)]
    [InlineData("false && a", false)]
    [InlineData("true && false", false)]
    public void Constants(string clause, bool expected) => Assert.Equal(expected, Ctx().Evaluate(clause));

    // ---- truthiness & key names --------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("n", true)]
    [InlineData("zero", false)]
    [InlineData("neg", true)]
    [InlineData("s", true)]
    [InlineData("empty", false)]
    [InlineData("list", true)]
    [InlineData("jstr", true)]
    [InlineData("jnum", true)]
    [InlineData("jtrue", true)]
    [InlineData("jfalse", false)]
    [InlineData("jarr", true)]
    [InlineData("jobj", true)]
    [InlineData("größe", true)]                    // unicode key
    [InlineData("view.some-thing:x", true)]        // dots, dashes, colons inside a key
    public void Bare_key_truthiness_follows_javascript_rules(string clause, bool expected) => Assert.Equal(expected, Ctx().Evaluate(clause));

    [Fact]
    public void Null_value_is_falsy_and_removes_a_root_key()
    {
        var c = Ctx();
        c.Set("a", null);
        Assert.False(c.Evaluate("a"));
        Assert.Null(c.Get("a"));
    }

    // ---- equality ----------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("lang == csharp", true)]
    [InlineData("lang == 'csharp'", true)]
    [InlineData("lang == \"csharp\"", true)]
    [InlineData("lang == fsharp", false)]
    [InlineData("lang != fsharp", true)]
    [InlineData("lang != csharp", false)]
    [InlineData("lang == Csharp", false)]           // case-sensitive
    [InlineData("ext == .cs", true)]
    [InlineData("ext == '.cs'", true)]
    [InlineData("fn == 'Program.cs'", true)]
    [InlineData("fn=='Program.cs'", true)]
    [InlineData("unknown == x", false)]
    [InlineData("unknown != x", true)]
    [InlineData("empty == ''", true)]
    [InlineData("unknown == ''", false)]            // an unset key is not the empty string
    [InlineData("uni == 'héllo wörld'", true)]
    [InlineData("jp == 日本語", true)]
    [InlineData("lang == 'c sharp'", false)]        // a quoted value with a space does not break tokenising
    [InlineData("s == hello && lang == csharp", true)]
    [InlineData("dbg == stopped", true)]
    [InlineData("dbg != stopped", false)]
    [InlineData("jstr == hello", true)]
    [InlineData("jstr != hello", false)]
    public void String_equality(string clause, bool expected) => Assert.Equal(expected, Ctx().Evaluate(clause));

    [Theory]
    [InlineData("n == 5", true)]
    [InlineData("n == 6", false)]
    [InlineData("n != 6", true)]
    [InlineData("n == '5'", true)]                  // loose: number vs numeric text
    [InlineData("n == 5.0", true)]
    [InlineData("neg == -3", true)]
    [InlineData("pi == 3.14", true)]
    [InlineData("ver == 10", true)]                 // text "10" vs literal 10: same text
    [InlineData("ver == 10.0", false)]              // two strings compare ordinally
    [InlineData("jnum == 7", true)]
    [InlineData("jnum != 7", false)]
    [InlineData("n == five", false)]
    public void Numeric_equality(string clause, bool expected) => Assert.Equal(expected, Ctx().Evaluate(clause));

    [Theory]
    [InlineData("n === 5", true)]
    [InlineData("n === '5'", false)]                // strict: number vs string
    [InlineData("ver === 10", false)]               // strict: string vs number
    [InlineData("ver === '10'", true)]
    [InlineData("lang === csharp", true)]
    [InlineData("lang !== csharp", false)]
    [InlineData("lang !== fsharp", true)]
    [InlineData("n !== 5", false)]
    [InlineData("n !== '5'", true)]
    [InlineData("a === true", true)]
    [InlineData("n === true", false)]
    [InlineData("jtrue === true", true)]
    [InlineData("jnum === 7", true)]
    [InlineData("unknown === false", false)]        // strict: unset is not the boolean false
    public void Strict_equality(string clause, bool expected) => Assert.Equal(expected, Ctx().Evaluate(clause));

    [Theory]
    [InlineData("a == true", true)]
    [InlineData("b == true", false)]
    [InlineData("b == false", true)]
    [InlineData("unknown == false", true)]          // loose: `== false` is `!key`
    [InlineData("a != true", false)]
    [InlineData("n == true", true)]                 // truthy
    [InlineData("zero == false", true)]
    public void Boolean_literals_compare_by_truthiness(string clause, bool expected) => Assert.Equal(expected, Ctx().Evaluate(clause));

    // ---- ordering ----------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("n > 4", true)]
    [InlineData("n > 5", false)]
    [InlineData("n >= 5", true)]
    [InlineData("n < 6", true)]
    [InlineData("n < 5", false)]
    [InlineData("n <= 5", true)]
    [InlineData("n <= 4", false)]
    [InlineData("n>4", true)]
    [InlineData("neg < 0", true)]
    [InlineData("n > -1", true)]
    [InlineData("pi < 3.2", true)]
    [InlineData("pi > 3.2", false)]
    [InlineData("ver > 9", true)]                   // numeric text compares as a number
    [InlineData("ver >= 10", true)]
    [InlineData("ver < 10", false)]
    [InlineData("jnum >= 7", true)]
    [InlineData("s > 1", false)]                    // non-numeric: every ordering is false
    [InlineData("s < 1", false)]
    [InlineData("unknown < 5", false)]
    [InlineData("unknown >= 0", false)]
    [InlineData("n > 1 && n < 10", true)]
    public void Numeric_ordering(string clause, bool expected) => Assert.Equal(expected, Ctx().Evaluate(clause));

    // ---- regex -------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("lang =~ /^c/", true)]
    [InlineData("lang =~ /^sharp/", false)]
    [InlineData("lang =~ /SHARP/", false)]
    [InlineData("lang =~ /SHARP/i", true)]
    [InlineData("fn =~ /\\.cs$/", true)]
    [InlineData("ext =~ /^\\.(cs|vb)$/", true)]
    [InlineData("lang =~ /^(c|f)sharp$/", true)]
    [InlineData("unknown =~ /^$/", true)]           // unset matches as the empty string
    [InlineData("unknown =~ /x/", false)]
    [InlineData("n =~ /^\\d+$/", true)]             // numbers match by their text
    [InlineData("path =~ /a\\/b/", true)]           // escaped delimiter
    [InlineData("path =~ /[/]/", true)]             // '/' inside a character class does not end the regex literal; "a/b" contains one
    [InlineData("multi =~ /^second$/m", true)]
    [InlineData("multi =~ /^second$/", false)]
    [InlineData("multi =~ /first.second/s", true)]
    [InlineData("multi =~ /first.second/", false)]
    [InlineData("lang =~ /(?<=c)sharp/", true)]     // lookbehind: needs the backtracking fallback engine
    [InlineData("lang =~ /^(c)s.*\\1$/", false)]    // back-reference: same
    [InlineData("a =~ /true/", true)]               // booleans match by their text
    [InlineData("jstr =~ /^hel/", true)]
    [InlineData("uni =~ /wörld$/", true)]
    [InlineData("lang =~ /^c/ && fn =~ /\\.cs$/", true)]
    [InlineData("!(lang =~ /^c/)", false)]
    public void Regex_matching(string clause, bool expected) => Assert.Equal(expected, Ctx().Evaluate(clause));

    // ---- in / not in -------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("x in list", true)]
    [InlineData("y in list", false)]
    [InlineData("x not in list", false)]
    [InlineData("y not in list", true)]
    [InlineData("'a' in list", true)]
    [InlineData("'q' in list", false)]
    [InlineData("'q' not in list", true)]
    [InlineData("x in ids", true)]                  // List<string>
    [InlineData("x in set", true)]                  // HashSet<string>
    [InlineData("y in set", false)]
    [InlineData("x in dict", true)]                 // dictionary: key membership
    [InlineData("y in dict", false)]
    [InlineData("n in nums", true)]                 // object?[] with numbers
    [InlineData("zero in nums", false)]
    [InlineData("x in jarr", true)]                 // JSON array
    [InlineData("y in jarr", false)]
    [InlineData("n in jarr", false)]
    [InlineData("x in jobj", false)]
    [InlineData("'k' in jobj", true)]               // JSON object: property membership
    [InlineData("'nope' in jobj", false)]
    [InlineData("x in unknown", false)]
    [InlineData("x not in unknown", true)]
    [InlineData("unknown in list", false)]
    [InlineData("frag in s", true)]                 // string on the right: substring
    [InlineData("x in list && !(y in list)", true)]
    public void Membership(string clause, bool expected) => Assert.Equal(expected, Ctx().Evaluate(clause));

    // ---- config.* keys -----------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("config.editor.minimap.enabled", true)]
    [InlineData("!config.editor.minimap.enabled", false)]
    [InlineData("config.editor.tabSize == 4", true)]
    [InlineData("config.editor.tabSize > 2", true)]
    [InlineData("config.editor.tabSize >= 5", false)]
    [InlineData("config.editor.wordWrap == 'on'", true)]
    [InlineData("config.editor.wordWrap =~ /^o/", true)]
    [InlineData("config.missing.setting", false)]
    [InlineData("!config.missing.setting", true)]
    [InlineData("config.editor.minimap.enabled && lang == csharp", true)]
    [InlineData("config.editor.formatOnSave", false)]
    public void Config_keys_read_live_settings(string clause, bool expected)
    {
        var cfg = new TestConfigurationService();
        cfg.Set("editor.minimap.enabled", true);
        cfg.Set("editor.tabSize", 4);
        cfg.Set("editor.wordWrap", "on");
        cfg.Set("editor.formatOnSave", false);
        Assert.Equal(expected, Ctx(cfg).Evaluate(clause));
    }

    [Fact]
    public void Config_values_are_live_not_snapshotted()
    {
        var cfg = new TestConfigurationService();
        cfg.Set("editor.tabSize", 4);
        var c = Ctx(cfg);
        Assert.True(c.Evaluate("config.editor.tabSize == 4"));
        cfg.Set("editor.tabSize", 8);
        Assert.False(c.Evaluate("config.editor.tabSize == 4"));
        Assert.True(c.Evaluate("config.editor.tabSize == 8"));
    }

    [Fact]
    public void Config_in_clause_uses_a_config_collection()
    {
        var cfg = new TestConfigurationService();
        cfg.Set("feature.langs", new[] { "csharp", "fsharp" });
        Assert.True(Ctx(cfg).Evaluate("lang in config.feature.langs"));
    }

    [Fact]
    public void Config_changes_surface_as_config_keys_on_the_changed_event()
    {
        var cfg = new TestConfigurationService();
        var c = Ctx(cfg);
        var seen = new List<string>();
        using var sub = c.Changed.Subscribe(keys => seen.AddRange(keys));
        cfg.Set("editor.fontSize", 16);
        Assert.Equal(["config.editor.fontSize"], seen);
    }

    // ---- malformed input never throws and is false -------------------------------------------------------------------------------

    [Theory]
    [InlineData("a &&")]
    [InlineData("&& a")]
    [InlineData("a || || b")]
    [InlineData("(a")]
    [InlineData("a)")]
    [InlineData("()")]
    [InlineData("a ==")]
    [InlineData("a =")]
    [InlineData("a & b")]
    [InlineData("a | b")]
    [InlineData("!")]
    [InlineData("a b")]
    [InlineData("'x'")]
    [InlineData("a == == b")]
    [InlineData("a =~")]
    [InlineData("a =~ b")]
    [InlineData("== a")]
    [InlineData("a in")]
    [InlineData("a not b")]
    [InlineData("a == 'unterminated")]
    [InlineData("a =~ /unterminated")]
    [InlineData("lang =~ /(/")]
    [InlineData("lang =~ /x/q")]
    [InlineData("n > abc")]
    [InlineData("n >")]
    [InlineData("a == b == c")]
    [InlineData("a <")]
    [InlineData("<<<<")]
    public void Malformed_clauses_evaluate_to_false_and_never_throw(string clause)
    {
        var c = Ctx();
        var ex = Record.Exception(() => Assert.False(c.Evaluate(clause)));
        Assert.Null(ex);
        Assert.NotNull(WhenClause.Validate(clause));
    }

    [Fact]
    public void Absurd_nesting_is_rejected_not_a_stack_overflow()
    {
        var clause = new string('(', 5000) + "a" + new string(')', 5000);
        Assert.False(Ctx().Evaluate(clause));
        var nots = new string('!', 5000) + "a";
        Assert.False(Ctx().Evaluate(nots));
    }

    [Fact]
    public void Validate_accepts_good_clauses_and_reports_bad_ones()
    {
        Assert.Null(WhenClause.Validate("a && !b || (c == 'x')"));
        Assert.Null(WhenClause.Validate(""));
        Assert.Null(WhenClause.Validate(null));
        Assert.Contains("'&&'", WhenClause.Validate("a & b"));
    }

    [Fact]
    public void GetKeys_lists_every_key_the_clause_reads()
    {
        var keys = WhenClause.GetKeys("a && !b || lang == csharp || x in list || n > 3 || config.editor.tabSize == 4 || fn =~ /x/");
        Assert.Equal(["a", "b", "config.editor.tabSize", "fn", "lang", "list", "n", "x"], keys.Order().ToArray());
        Assert.Empty(WhenClause.GetKeys("a &&"));
    }

    [Fact]
    public void A_malformed_clause_is_logged_once_and_then_served_from_the_cache()
    {
        var log = new CapturingLogger<ContextKeyService>();
        var c = new ContextKeyService(null, log);
        for (var i = 0; i < 5; i++) Assert.False(c.Evaluate("a &&"));
        var entries = log.Entries.Where(e => e.Level == LogLevel.Warning).ToArray();
        Assert.Single(entries);
        Assert.Contains("a &&", entries[0].Message);
    }

    // ---- parse cache -------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Clauses_are_parsed_once_and_cached()
    {
        var c = Ctx();
        for (var i = 0; i < 100; i++) c.Evaluate("a && b");
        Assert.Equal(1, c.CachedClauseCount);
    }

    [Fact]
    public void The_parse_cache_is_bounded()
    {
        var c = Ctx();
        for (var i = 0; i < 10_000; i++) c.Evaluate($"key{i} == v{i}");
        Assert.InRange(c.CachedClauseCount, 1, 2048);
        Assert.True(c.Evaluate("a")); // still correct after a reset
    }

    // ---- values, scopes, change events -------------------------------------------------------------------------------------------

    [Fact]
    public void Get_returns_the_stored_value_with_its_original_type()
    {
        var c = new ContextKeyService();
        c.Set("i", 5);
        c.Set("s", "x");
        c.Set("b", true);
        Assert.Equal(5, c.Get("i"));
        Assert.Equal("x", c.Get("s"));
        Assert.Equal(true, c.Get("b"));
        Assert.Null(c.Get("nope"));
    }

    [Fact]
    public void Enum_values_are_stored_as_their_names()
    {
        var c = new ContextKeyService();
        c.Set("state", DayOfWeek.Friday);
        Assert.Equal("Friday", c.Get("state"));
        Assert.True(c.Evaluate("state == Friday"));
    }

    [Fact]
    public void Changed_fires_only_when_a_value_really_changes_and_lists_the_key()
    {
        var c = new ContextKeyService();
        var seen = new List<string[]>();
        using var sub = c.Changed.Subscribe(k => seen.Add(k.ToArray()));
        c.Set("k", 1);
        c.Set("k", 1);          // same value: no event
        c.Set("k", 2);
        c.Set("k", null);       // unset
        c.Set("k", null);       // already unset: no event
        Assert.Equal([["k"], ["k"], ["k"]], seen);
    }

    [Fact]
    public void SetMany_raises_a_single_event_with_exactly_the_changed_keys()
    {
        var c = new ContextKeyService();
        c.Set("same", 1);
        var seen = new List<string[]>();
        using var sub = c.Changed.Subscribe(k => seen.Add(k.ToArray()));
        c.SetMany([new("same", 1), new("p", true), new("q", "x")]);
        Assert.Single(seen);
        Assert.Equal(["p", "q"], seen[0].Order().ToArray());
    }

    [Fact]
    public void A_child_scope_overlays_without_mutating_the_parent()
    {
        var root = new ContextKeyService();
        root.Set("shared", "root");
        root.Set("onlyRoot", true);
        var child = (ContextKeyService)root.CreateScope();

        child.Set("shared", "child");
        child.Set("onlyChild", true);

        Assert.Equal("child", child.Get("shared"));
        Assert.Equal("root", root.Get("shared"));
        Assert.True(child.Evaluate("onlyRoot && onlyChild && shared == child"));
        Assert.False(root.Evaluate("onlyChild"));
        Assert.True(root.Evaluate("shared == root"));
    }

    [Fact]
    public void A_child_can_shadow_a_parent_value_with_null_and_reset_restores_it()
    {
        var root = new ContextKeyService();
        root.Set("k", true);
        var child = (ContextKeyService)root.CreateScope();
        child.Set("k", null);
        Assert.False(child.Evaluate("k"));
        Assert.True(root.Evaluate("k"));
        child.Reset("k");
        Assert.True(child.Evaluate("k"));
    }

    [Fact]
    public void Parent_changes_reach_children_unless_the_child_shadows_the_key()
    {
        var root = new ContextKeyService();
        var child = (ContextKeyService)root.CreateScope();
        child.Set("shadowed", 1);
        var seen = new List<string>();
        using var sub = child.Changed.Subscribe(k => seen.AddRange(k));

        root.Set("inherited", true);
        root.Set("shadowed", 99);

        Assert.Equal(["inherited"], seen);
        Assert.True(child.Evaluate("inherited"));
        Assert.Equal(1, child.Get("shadowed"));
    }

    [Fact]
    public void Child_changes_do_not_notify_the_parent_or_siblings()
    {
        var root = new ContextKeyService();
        var a = (ContextKeyService)root.CreateScope();
        var b = (ContextKeyService)root.CreateScope();
        var rootSeen = 0;
        var siblingSeen = 0;
        using var s1 = root.Changed.Subscribe(_ => rootSeen++);
        using var s2 = b.Changed.Subscribe(_ => siblingSeen++);
        a.Set("k", 1);
        Assert.Equal(0, rootSeen);
        Assert.Equal(0, siblingSeen);
        Assert.Null(b.Get("k"));
    }

    [Fact]
    public void Nested_scopes_chain_lookups_and_events()
    {
        var root = new ContextKeyService();
        var mid = (ContextKeyService)root.CreateScope();
        var leaf = (ContextKeyService)mid.CreateScope();
        root.Set("r", 1);
        mid.Set("m", 2);
        Assert.True(leaf.Evaluate("r == 1 && m == 2"));
        var seen = new List<string>();
        using var sub = leaf.Changed.Subscribe(k => seen.AddRange(k));
        root.Set("r", 5);
        mid.Set("m", 6);
        Assert.Equal(["r", "m"], seen);
    }

    [Fact]
    public void A_disposed_scope_stops_receiving_events_and_is_detached()
    {
        var root = new ContextKeyService();
        var child = (ContextKeyService)root.CreateScope();
        var seen = 0;
        using var sub = child.Changed.Subscribe(_ => seen++);
        child.Dispose();
        root.Set("k", 1);
        Assert.Equal(0, seen);
    }

    [Fact]
    public void A_dropped_scope_is_collectable_even_without_dispose()
    {
        var root = new ContextKeyService();
        LeakAssert.NotRetained(() =>
        {
            var scope = (ContextKeyService)root.CreateScope();
            scope.Set("k", new object());
            var handlerTarget = new object();
            scope.Changed.Subscribe(_ => GC.KeepAlive(handlerTarget));
            root.Set("poke", 1); // parent -> child notification path exercised while alive
            return [scope, handlerTarget];
        });
        GC.KeepAlive(root);
    }

    [Fact]
    public void Disposed_scopes_and_subscriptions_are_not_retained_by_the_root()
    {
        var root = new ContextKeyService();
        LeakAssert.NotRetained(() =>
        {
            var scope = (ContextKeyService)root.CreateScope();
            var subscriber = new List<string>();
            var sub = scope.Changed.Subscribe(subscriber.AddRange);
            sub.Dispose();
            scope.Dispose();
            return [scope, subscriber];
        });
        GC.KeepAlive(root);
    }

    [Fact]
    public void Disposing_the_root_unsubscribes_from_the_configuration_service()
    {
        var cfg = new TestConfigurationService();
        LeakAssert.NotRetained(() =>
        {
            var c = new ContextKeyService(cfg);
            c.Dispose();
            return [c];
        });
        GC.KeepAlive(cfg);
    }

    // ---- performance: no allocation on the hot path ------------------------------------------------------------------------------

    [Fact]
    public void Evaluate_allocates_nothing_after_warm_up_over_one_million_calls()
    {
        var cfg = new TestConfigurationService();
        cfg.Set("editor.minimap.enabled", true);
        cfg.Set("editor.tabSize", 4);
        var c = Ctx(cfg);
        c.Set("editorTextFocus", true);
        c.Set("editorReadonly", false);
        c.Set("resourceExtname", ".cs");
        c.Set("debugState", "stopped");
        string[] clauses =
        [
            "editorTextFocus && !editorReadonly",
            "resourceExtname == .cs",
            "debugState == 'stopped' || inDebugMode",
            "n > 3 && n <= 10",
            "x in list",
            "x in set && 'q' not in ids",
            "n in nums",
            "x in jarr",
            "config.editor.minimap.enabled && config.editor.tabSize == 4",
            "lang =~ /^c(sharp|pp)$/",
            "!(a && b) && (c || zero)",
            "ver === '10' || ver == 10",
            "a && b && c || a",
            "unknown",
        ];

        foreach (var cl in clauses) for (var i = 0; i < 20_000; i++) c.Evaluate(cl); // warm-up: parse, tier-up JIT

        var before = GC.GetAllocatedBytesForCurrentThread();
        var hits = 0;
        for (var i = 0; i < 1_000_000; i++)
            if (c.Evaluate(clauses[i % clauses.Length])) hits++;
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(hits > 0);
        Assert.True(allocated <= 4096, $"Evaluate allocated {allocated:N0} bytes over 1,000,000 calls");
    }

    [Fact]
    public void Get_and_Set_of_an_unchanged_value_allocate_nothing_after_warm_up()
    {
        var c = Ctx();
        for (var i = 0; i < 10_000; i++) c.Get("n");
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1_000_000; i++) c.Get("lang");
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated <= 4096, $"Get allocated {allocated:N0} bytes");
    }

    [Fact]
    public async Task Concurrent_set_and_evaluate_is_safe()
    {
        var c = new ContextKeyService();
        var errors = new System.Collections.Concurrent.ConcurrentBag<Exception>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var tasks = new List<Task>();
        for (var w = 0; w < 4; w++)
        {
            var id = w;
            tasks.Add(Task.Run(() =>
            {
                try { var i = 0; while (!cts.IsCancellationRequested) { c.Set("k" + id, i++ % 2 == 0); c.Evaluate("k0 || k1 || k2 && !k3"); var scope = c.CreateScope(); scope.Set("x", i); scope.Evaluate("x > 0"); } }
                catch (Exception ex) { errors.Add(ex); }
            }, TestContext.Current.CancellationToken));
        }
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Empty(errors);
    }
}
