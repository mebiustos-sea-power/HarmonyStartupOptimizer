using System;
using System.Collections.Generic;
using System.Reflection;

namespace HarmonyStartupOptimizer;

// Cache only complete enumerations; each caller still owns its returned array.
internal sealed class AssemblyTypeCache
{
    private readonly object gate = new object();
    private readonly Dictionary<Assembly, Type[]> entries = new Dictionary<Assembly, Type[]>();
    private long generation;
    private bool active = true;

    internal Type[] GetTypes(Assembly assembly)
    {
        if (assembly.IsDynamic) return assembly.GetTypes();
        long started;
        lock (gate)
        {
            started = generation;
            if (active && entries.TryGetValue(assembly, out Type[]? cached)) return (Type[])cached.Clone();
        }
        Type[] result = assembly.GetTypes();
        lock (gate)
        {
            if (active && generation == started) entries[assembly] = (Type[])result.Clone();
        }
        return result;
    }

    internal void Invalidate()
    {
        lock (gate) { generation++; entries.Clear(); }
    }

    internal void Deactivate()
    {
        lock (gate) { active = false; generation++; entries.Clear(); }
    }
}
