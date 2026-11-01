using CsCode.Testing.Providers.DotNet;
using CsCode.Testing.Providers.JavaScript;
using CsCode.Testing.Providers.Rust;
using Xunit;

namespace CsCode.Panels.Testing.Tests.Providers;

public sealed class CSharpTestScannerTests
{
    [Fact]
    public void Finds_xunit_nunit_and_mstest_methods_with_namespace_class_and_line()
    {
        var text = """
            using Xunit;
            namespace Sample.Ns;

            public class CalcTests
            {
                [Fact]
                public void Adds() => Assert.Equal(2, 1 + 1);

                [Theory, InlineData(1)]
                public async Task Th(int x) { await Task.Yield(); }

                [Test] public void NUnitOne() { }
                [TestMethod]
                public void MsTestOne() { }
                [AvaloniaFact]
                public void Headless() { }
                public void NotATest() { }
            }
            """;
        var found = CSharpTestScanner.Scan(text);
        Assert.Equal(["Sample.Ns.CalcTests.Adds", "Sample.Ns.CalcTests.Th", "Sample.Ns.CalcTests.NUnitOne", "Sample.Ns.CalcTests.MsTestOne", "Sample.Ns.CalcTests.Headless"],
            found.Select(f => f.FullName));
        Assert.Equal(6, found[0].Line);
        Assert.Equal(3, found[0].ClassLine);
        Assert.All(found, f => Assert.Equal("Sample.Ns", f.Namespace));
    }

    [Fact]
    public void Block_namespaces_nest_and_nested_classes_join_with_plus()
    {
        var text = """
            namespace A
            {
                namespace B.C
                {
                    public class Outer
                    {
                        public class Inner
                        {
                            [Fact] public void T() { }
                        }
                        [Fact] public void U() { }
                    }
                }
            }
            """;
        var found = CSharpTestScanner.Scan(text);
        Assert.Equal(["A.B.C.Outer+Inner.T", "A.B.C.Outer.U"], found.Select(f => f.FullName));
    }

    [Fact]
    public void Comments_strings_and_raw_strings_hide_what_looks_like_tests()
    {
        var text = """"
            namespace N;
            public class K
            {
                // [Fact] public void Commented() { }
                /* [Fact]
                   public void Block() { } */
                private const string S = "[Fact] public void InString() { }";
                private const string R = """
                    [Fact] public void InRaw() { }
                    """;
                [Fact] public void Real() { var s = "}"; var c = '}'; }
                [Fact] public void Second() { }
            }
            """";
        Assert.Equal(["N.K.Real", "N.K.Second"], CSharpTestScanner.Scan(text).Select(f => f.FullName));
    }

    [Fact]
    public void Skip_reasons_generic_return_types_and_attribute_targets()
    {
        var text = """
            namespace N;
            public class K
            {
                [Fact(Skip = "not yet")] public void Skipped() { }
                [Fact] public Task<int> Generic() => Task.FromResult(1);
                [Fact] public void Generic2<T>() { }
                [method: Fact] public void Targeted() { }
                [Test, Ignore("flaky")] public void Ignored() { }
            }
            """;
        var found = CSharpTestScanner.Scan(text);
        Assert.Equal(["N.K.Skipped", "N.K.Generic", "N.K.Generic2", "N.K.Targeted", "N.K.Ignored"], found.Select(f => f.FullName));
        Assert.Equal("not yet", found[0].SkipReason);
        Assert.Null(found[1].SkipReason);
        Assert.NotNull(found[4].SkipReason);
    }

    [Fact]
    public void Lambdas_and_local_functions_inside_tests_are_not_tests()
    {
        var text = """
            namespace N;
            public class K
            {
                [Fact]
                public void Outer()
                {
                    void Local() { }
                    Action a = () => { };
                }
                [Fact] public void Next() { }
            }
            """;
        Assert.Equal(["N.K.Outer", "N.K.Next"], CSharpTestScanner.Scan(text).Select(f => f.FullName));
    }
}

public sealed class RustTestScannerTests
{
    [Fact]
    public void Finds_tests_in_inline_modules_with_paths_lines_and_ignore()
    {
        var text = """
            pub fn add(a: i32, b: i32) -> i32 { a + b }
            mod parser;

            #[cfg(test)]
            mod tests {
                use super::*;
                #[test]
                fn adds() { assert_eq!(add(1, 2), 3); }
                #[test]
                #[ignore]
                fn ignored() {}
                // #[test] fn commented() {}
                mod nested {
                    #[tokio::test]
                    async fn deep() { let s = "}"; let c = '}'; let l: &'static str = "x"; }
                }
                fn helper() {}
            }
            """;
        var r = RustTestScanner.Scan(text);
        Assert.Equal(["tests::adds", "tests::ignored", "tests::nested::deep"], r.Tests.Select(t => t.Path));
        Assert.Equal(7, r.Tests[0].Line);
        Assert.True(r.Tests[1].Ignored);
        Assert.Contains(r.Modules, m => m.Path == "parser" && !m.Inline);
        Assert.Contains(r.Modules, m => m.Path == "tests::nested" && m.Inline);
    }

    [Fact]
    public async Task Locator_follows_inline_and_out_of_line_modules()
    {
        using var dir = new CsCode.TestDoubles.TempDirectory();
        var lib = dir.WriteFile("src/lib.rs", "mod a;\n#[cfg(test)]\nmod tests {\n    #[test]\n    fn root_test() {}\n}\n");
        dir.WriteFile("src/a.rs", "mod b;\n#[test]\nfn in_a() {}\n");
        dir.WriteFile("src/a/b/mod.rs", "\n\n#[cfg(test)]\nmod t {\n  #[test]\n  fn in_b() {}\n}\n");
        var locator = new RustTestLocator(p => Task.FromResult(File.Exists(p) ? File.ReadAllText(p) : null));
        var root = await locator.LocateAsync(lib, "tests::root_test", TestContext.Current.CancellationToken);
        Assert.Equal((lib, 4), (root!.FilePath, root.Line));
        var a = await locator.LocateAsync(lib, "a::in_a", TestContext.Current.CancellationToken);
        Assert.Equal((dir.Combine("src", "a.rs"), 2), (a!.FilePath, a.Line));
        var b = await locator.LocateAsync(lib, "a::b::t::in_b", TestContext.Current.CancellationToken);
        Assert.Equal((dir.Combine("src", "a", "b", "mod.rs"), 5), (b!.FilePath, b.Line));
        Assert.Null(await locator.LocateAsync(lib, "missing::x", TestContext.Current.CancellationToken));
    }
}

public sealed class JsTestScannerTests
{
    [Fact]
    public void Finds_tests_nested_in_describe_blocks()
    {
        var text = """
            import { add } from './math';
            // it('commented', () => {});
            describe('math', () => {
              describe("add", () => {
                it('adds two numbers', () => { expect(add(1, 2)).toBe(3); });
              });
              test.skip(`subtracts`, () => {});
              it.each([1, 2])('each %i', (n) => {});
            });
            test('top level', async () => { const s = "describe('x', () => {"; });
            xit('disabled', () => {});
            """;
        var found = JsTestScanner.Scan(text);
        Assert.Equal(["math > add > adds two numbers", "math > subtracts", "top level", "disabled"], found.Select(f => f.Name));
        Assert.Equal(4, found[0].Line);
        Assert.True(found[1].Skipped);
        Assert.False(found[0].Skipped);
        Assert.True(found[3].Skipped);
    }

    [Fact]
    public void Test_files_are_told_by_name_or_tests_directory()
    {
        Assert.True(JavaScriptTestProvider.IsTestFile("/p/src/math.test.ts"));
        Assert.True(JavaScriptTestProvider.IsTestFile("/p/src/math.spec.jsx"));
        Assert.True(JavaScriptTestProvider.IsTestFile("/p/src/__tests__/math.js"));
        Assert.False(JavaScriptTestProvider.IsTestFile("/p/src/math.ts"));
        Assert.False(JavaScriptTestProvider.IsTestFile("/p/src/math.test.json"));
    }
}
