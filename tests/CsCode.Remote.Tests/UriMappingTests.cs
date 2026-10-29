using CsCode.Platform.Io;
using CsCode.Remote.Protocol;
using Xunit;

namespace CsCode.Remote.Tests;

public class UriMappingTests
{
    public static TheoryData<string> TrickyPaths =>
    [
        "/home/me/project/main.ts",
        "/home/me/my project/a b.txt",
        "/tmp/hash#name/100% done?.cs",
        "/srv/[weird]/(parens)/x;y=z,w&q.txt",
        "/data/unicode-ünïcødé/文件.md",
        "/a/b/c/",
        "/",
        "/home/me/it's \"quoted\"/file",
        "/home/me/plus+tilde~dollar$/at@sign",
    ];

    [Theory]
    [MemberData(nameof(TrickyPaths))]
    public void Remote_paths_round_trip_through_ssh_and_server_uris(string path)
    {
        var mapper = new SshUriMapper(new RemoteAuthority("box.example.com", "me", 2222));
        var client = mapper.FromRemotePath(path);
        Assert.Equal("ssh", client.Scheme);
        Assert.True(mapper.Handles(client));
        Assert.Equal(path, mapper.ToRemotePath(client));
        var server = mapper.ToServer(client);
        Assert.True(server.IsFile);
        Assert.Equal(path, Uri.UnescapeDataString(server.AbsolutePath));
        Assert.Equal(client, mapper.ToClient(server));
    }

    [Theory]
    [MemberData(nameof(TrickyPaths))]
    public void Server_uris_are_exactly_what_Platform_builds_for_the_same_path(string path)
    {
        // The server's document manager and language host key documents by the URI Platform's FileUri builds; the mapper must produce the same one.
        var mapper = new SshUriMapper(new RemoteAuthority("box"));
        var fromMapper = mapper.ToServer(mapper.FromRemotePath(path));
        var fromPlatform = FileUri.FromPath(path, windows: false);
        Assert.Equal(fromPlatform, fromMapper);
        Assert.Equal(fromPlatform.AbsoluteUri, fromMapper.AbsoluteUri);
    }

    [Theory]
    [InlineData("a b.txt")]
    [InlineData("x#y%z?.cs")]
    [InlineData("(1) [2] {3}.md")]
    [InlineData("ümlaut.txt")]
    public void A_uri_coming_back_from_the_server_equals_the_one_the_Explorer_built_for_the_same_file(string name)
    {
        // The Explorer builds children as parent.AbsoluteUri + Uri.EscapeDataString(name); diagnostics/definitions map back through the mapper.
        var mapper = new SshUriMapper(new RemoteAuthority("box", "me"));
        var folder = mapper.FromRemotePath("/home/me/src/");
        var explorer = new Uri(folder.AbsoluteUri + Uri.EscapeDataString(name));
        var fromServer = mapper.ToClient(FileUri.FromPath("/home/me/src/" + name, windows: false));
        Assert.Equal(explorer, fromServer);
        Assert.Equal(explorer.GetHashCode(), fromServer.GetHashCode());
    }

    [Theory]
    [InlineData("box", null, "box", null)]
    [InlineData("me@box", "me", "box", null)]
    [InlineData("me@box:2222", "me", "box", 2222)]
    [InlineData("box.example.com:22", null, "box.example.com", 22)]
    [InlineData("[::1]:2222", null, "::1", 2222)]
    [InlineData("me@[fe80::1]", "me", "fe80::1", null)]
    [InlineData("::1", null, "::1", null)]
    [InlineData("first.last@corp.example.com", "first.last", "corp.example.com", null)]
    [InlineData("ssh://me@box:2200/home/me", "me", "box", 2200)]
    public void Destinations_parse_into_user_host_and_port(string text, string? user, string host, int? port)
    {
        var a = RemoteAuthority.Parse(text);
        Assert.Equal(user, a.User);
        Assert.Equal(host, a.Host);
        Assert.Equal(port, a.Port);
        // and the authority round-trips through its own root URI
        var back = RemoteAuthority.Parse(a.Root.AbsoluteUri);
        Assert.Equal(a, back);
    }

    [Fact]
    public void Ipv6_hosts_and_ports_produce_valid_uris_that_only_match_their_own_authority()
    {
        var v6 = new SshUriMapper(new RemoteAuthority("::1", "me", 2222));
        var uri = v6.FromRemotePath("/x/y.txt");
        Assert.Equal("ssh://me@[::1]:2222/x/y.txt", uri.AbsoluteUri);
        Assert.True(v6.Handles(uri));
        var otherPort = new SshUriMapper(new RemoteAuthority("::1", "me", 22));
        Assert.False(otherPort.Handles(uri));
        var otherUser = new SshUriMapper(new RemoteAuthority("::1", "you", 2222));
        Assert.False(otherUser.Handles(uri));
        Assert.False(v6.Handles(new Uri("file:///x/y.txt")));
        Assert.Throws<NotSupportedException>(() => otherPort.ToRemotePath(uri));
    }

    [Fact]
    public void Windows_remotes_map_drive_paths()
    {
        var win = new SshUriMapper(new RemoteAuthority("winbox"), windowsRemote: true);
        var uri = win.FromRemotePath(@"C:\Users\me\My Code\a.cs");
        Assert.Equal("ssh://winbox/C:/Users/me/My%20Code/a.cs", uri.AbsoluteUri);
        Assert.Equal("C:/Users/me/My Code/a.cs", win.ToRemotePath(uri));
        var server = win.ToServer(uri);
        Assert.Equal(FileUri.FromPath(@"C:\Users\me\My Code\a.cs", windows: true), server);
        Assert.Equal(uri, win.ToClient(server));
    }

    [Theory]
    [InlineData("")]
    [InlineData("me@")]
    [InlineData("box:notaport")]
    [InlineData("[::1")]
    public void Malformed_destinations_are_rejected(string text) =>
        Assert.ThrowsAny<Exception>(() => RemoteAuthority.Parse(text));

    [Fact]
    public void The_identity_mapper_serves_local_files_and_untitled_buffers_only()
    {
        var id = UriMapper.Identity;
        Assert.True(id.Handles(new Uri("file:///a/b.cs")));
        Assert.True(id.Handles(new Uri("untitled:Untitled-1")));
        Assert.False(id.Handles(new Uri("ssh://box/a")));
        Assert.Equal("/a b/c.cs", id.ToServerPath(FileUri.FromPath("/a b/c.cs", false)));
        Assert.Null(id.ToServerPath(new Uri("untitled:Untitled-1")));
    }
}
