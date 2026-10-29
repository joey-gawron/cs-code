using System.Text;

namespace CsCode.App.Startup;

/// <summary>
/// Structured crash handling: every unhandled exception is written to a log file and surfaced to the user; the process never exits silently.
/// UI-thread exceptions are reported and the app keeps running; fatal ones (unhandled on another thread / in startup) write the log, tell the user where
/// it is (a friendly dialog when the UI is alive, stderr otherwise) and then let the runtime terminate. Old crash logs are pruned.
/// </summary>
public sealed class CrashHandler
{
    public const int MaxLogsKept = 20;
    private readonly string _logDirectory;
    private readonly Func<DateTimeOffset> _now;
    private readonly object _gate = new();
    private int _reported;

    /// <summary>Raised after a report was written (the shell shows a notification / dialog). Argument: (summary, log path, fatal).</summary>
    public event Action<string, string, bool>? Reported;

    public CrashHandler(string logDirectory, Func<DateTimeOffset>? now = null)
    {
        _logDirectory = logDirectory;
        _now = now ?? (() => DateTimeOffset.Now);
    }

    public string LogDirectory => _logDirectory;
    public int ReportCount => Volatile.Read(ref _reported);

    /// <summary>Default log directory honouring <c>CSCODE_HOME</c> (portable / test installs).</summary>
    public static string DefaultLogDirectory()
    {
        var home = Environment.GetEnvironmentVariable("CSCODE_HOME");
        var root = string.IsNullOrEmpty(home) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cscode") : home;
        return Path.Combine(root, "logs");
    }

    /// <summary>Hooks the process-wide unhandled-exception events. The UI-thread hook is installed by the app because it needs the dispatcher.</summary>
    public void Install()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Report(e.ExceptionObject as Exception ?? new InvalidOperationException(e.ExceptionObject?.ToString()), fatal: e.IsTerminating, source: "AppDomain");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Report(e.Exception, fatal: false, source: "UnobservedTask");
            e.SetObserved();
        };
    }

    /// <summary>Writes the report; returns the log path (null if even writing the log failed, in which case stderr still got it).</summary>
    public string? Report(Exception exception, bool fatal, string source = "UI")
    {
        Interlocked.Increment(ref _reported);
        var text = Format(exception, fatal, source, _now());
        string? path = null;
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(_logDirectory);
                path = Path.Combine(_logDirectory, $"crash-{_now():yyyyMMdd-HHmmss-fff}-{Environment.CurrentManagedThreadId}.log");
#pragma warning disable RS0030 // sync write is deliberate: the crash path may run on a dying thread where nothing async can complete
                File.WriteAllText(path, text, Encoding.UTF8);
#pragma warning restore RS0030
                Prune();
            }
            catch (Exception io) when (io is IOException or UnauthorizedAccessException)
            {
                path = null;
            }
        }
        if (path is null) Console.Error.WriteLine(text); // the log directory is unusable: still never lose the report
        try { Reported?.Invoke(Summarize(exception), path ?? "(not written)", fatal); }
        catch (Exception) { /* a failing notifier must not mask the original crash */ }
        return path;
    }

    private void Prune()
    {
        var logs = new DirectoryInfo(_logDirectory).GetFiles("crash-*.log").OrderByDescending(f => f.Name, StringComparer.Ordinal).Skip(MaxLogsKept);
        foreach (var f in logs)
        {
            try { f.Delete(); } catch (IOException) { }
        }
    }

    public static string Summarize(Exception e) => $"{e.GetType().Name}: {e.Message}";

    public static string Format(Exception exception, bool fatal, string source, DateTimeOffset when)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"CsCode crash report ({(fatal ? "FATAL" : "recovered")}, source: {source})");
        sb.AppendLine($"Time: {when:O}");
        sb.AppendLine($"Version: {typeof(CrashHandler).Assembly.GetName().Version}");
        sb.AppendLine($"Runtime: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription} on {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
        sb.AppendLine($"Thread: {Environment.CurrentManagedThreadId} (pool: {Thread.CurrentThread.IsThreadPoolThread})");
        sb.AppendLine();
        var depth = 0;
        for (Exception? e = exception; e is not null && depth < 10; e = e.InnerException, depth++)
        {
            sb.AppendLine(depth == 0 ? e.ToString() : $"--- inner exception {depth} ---\n{e}");
            if (e is AggregateException agg)
                foreach (var inner in agg.InnerExceptions.Take(5)) sb.AppendLine($"--- aggregate member ---\n{inner}");
        }
        return sb.ToString();
    }
}
