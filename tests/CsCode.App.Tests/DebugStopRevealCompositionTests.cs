using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using CsCode.Core.Debugging;
using CsCode.Core.Text;
using CsCode.Editor;
using CsCode.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CsCode.App.Tests;

/// <summary>
/// Debugger and editor over the REAL composition root (every real module, CSCODE_HOME in a temp dir), driven by a minimal DAP adapter on a
/// socket: the editor re-centers on the stop line whenever the debug service announces a stop location, so the service must announce one only
/// when the stop actually moved. An adapter <c>invalidated</c> event for variables (a display-format change, a setVariable) leaves the stop
/// where it is and must not scroll a view the user moved away from the stop line. Each module's own tests use a fake of the other side, so
/// only this test proves the two agree.
/// </summary>
public class DebugStopRevealCompositionTests
{
    private const int StopLine = 300; // 1-based, as the adapter reports it

    [AvaloniaFact(Timeout = 60_000)]
    public async Task An_adapter_invalidating_variables_does_not_scroll_the_stop_file_back_to_the_stop_line()
    {
        using var home = new TempDirectory("cscode-stop-reveal-");
        var previousHome = Environment.GetEnvironmentVariable("CSCODE_HOME");
        Environment.SetEnvironmentVariable("CSCODE_HOME", home.Path); // never the user's real config/state directories
        try
        {
            // Plain text: the editor's debug surface does not depend on the language, and no language server gets started.
            var path = home.WriteFile("work/stop.txt", string.Join('\n', Enumerable.Range(0, 600).Select(i => "line" + i)) + "\n");
            await using var adapter = new MiniDebugAdapter(path, StopLine);
            await using var sp = AppServices.Build([]);
            var factory = sp.GetRequiredService<IEditorViewFactory>();
            var debug = sp.GetRequiredService<IDebugService>();
            var doc = await factory.OpenDocumentAsync(new Uri(path));
            var view = factory.CreateView(doc);
            var controller = factory.GetController(view)!;
            var window = new Window { Width = 600, Height = 400, Content = view };
            using var closeWindow = CsCode.Core.Lifecycle.Disposable.Create(() => { window.Close(); factory.DisposeView(view); });
            window.Show();
            Dispatcher.UIThread.RunJobs();
            double Scroll() => controller.GetViewState().VerticalOffset;

            using var registration = debug.RegisterAdapterFactory("minifake", new ServerFactory(adapter.Port));
            var session = await debug.StartAsync(DebugConfiguration.FromJson(JsonSerializer.SerializeToElement(new { type = "minifake", request = "launch", name = "Mini" })));
            Assert.NotNull(session);

            // The stop is revealed in the view.
            await Until(() => debug.StopLocation is { Line: StopLine - 1 } && Scroll() > 0, "the stop line revealed");

            // The user scrolls back to the top to read other code while stopped.
            controller.Reveal(TextRange.At(0, 0), center: false);
            await Until(() => Scroll() == 0, "the view scrolled to the top");

            var invalidated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var sub = session.Invalidated.Subscribe(_ => invalidated.TrySetResult());
            await adapter.SendEventAsync("invalidated", new JsonObject { ["areas"] = new JsonArray("variables") });
            await invalidated.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Pump(TimeSpan.FromMilliseconds(500)); // the service reacts on its lane, the editor on its next UI turn

            Assert.Equal(0d, Scroll());
            Assert.Equal(StopLine - 1, debug.StopLocation?.Line);

            // Control: a real new stop (resume, stop again) does reveal the stop line again, so the check above can see a reveal.
            await adapter.SendEventAsync("continued", new JsonObject { ["threadId"] = 1, ["allThreadsContinued"] = true });
            await Until(() => debug.StopLocation is null, "the stop location cleared on resume");
            await adapter.SendEventAsync("stopped", new JsonObject { ["reason"] = "step", ["threadId"] = 1, ["allThreadsStopped"] = true });
            await Until(() => debug.StopLocation is not null && Scroll() > 0, "the new stop revealed");

            await session.StopAsync();
        }
        finally
        {
            Environment.SetEnvironmentVariable("CSCODE_HOME", previousHome);
        }
    }

    private static async Task Until(Func<bool> condition, string what)
    {
        var deadline = Environment.TickCount64 + 20_000;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline) throw new TimeoutException("Timed out waiting for " + what);
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task Pump(TimeSpan duration)
    {
        var until = Environment.TickCount64 + (long)duration.TotalMilliseconds;
        while (Environment.TickCount64 < until)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
        Dispatcher.UIThread.RunJobs();
    }

    private sealed class ServerFactory(int port) : IDebugAdapterFactory
    {
        public ValueTask<DebugAdapterDescriptor?> CreateDescriptorAsync(DebugConfiguration configuration, CancellationToken ct) =>
            ValueTask.FromResult<DebugAdapterDescriptor?>(new DebugAdapterServer("127.0.0.1", port));
    }

    /// <summary>Just enough of a debug adapter on a loopback socket: one thread with one frame at <c>line</c> of <c>path</c>, stopped right
    /// after configurationDone; every other request succeeds with an empty body.</summary>
    private sealed class MiniDebugAdapter : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cts = new();
        private readonly SemaphoreSlim _write = new(1, 1);
        private readonly TaskCompletionSource<Stream> _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly string _path;
        private readonly int _line;
        private readonly Task _loop;
        private int _seq;

        public MiniDebugAdapter(string path, int line)
        {
            _path = path;
            _line = line;
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _loop = Task.Run(RunAsync);
        }

        public int Port { get; }

        public Task SendEventAsync(string name, JsonObject? body = null)
        {
            var message = new JsonObject { ["type"] = "event", ["event"] = name };
            if (body is not null) message["body"] = body;
            return SendAsync(message);
        }

        private async Task RunAsync()
        {
            try
            {
                using var socket = await _listener.AcceptSocketAsync(_cts.Token);
                await using var stream = new NetworkStream(socket, ownsSocket: false);
                _connected.TrySetResult(stream);
                while (await ReadAsync(stream) is { } request) await HandleAsync(request);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or ObjectDisposedException) { }
            finally { _connected.TrySetCanceled(); }
        }

        private async Task<JsonObject?> ReadAsync(Stream stream)
        {
            var header = new StringBuilder();
            var one = new byte[1];
            while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
            {
                if (await stream.ReadAsync(one, _cts.Token) == 0) return null;
                header.Append((char)one[0]);
            }
            const string Key = "Content-Length:";
            var lengthLine = header.ToString().Split("\r\n").First(l => l.StartsWith(Key, StringComparison.OrdinalIgnoreCase));
            var body = new byte[int.Parse(lengthLine[Key.Length..].Trim(), CultureInfo.InvariantCulture)];
            await stream.ReadExactlyAsync(body, _cts.Token);
            return JsonNode.Parse(body)!.AsObject();
        }

        private async Task HandleAsync(JsonObject request)
        {
            var command = (string?)request["command"];
            JsonObject? body = command switch
            {
                "initialize" => new JsonObject { ["supportsConfigurationDoneRequest"] = true },
                "threads" => new JsonObject { ["threads"] = new JsonArray(new JsonObject { ["id"] = 1, ["name"] = "main" }) },
                "stackTrace" => new JsonObject
                {
                    ["stackFrames"] = new JsonArray(new JsonObject
                    {
                        ["id"] = 1, ["name"] = "Main", ["line"] = _line, ["column"] = 1,
                        ["source"] = new JsonObject { ["name"] = Path.GetFileName(_path), ["path"] = _path },
                    }),
                    ["totalFrames"] = 1,
                },
                "scopes" => new JsonObject { ["scopes"] = new JsonArray(new JsonObject { ["name"] = "Locals", ["variablesReference"] = 10, ["expensive"] = false }) },
                "variables" => new JsonObject { ["variables"] = new JsonArray() },
                "setBreakpoints" => new JsonObject { ["breakpoints"] = new JsonArray() },
                _ => null,
            };
            var response = new JsonObject { ["type"] = "response", ["request_seq"] = (int?)request["seq"] ?? 0, ["success"] = true, ["command"] = command };
            if (body is not null) response["body"] = body;
            await SendAsync(response);
            switch (command)
            {
                case "initialize": await SendEventAsync("initialized"); break;
                case "configurationDone": await SendEventAsync("stopped", new JsonObject { ["reason"] = "breakpoint", ["threadId"] = 1, ["allThreadsStopped"] = true }); break;
                case "disconnect" or "terminate": await SendEventAsync("terminated"); break;
            }
        }

        private async Task SendAsync(JsonObject message)
        {
            var stream = await _connected.Task;
            await _write.WaitAsync(_cts.Token);
            try
            {
                message["seq"] = Interlocked.Increment(ref _seq);
                var bytes = Encoding.UTF8.GetBytes(message.ToJsonString());
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"Content-Length: {bytes.Length}\r\n\r\n"), _cts.Token);
                await stream.WriteAsync(bytes, _cts.Token);
                await stream.FlushAsync(_cts.Token);
            }
            finally { _write.Release(); }
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            _listener.Stop();
            try { await _loop; }
            catch (OperationCanceledException) { }
            _cts.Dispose();
            _write.Dispose();
        }
    }
}
