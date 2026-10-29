using System.Reflection;

namespace CsCode.Editor.Tests.Support;

/// <summary>
/// Leak forensics: a breadth-first walk over the strong references (instance fields, array elements, delegate targets) from some roots, looking for
/// a path to an object that should be dead. Weak references, thread objects and runtime type metadata are not followed. Used by the leak tests to
/// say WHICH field keeps a disposed view alive, instead of just "still reachable".
/// </summary>
public static class RetentionPath
{
    public static string? Find(IEnumerable<object> roots, object target, int maxNodes = 4_000_000)
    {
        var parent = new Dictionary<object, (object? Parent, string Edge)>(ReferenceEqualityComparer.Instance);
        var queue = new Queue<object>();
        foreach (var root in roots)
        {
            if (parent.TryAdd(root, (null, "<root " + root.GetType().Name + ">"))) queue.Enqueue(root);
        }
        var visited = 0;
        while (queue.Count > 0 && visited++ < maxNodes)
        {
            var current = queue.Dequeue();
            if (ReferenceEquals(current, target)) return Describe(parent, current);
            foreach (var (child, edge) in Children(current))
            {
                if (child is null || !parent.TryAdd(child, (current, edge))) continue;
                queue.Enqueue(child);
            }
        }
        return visited >= maxNodes ? "(search gave up after " + maxNodes + " objects)" : null;
    }

    private static string Describe(Dictionary<object, (object? Parent, string Edge)> parent, object end)
    {
        var parts = new List<string>();
        for (var node = (object?)end; node is not null; node = parent[node].Parent)
            parts.Add($"{parent[node].Edge} : {node.GetType().Name}");
        parts.Reverse();
        return string.Join("\n   -> ", parts);
    }

    private static IEnumerable<(object? Child, string Edge)> Children(object o)
    {
        var type = o.GetType();
        if (type.IsPrimitive || o is string || o is Pointer || o is WeakReference || o is Thread || o is Type || o is MemberInfo || o is Assembly || o is Module) yield break;
        if (type.IsGenericType && type.GetGenericTypeDefinition().Name.StartsWith("ConditionalWeakTable")) yield break;
        if (o is Array array)
        {
            if (type.GetElementType()!.IsPrimitive || type.GetElementType()!.IsEnum) yield break;
            var i = 0;
            foreach (var element in array)
            {
                if (element is not null && !element.GetType().IsPrimitive) yield return (element, $"[{i}]");
                i++;
            }
            yield break;
        }
        for (var t = type; t is not null && t != typeof(object); t = t.BaseType)
        {
            foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly))
            {
                if (f.FieldType.IsPrimitive || f.FieldType.IsEnum || f.FieldType.IsPointer || f.FieldType == typeof(string)) continue;
                object? value;
                try
                {
                    value = f.GetValue(o);
                }
                catch (Exception)
                {
                    continue;
                }
                if (value is null) continue;
                yield return (value, t.Name + "." + f.Name);
            }
        }
    }
}
