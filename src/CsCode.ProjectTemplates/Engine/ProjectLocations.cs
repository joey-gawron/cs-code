using System.Globalization;

namespace CsCode.ProjectTemplates.Engine;

/// <summary>Where the New Project page proposes a project: its default location and a free default name. File I/O: off the UI thread.</summary>
public static class ProjectLocations
{
    /// <summary>
    /// <c>cscode.newProject.defaultLocation</c> when set (<c>~</c> expanded); else the parent of the most recently opened folder that still
    /// exists; else the home folder.
    /// </summary>
    public static string DefaultLocation(string? configured, IReadOnlyList<string> recentFolders, string home)
    {
        if (configured is { Length: > 0 }) return ExpandHome(configured.Trim(), home);
        foreach (var recent in recentFolders)
        {
            var folder = Path.TrimEndingDirectorySeparator(recent);
            if (Directory.Exists(folder) && Path.GetDirectoryName(folder) is { Length: > 0 } parent) return parent;
        }
        return home;
    }

    /// <summary><paramref name="stem"/>, else stem1, stem2…: the first name that does not exist in <paramref name="location"/>.</summary>
    public static string UniqueName(string location, string stem)
    {
        for (var i = 0; i < 1000; i++)
        {
            var name = i == 0 ? stem : stem + i.ToString(CultureInfo.InvariantCulture);
            var path = Path.Combine(location, name);
            if (!Directory.Exists(path) && !File.Exists(path)) return name;
        }
        return stem;
    }

    /// <summary><c>~</c> and <c>~/…</c> as the home folder; anything else as it is.</summary>
    public static string ExpandHome(string path, string home) =>
        path == "~" ? home
        : path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal) ? Path.Combine(home, path[2..])
        : path;
}
