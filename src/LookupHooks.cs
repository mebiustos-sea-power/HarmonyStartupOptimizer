using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace HarmonyStartupOptimizer;

internal sealed class LookupHooks
{
    [ThreadStatic] private static Frame? activeFrame;
    private static LookupHooks? installed;
    private readonly PatchRegistry patches;
    private bool subscribed;
    internal readonly TypeLookupCache Cache;
    internal static MethodInfo Target => typeof(AccessTools).GetMethod(nameof(AccessTools.TypeByName), new[] { typeof(string) })
        ?? throw new MissingMethodException(typeof(AccessTools).FullName, "TypeByName(string)");

    internal LookupHooks(string owner, TypeLookupCache cache)
    { patches = new PatchRegistry(new Harmony(owner)); Cache = cache; }

    internal void Install()
    {
        if (installed != null) throw new InvalidOperationException("A shared Harmony type cache is already installed.");
        MethodInfo target = Target;
        if (!target.IsStatic || target.IsGenericMethod || target.ReturnType != typeof(Type) || target.GetMethodBody() == null)
            throw new InvalidOperationException("Unsupported Harmony TypeByName contract: " + target);
        installed = this;
        AppDomain.CurrentDomain.AssemblyLoad += AssemblyLoaded;
        subscribed = true;
        try
        {
            patches.Add(target,
                prefix: new HarmonyMethod(typeof(LookupHooks), nameof(Prefix)) { priority = Priority.First },
                transpiler: new HarmonyMethod(typeof(LookupHooks), nameof(Transpiler)) { priority = Priority.Last },
                finalizer: new HarmonyMethod(typeof(LookupHooks), nameof(Finalizer)) { priority = Priority.Last });
        }
        catch (Exception error)
        {
            try { Uninstall(); }
            catch (Exception cleanup) { throw new AggregateException("Shared type-cache installation and rollback failed.", error, cleanup); }
            throw;
        }
    }

    internal void Uninstall()
    {
        Cache.Deactivate();
        if (subscribed) { AppDomain.CurrentDomain.AssemblyLoad -= AssemblyLoaded; subscribed = false; }
        patches.RemoveAll();
        if (installed == this) installed = null;
    }

    private void AssemblyLoaded(object? sender, AssemblyLoadEventArgs args) => Cache.Invalidate();

    private static void Prefix(out Frame? __state)
    {
        LookupHooks? owner = installed ?? activeFrame?.Owner;
        __state = owner == null ? null : new Frame(owner, activeFrame);
        if (__state != null) activeFrame = __state;
    }

    private static Type? Probe(string? name)
    {
        Frame? frame = activeFrame;
        if (frame == null)
        {
            // An invocation can reach an old wrapper while the hooks are being removed.
            if (installed == null) return null;
            throw new InvalidOperationException("The shared type-cache invocation was not entered.");
        }
        if (frame.Request != null) throw new InvalidOperationException("A type-cache invocation probed more than once.");
        frame.Request = frame.Owner.Cache.Begin(name);
        return frame.Request?.CachedResult;
    }

    private static Type? Returned(Type? result)
    {
        Frame? frame = activeFrame;
        if (frame?.Request != null) frame.Owner.Cache.Returned(frame.Request, result);
        else if (frame == null && installed != null)
            throw new InvalidOperationException("The shared type-cache result has no invocation.");
        return result;
    }

    // A void finalizer preserves Harmony's current exception, including exceptions
    // from other patches. Publish the raw result only after the whole call succeeds.
    private static void Finalizer(Exception? __exception, Frame? __state)
    {
        if (__state == null) return;
        if (__state.Finished) return;
        __state.Finished = true;
        try
        {
            if (__state.Request != null) __state.Owner.Cache.Finish(__state.Request, __exception);
        }
        finally
        {
            if (activeFrame != __state) throw new InvalidOperationException("Type-cache invocations ended out of order.");
            activeFrame = __state.Parent;
        }
    }

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
    {
        // Other owners can replace the raw resolver with a transpiler. Any wrapper
        // rebuild must discard results obtained from its previous implementation.
        installed?.Cache.Invalidate();
        var original = new List<CodeInstruction>(instructions);
        int returns = 0;
        foreach (CodeInstruction instruction in original) if (instruction.opcode == OpCodes.Ret) returns++;
        if (returns == 0) throw new InvalidOperationException("Harmony TypeByName no longer has a supported return path.");
        Label miss = generator.DefineLabel();
        var continueOriginal = new CodeInstruction(OpCodes.Pop);
        // Harmony's labels field uses mscorlib's Label in the game's assembly.
        // IList avoids the netstandard facade mismatch without changing label values.
        ((IList)(typeof(CodeInstruction).GetField("labels")!.GetValue(continueOriginal)
            ?? throw new InvalidOperationException("Harmony instruction labels are unavailable."))).Add(miss);
        var result = new List<CodeInstruction>
        {
            new CodeInstruction(OpCodes.Ldarg_0),
            new CodeInstruction(OpCodes.Call, Callback(nameof(Probe))),
            new CodeInstruction(OpCodes.Dup),
            new CodeInstruction(OpCodes.Brfalse, miss),
            new CodeInstruction(OpCodes.Ret),
            continueOriginal
        };
        foreach (CodeInstruction instruction in original)
        {
            if (instruction.opcode == OpCodes.Ret)
            {
                var capture = new CodeInstruction(OpCodes.Call, Callback(nameof(Returned)));
                instruction.MoveLabelsTo(capture);
                instruction.MoveBlocksTo(capture);
                result.Add(capture);
            }
            result.Add(instruction);
        }
        return result;
    }

    private static MethodInfo Callback(string name) => typeof(LookupHooks).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!;
    private sealed class Frame
    {
        internal readonly LookupHooks Owner;
        internal readonly Frame? Parent;
        internal LookupRequest? Request;
        internal bool Finished;
        internal Frame(LookupHooks owner, Frame? parent) { Owner = owner; Parent = parent; }
    }
}
