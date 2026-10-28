using System;
using System.Collections.Concurrent;

namespace HarmonyStartupOptimizer;

internal sealed class TypeNameCache
{
    private static readonly Type RuntimeType = typeof(object).GetType();
    private readonly ConcurrentDictionary<Type, Names> entries = new ConcurrentDictionary<Type, Names>();
    private readonly object gate = new object();
    private volatile bool active = true;

    // Runtime type names are immutable. Custom Type implementations keep their
    // original getters, which may be computed or have observable side effects.
    internal string? FullName(Type type) => active && type.GetType() == RuntimeType ? Get(type).FullName : type.FullName;
    internal string Name(Type type) => active && type.GetType() == RuntimeType ? Get(type).Name : type.Name;

    private Names Get(Type type)
    {
        if (entries.TryGetValue(type, out Names? cached)) return cached;
        var names = new Names(type.FullName, type.Name);
        lock (gate) if (active) entries.TryAdd(type, names);
        return names;
    }

    internal void Invalidate() { lock (gate) entries.Clear(); }
    internal void Deactivate() { lock (gate) { active = false; entries.Clear(); } }

    private sealed class Names
    {
        internal readonly string? FullName;
        internal readonly string Name;
        internal Names(string? fullName, string name) { FullName = fullName; Name = name; }
    }
}
