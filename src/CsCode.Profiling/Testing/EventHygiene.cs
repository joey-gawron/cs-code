using System.Reflection;

namespace CsCode.Profiling.Testing;

/// <summary>
/// Event hygiene audit (SHOULD, the analyzer appendix of docs/MEMORY.md): a C# <c>static event</c> keeps every subscriber alive for the process's
/// lifetime unless each one remembers to unsubscribe — the classic managed leak ARCHITECTURE §8 rule 1 forbids. A banned-API analyzer cannot ban a
/// declaration, so this reflection check does it for any set of assemblies (a test fails with the list).
/// </summary>
public static class EventHygiene
{
    /// <summary>Every static event declared in the assemblies, as "Type.Event".</summary>
    public static IReadOnlyList<string> StaticEvents(IEnumerable<Assembly> assemblies)
    {
        var found = new List<string>();
        foreach (var assembly in assemblies)
        {
            Type[] types;
            try { types = assembly.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t is not null).ToArray()!; }
            foreach (var type in types)
                foreach (var e in type.GetEvents(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    found.Add($"{type.FullName}.{e.Name}");
        }
        return found;
    }
}
