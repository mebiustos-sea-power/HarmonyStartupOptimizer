using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace HarmonyStartupOptimizer;

internal sealed class PatchRegistry
{
    private readonly Harmony harmony;
    private readonly HashSet<MethodBase> targets = new HashSet<MethodBase>();

    internal PatchRegistry(Harmony harmony) { this.harmony = harmony; }

    internal void Add(MethodBase target, HarmonyMethod? prefix = null, HarmonyMethod? postfix = null,
        HarmonyMethod? transpiler = null, HarmonyMethod? finalizer = null)
    {
        try
        {
            harmony.Patch(target, prefix, postfix, transpiler, finalizer, ilmanipulator: null);
            targets.Add(target);
        }
        catch (Exception original)
        {
            // Harmony can retain registration after compiling a wrapper fails.
            // Remove this owner's entire registration in one rebuild.
            try { harmony.Unpatch(target, HarmonyPatchType.All, harmony.Id); targets.Remove(target); }
            catch (Exception rollback)
            {
                targets.Add(target);
                throw new AggregateException("Optimizer patch installation and rollback failed: " + target, original, rollback);
            }
            throw;
        }
    }

    internal void RemoveAll()
    {
        var errors = new List<Exception>();
        foreach (MethodBase target in targets.ToArray())
        {
            try { harmony.Unpatch(target, HarmonyPatchType.All, harmony.Id); targets.Remove(target); }
            catch (Exception error) { errors.Add(new InvalidOperationException("Could not remove optimizer hooks from " + target, error)); }
        }
        if (errors.Count != 0) throw new AggregateException("Optimizer hook cleanup failed.", errors);
    }
}
