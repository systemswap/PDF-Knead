using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace PdfKnead.Tests.Ui;

/// <summary>
/// Helps to track down memory leaks in tests: through which static fields is an object still reachable? Breadth-first search over all
/// fields (including private ones), arrays and their elements, without a tool such as dotnet-gcdump. Does not find roots on stacks or in
/// GC handles; weak references and ConditionalWeakTable are not followed.
/// </summary>
internal static class RootFinder
{
    /// <summary>Searches for a reference path from a static field to <paramref name="target"/>.</summary>
    /// <param name="target">The object that should no longer be reachable.</param>
    /// <param name="maxObjects">Maximum number of objects to visit before giving up.</param>
    /// <returns>The path as "field → type | …", or null if no path was found within the limit.</returns>
    public static string? PathTo(object target, int maxObjects = 3_000_000)
    {
        var parent = new Dictionary<object, (object? From, string Via)>(ReferenceEqualityComparer.Instance);
        var queue = new Queue<object>();
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.IsDynamic) continue;
            Type?[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException e)
            {
                types = e.Types;
            }
            foreach (var type in types)
            {
                if (type is null || type.ContainsGenericParameters) continue;
                FieldInfo[] fields;
                try
                {
                    fields = type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                }
                catch
                {
                    continue;
                }
                foreach (var field in fields)
                {
                    if (field.FieldType.IsPrimitive || field.IsLiteral) continue;
                    object? value;
                    try
                    {
                        value = field.GetValue(null);
                    }
                    catch
                    {
                        continue;
                    }
                    if (value is null || value is string || value is Pointer || parent.ContainsKey(value)) continue;
                    parent[value] = (null, $"{type.FullName}.{field.Name}");
                    queue.Enqueue(value);
                }
            }
        }
        while (queue.Count > 0 && parent.Count < maxObjects)
        {
            var current = queue.Dequeue();
            if (ReferenceEquals(current, target)) return Describe(target, parent);
            foreach (var (child, via) in Children(current))
            {
                if (child is null || child is string || child is Pointer || child is Type || child is MemberInfo || child is Assembly) continue;
                if (child.GetType().IsPrimitive || child.GetType().IsEnum || parent.ContainsKey(child)) continue;
                parent[child] = (current, via);
                if (ReferenceEquals(child, target)) return Describe(target, parent);
                queue.Enqueue(child);
            }
        }
        return null;
    }

    private static IEnumerable<(object?, string)> Children(object obj)
    {
        if (obj is Array array)
        {
            if (array.GetType().GetElementType() is { IsPrimitive: true }) yield break;
            var i = 0;
            foreach (var item in array) yield return (item, $"[{i++}]");
            yield break;
        }
        if (obj is WeakReference or IStrongBox { Value: null }) yield break;
        for (var type = obj.GetType(); type is not null && type != typeof(object); type = type.BaseType)
        {
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(WeakReference<>)) yield break;
            if (type.FullName is "System.Runtime.CompilerServices.ConditionalWeakTable`2" or "System.Runtime.CompilerServices.ConditionalWeakTable`2+Container") yield break;
            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (field.FieldType.IsPrimitive || field.FieldType.IsPointer || field.FieldType.IsByRefLike) continue;
                object? value;
                try
                {
                    value = field.GetValue(obj);
                }
                catch
                {
                    continue;
                }
                if (value is not null) yield return (value, $"{type.Name}.{field.Name}");
            }
        }
    }

    private static string Describe(object target, Dictionary<object, (object? From, string Via)> parent)
    {
        var steps = new List<string>();
        for (object? at = target; at is not null && parent.TryGetValue(at, out var link); at = link.From)
            steps.Add($"{link.Via} → {at.GetType().Name}");
        steps.Reverse();
        return string.Join(" | ", steps);
    }
}
