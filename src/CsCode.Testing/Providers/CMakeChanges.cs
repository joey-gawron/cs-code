using CsCode.Core.Build;
using CsCode.Core.Lifecycle;

namespace CsCode.Testing.Providers;

/// <summary>
/// The CMake integration's changes as a test provider's <see cref="Engine.ITestProvider.ProjectsChanged"/> (the C++ and CTest providers): a
/// configure into new targets (<see cref="CMakeProject.Generation"/>) or a project that came or went, and every finished build (a build makes a
/// target's executable and can change what ctest lists).
/// </summary>
internal sealed class CMakeChanges(ICMakeService cmake) : IEventSource<bool>
{
    public IDisposable Subscribe(Action<bool> handler)
    {
        var gate = new object();
        var last = Generations(cmake.Projects);
        var configured = cmake.ProjectsChanged.Subscribe(projects =>
        {
            var now = Generations(projects);
            lock (gate)
            {
                if (now == last) return;
                last = now;
            }
            handler(true);
        });
        var built = cmake.BuildFinished.Subscribe(e => { if (e.Result.ExitCode is not null) handler(true); });
        return Disposable.Combine(configured, built);
    }

    private static string Generations(IReadOnlyList<CMakeProject> projects) => string.Join('\n', projects.Select(p => p.SourceDirectory + "\0" + p.Generation));
}
