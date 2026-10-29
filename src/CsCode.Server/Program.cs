using CsCode.Remote.Askpass;
using CsCode.Remote.Protocol;

namespace CsCode.Server;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        ServerOptions options;
        try { options = ServerOptions.Parse(args); }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine("cscode-server: " + ex.Message);
            return 2;
        }
        if (options.ShowVersion) { Console.WriteLine($"cscode-server {ServerHost.Version} (protocol v{RemoteProtocol.Version})"); return 0; }
        if (options.Askpass) return AskpassClient.Run(options.AskpassPrompt ?? "", Console.Out, Console.Error);

        // stdout carries the protocol and nothing else: anything that writes to Console.Out (a library, a stray debug line) goes to stderr instead.
        var stdout = Console.OpenStandardOutput();
        var stdin = Console.OpenStandardInput();
        Console.SetOut(Console.Error);

        ServerHost host;
        try
        {
            host = new ServerHost(options, stdin, stdout);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("cscode-server: start-up failed: " + ex);
            return 1;
        }
        // The greeting tells the client where the framed stream starts (a login shell may have printed a banner before us).
        var greeting = System.Text.Encoding.ASCII.GetBytes("\n" + RemoteProtocol.Greeting + "\n");
        stdout.Write(greeting);
        stdout.Flush();

        await using (host)
            await host.RunAsync().ConfigureAwait(false);
        return 0;
    }
}
