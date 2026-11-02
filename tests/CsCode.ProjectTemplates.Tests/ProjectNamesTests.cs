using CsCode.ProjectTemplates.Engine;
using Xunit;

namespace CsCode.ProjectTemplates.Tests;

public sealed class ProjectNamesTests
{
    [Theory]
    [InlineData("hello")]
    [InlineData("hello-world")]
    [InlineData("hello_world2")]
    [InlineData("_x")]
    public void Rust_accepts_cargo_names(string name) => Assert.Null(ProjectNames.Rust(name));

    [Theory]
    [InlineData("")]
    [InlineData("2fast")]
    [InlineData("has space")]
    [InlineData("dot.ted")]
    [InlineData("fn")]
    [InlineData("test")]
    [InlineData("std")]
    [InlineData("build")]
    [InlineData("con")]
    public void Rust_rejects_what_cargo_refuses(string name) => Assert.NotNull(ProjectNames.Rust(name));

    [Theory]
    [InlineData("hello", "hello")]
    [InlineData("Hello-World", "hello_world")]
    [InlineData("my.cool__pkg", "my_cool_pkg")]
    public void Python_accepts_pep508_names_and_normalises_the_package(string name, string package)
    {
        Assert.Null(ProjectNames.Python(name));
        Assert.Equal(package, ProjectNames.PythonPackage(name));
    }

    [Theory]
    [InlineData("-lead")]
    [InlineData("trail-")]
    [InlineData("1abc")]
    [InlineData("class")]
    [InlineData("tests")]
    [InlineData("bad!name")]
    public void Python_rejects_names_that_cannot_be_imported(string name) => Assert.NotNull(ProjectNames.Python(name));

    [Theory]
    [InlineData("MyApp")]
    [InlineData("Company.Product.Api")]
    [InlineData("_internal")]
    public void CSharp_accepts_dotted_identifiers(string name) => Assert.Null(ProjectNames.CSharp(name));

    [Theory]
    [InlineData("My-App")]
    [InlineData("1App")]
    [InlineData("Company..Api")]
    [InlineData("class")]
    [InlineData("App.namespace")]
    public void CSharp_rejects_non_identifiers_and_keywords(string name) => Assert.NotNull(ProjectNames.CSharp(name));

    [Theory]
    [InlineData("hello")]
    [InlineData("hello-world")]
    [InlineData("Hello_World")]
    public void CMake_accepts_target_names(string name) => Assert.Null(ProjectNames.CMake(name));

    [Theory]
    [InlineData("test")]
    [InlineData("all")]
    [InlineData("9lives")]
    [InlineData("a.b")]
    public void CMake_rejects_reserved_targets_and_bad_names(string name) => Assert.NotNull(ProjectNames.CMake(name));

    [Fact]
    public void CIdentifier_lowercases_and_replaces_dashes() => Assert.Equal("hello_world", ProjectNames.CIdentifier("Hello-World"));

    [Theory]
    [InlineData("hello", true)]
    [InlineData("hello_zig", true)]
    [InlineData("hello-zig", false)]
    [InlineData("fn", false)]
    [InlineData("a_very_long_package_name_beyond_32", false)]
    public void Zig_takes_identifiers_up_to_32_bytes(string name, bool valid) => Assert.Equal(valid, ProjectNames.Zig(name) is null);

    [Theory]
    [InlineData("ok name")]
    [InlineData("Ünïcødé")]
    public void Folder_accepts_portable_names(string name) => Assert.Null(ProjectNames.Folder(name));

    [Theory]
    [InlineData("")]
    [InlineData(" lead")]
    [InlineData("a/b")]
    [InlineData("a:b")]
    [InlineData("nul")]
    [InlineData("dot.")]
    public void Folder_rejects_names_some_platform_refuses(string name) => Assert.NotNull(ProjectNames.Folder(name));
}
