using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace HarmonyStartupOptimizer;

internal sealed class LookupHooks : IOptimizerModule
{
    [ThreadStatic] private static Frame? activeFrame;
    private static LookupHooks? installed;
    private readonly PatchRegistry patches;
    private readonly AssemblyTypeCache assemblyTypes = new AssemblyTypeCache();
    private readonly TypeNameCache typeNames = new TypeNameCache();
    private bool subscribed;
    internal readonly TypeLookupCache Cache;
    public string Name => "TypeLookup";
    internal static MethodInfo Target => typeof(AccessTools).GetMethod(nameof(AccessTools.TypeByName), new[] { typeof(string) })
        ?? throw new MissingMethodException(typeof(AccessTools).FullName, "TypeByName(string)");
    internal static MethodInfo TypesTarget => typeof(AccessTools).GetMethod(nameof(AccessTools.GetTypesFromAssembly), new[] { typeof(Assembly) })
        ?? throw new MissingMethodException(typeof(AccessTools).FullName, "GetTypesFromAssembly(Assembly)");

    internal LookupHooks(string owner, TypeLookupCache cache)
    { patches = new PatchRegistry(new Harmony(owner)); Cache = cache; }

    public void Install()
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
            patches.Add(TypesTarget,
                transpiler: new HarmonyMethod(typeof(LookupHooks), nameof(TypesTranspiler)) { priority = Priority.Last });
            MethodInfo[] predicates = typeof(AccessTools).GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
                .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                .Where(m => m.Name.StartsWith("<TypeByName>", StringComparison.Ordinal) && m.ReturnType == typeof(bool) &&
                    m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(Type)).ToArray();
            if (predicates.Length != 2) throw new InvalidOperationException("Expected two TypeByName comparison predicates, found " + predicates.Length);
            foreach (MethodInfo predicate in predicates)
                patches.Add(predicate, transpiler: new HarmonyMethod(typeof(LookupHooks), nameof(NamesTranspiler)) { priority = Priority.Last });
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

    public void Uninstall()
    {
        Cache.Deactivate();
        assemblyTypes.Deactivate();
        typeNames.Deactivate();
        if (subscribed) { AppDomain.CurrentDomain.AssemblyLoad -= AssemblyLoaded; subscribed = false; }
        patches.RemoveAll();
        if (installed == this) installed = null;
    }

    private void AssemblyLoaded(object? sender, AssemblyLoadEventArgs args) => Invalidate();

    private void Invalidate()
    {
        Cache.Invalidate();
        assemblyTypes.Invalidate();
        typeNames.Invalidate();
    }

    private static Type[] EnumerateTypes(Assembly assembly)
    {
        LookupHooks? owner = installed;
        return owner == null ? assembly.GetTypes() : owner.assemblyTypes.GetTypes(assembly);
    }

    private static string? FullTypeName(Type type)
    {
        LookupHooks? owner = installed;
        return owner == null ? type.FullName : owner.typeNames.FullName(type);
    }

    private static string ShortTypeName(Type type)
    {
        LookupHooks? owner = installed;
        return owner == null ? type.Name : owner.typeNames.Name(type);
    }

    private static IEnumerable<CodeInstruction> NamesTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        installed?.Invalidate();
        int matches = 0;
        foreach (CodeInstruction instruction in instructions)
        {
            if (instruction.opcode == OpCodes.Callvirt && instruction.operand is MethodInfo method)
            {
                string? callback = method.DeclaringType == typeof(Type) && method.Name == "get_FullName" ? nameof(FullTypeName) :
                    method.DeclaringType == typeof(MemberInfo) && method.Name == "get_Name" ? nameof(ShortTypeName) : null;
                if (callback != null)
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = Callback(callback);
                    matches++;
                }
            }
            yield return instruction;
        }
        if (matches != 1) throw new InvalidOperationException("Expected one type-name getter in a comparison predicate, found " + matches);
    }

    private static IEnumerable<CodeInstruction> TypesTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        installed?.Invalidate();
        var result = new List<CodeInstruction>(instructions);
        int matches = 0;
        foreach (CodeInstruction instruction in result)
        {
            if (instruction.opcode == OpCodes.Callvirt && instruction.operand is MethodInfo method &&
                method.DeclaringType == typeof(Assembly) && method.Name == nameof(Assembly.GetTypes) &&
                method.GetParameters().Length == 0)
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = Callback(nameof(EnumerateTypes));
                matches++;
            }
        }
        if (matches != 1) throw new InvalidOperationException("Expected one Assembly.GetTypes call in " + TypesTarget + ", found " + matches);
        return result;
    }

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
