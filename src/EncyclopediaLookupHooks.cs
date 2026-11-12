using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using SeaPower;

namespace HarmonyStartupOptimizer;

internal sealed class EncyclopediaLookupHooks : IOptimizerModule
{
    private enum Projection { IniRef, DisplayName, Aircraft }
    [ThreadStatic] private static Frame? current;
    private static EncyclopediaLookupHooks? installed;
    private readonly PatchRegistry patches;
    private readonly Assembly game;
    private readonly Dictionary<MethodInfo, Plan> plans = new Dictionary<MethodInfo, Plan>();
    private readonly List<WeakReference<Scope>> scopes = new List<WeakReference<Scope>>();
    private ConditionalWeakTable<object, Scope> iterators = new ConditionalWeakTable<object, Scope>();
    public string Name => "EncyclopediaLookup";

    internal EncyclopediaLookupHooks(string owner, Assembly game)
    { patches = new PatchRegistry(new Harmony(owner)); this.game = game; }

    internal static EncyclopediaLookupHooks? CreateForGame(string owner, Assembly game, Action<string> warning)
    {
        if (SupportsGameBuild(game)) return new EncyclopediaLookupHooks(owner, game);
        warning("Encyclopedia optimization skipped: unsupported Sea Power build.");
        return null;
    }

    private static bool SupportsGameBuild(Assembly game)
    {
        Guid moduleVersionId = game.ManifestModule.ModuleVersionId;
        return moduleVersionId == new Guid("cc2f4c48-9fd9-47f0-a37c-896481c22c45") // 0.9.0-261111
            || moduleVersionId == new Guid("875e9708-6803-45c5-96b4-008c762358eb"); // 0.9.1-261112
    }

    public void Install()
    {
        if (installed != null) throw new InvalidOperationException("Encyclopedia lookup optimization is already installed.");
        if (!SupportsGameBuild(game))
            throw new NotSupportedException("Encyclopedia optimization requires a verified Sea Power game assembly.");
        Type manager = game.GetType("SeaPower.EncyclopedicDataManager", true)!;
        MethodInfo factory = manager.GetMethod("LoadUnitsFromMetadata", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new MissingMethodException(manager.FullName, "LoadUnitsFromMetadata");
        Type iterator = factory.GetCustomAttribute<IteratorStateMachineAttribute>()?.StateMachineType
            ?? throw new InvalidOperationException("The encyclopedia iterator metadata is unavailable.");
        MethodInfo move = iterator.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(iterator.FullName, "MoveNext");
        MethodInfo dispose = iterator.GetMethod("System.IDisposable.Dispose", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(iterator.FullName, "IDisposable.Dispose");
        AddPlan(manager, 9, Projection.DisplayName, "unitType");
        AddPlan(manager, 12, Projection.Aircraft, "obAircraft", "AircraftIniName", "SquadronIndex");
        AddPlan(manager, 13, Projection.IniRef, "sensorRef");
        AddPlan(manager, 14, Projection.IniRef, "ammunitionRef");
        AddPlan(manager, 15, Projection.IniRef, "ammunitionRef");
        AddPlan(manager, 16, Projection.IniRef, "systemRef", "Item1");
        AddPlan(manager, 17, Projection.IniRef, "systemRef", "Item2");
        AddPlan(manager, 24, Projection.IniRef, "pod", "SystemName");
        AddPlan(manager, 25, Projection.IniRef, "podCount", "Key");
        installed = this;
        patches.Add(move, prefix: Callback(nameof(Prefix)), finalizer: Callback(nameof(Finalizer)), transpiler: Callback(nameof(Transpiler)));
        patches.Add(dispose, finalizer: Callback(nameof(Disposed)));
        foreach (string property in new[] { nameof(MuseumEntry.IniRef), nameof(MuseumEntry.VariantIndex), nameof(MuseumEntry.DisplayName) })
            patches.Add(typeof(MuseumEntry).GetProperty(property)!.SetMethod!, postfix: Callback(nameof(KeyChanged)));
    }

    public void Uninstall()
    {
        if (installed == this) installed = null;
        foreach (var weak in scopes) if (weak.TryGetTarget(out Scope? scope)) scope.Dispose();
        scopes.Clear();
        iterators = new ConditionalWeakTable<object, Scope>();
        patches.RemoveAll();
        plans.Clear();
    }

    private void AddPlan(Type manager, int suffix, Projection kind, string fieldName, string? first = null, string? second = null)
    {
        string name = "<LoadUnitsFromMetadata>b__" + suffix;
        MethodInfo method = manager.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
            .SelectMany(t => t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            .Single(m => m.Name == name);
        FieldInfo field = method.DeclaringType!.GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(method.DeclaringType.FullName, fieldName);
        MemberInfo? firstMember = first == null ? null : RequiredMember(field.FieldType, first);
        MemberInfo? secondMember = second == null ? null : RequiredMember(field.FieldType, second);
        plans.Add(method, new Plan(kind, target =>
        {
            object? value = field.GetValue(target);
            return ((string?)Read(value, firstMember), secondMember == null ? 0 : (int)Read(value, secondMember)!);
        }));
    }

    private static MemberInfo RequiredMember(Type type, string name) =>
        (MemberInfo?)type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) ??
        type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) ??
        throw new MissingMemberException(type.FullName, name);

    private static object? Read(object? value, MemberInfo? member)
    {
        if (member == null) return value;
        return member is FieldInfo field ? field.GetValue(value) : ((PropertyInfo)member).GetValue(value);
    }

    private static HarmonyMethod Callback(string name) => new HarmonyMethod(typeof(EncyclopediaLookupHooks), name) { priority = Priority.First };

    private static void Prefix(object __instance, out Frame? __state)
    {
        EncyclopediaLookupHooks? owner = installed;
        if (owner == null) { __state = null; return; }
        Scope scope = owner.iterators.GetValue(__instance, ignored =>
        {
            var created = new Scope();
            owner.scopes.RemoveAll(w => !w.TryGetTarget(out _));
            owner.scopes.Add(new WeakReference<Scope>(created));
            return created;
        });
        __state = new Frame(owner, scope, current);
        current = __state;
    }

    private static void Finalizer(object __instance, bool __result, Exception? __exception, Frame? __state)
    {
        if (__state == null) return;
        current = __state.Parent;
        if (!__result || __exception != null) __state.Owner.Release(__instance);
    }

    private static void Disposed(object __instance) => installed?.Release(__instance);

    private void Release(object iterator)
    {
        if (!iterators.TryGetValue(iterator, out Scope? scope)) return;
        scope.Dispose(); iterators.Remove(iterator);
    }

    private static void KeyChanged(MuseumEntry __instance)
    {
        EncyclopediaLookupHooks? owner = installed;
        if (owner == null) return;
        foreach (var weak in owner.scopes)
            if (weak.TryGetTarget(out Scope? scope)) scope.InvalidateItem(__instance);
    }

    private static IEnumerable<T> Narrow<T>(IEnumerable<T> source, Func<T, bool> predicate) where T : class
    {
        Frame? frame = current;
        if (frame == null || installed != frame.Owner || predicate == null || !(source is IList<T> list) || !(source is INotifyCollectionChanged notifications) ||
            !frame.Owner.plans.TryGetValue(predicate.Method, out Plan? plan) || Patched(predicate.Method) || Patched(SearchMethod<T>.Original) || plan.PatchedGetters)
            return source;
        try
        {
            var key = plan.Wanted(predicate.Target!);
            T? found = frame.Scope.Find(list, notifications, plan.Kind, key);
            return found == null ? Array.Empty<T>() : new[] { found };
        }
        catch (Exception error)
        {
            // Stop only this module, report the infrastructure failure, then let
            // the original LINQ call retain its own result/exception behavior.
            OptimizerStartup.Log.LogError("EncyclopediaLookup stopped after an error: " + error);
            try { frame.Owner.Uninstall(); }
            catch (Exception cleanup) { OptimizerStartup.Log.LogError("EncyclopediaLookup cleanup failed: " + cleanup); }
            return source;
        }
    }

    private static bool Patched(MethodBase method) => Harmony.GetPatchInfo(method)?.Owners.Count > 0;

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
    {
        int matches = 0;
        var locals = new Dictionary<Type, LocalBuilder>();
        foreach (CodeInstruction instruction in instructions)
        {
            if (instruction.opcode == OpCodes.Call && instruction.operand is MethodInfo method && method.DeclaringType == typeof(Enumerable) &&
                method.Name == nameof(Enumerable.FirstOrDefault) && method.IsGenericMethod && method.GetParameters().Length == 2 &&
                method.GetParameters()[1].ParameterType.IsGenericType && method.GetParameters()[1].ParameterType.GetGenericTypeDefinition() == typeof(Func<,>))
            {
                Type item = method.GetGenericArguments()[0];
                Type predicateType = typeof(Func<,>).MakeGenericType(item, typeof(bool));
                if (!locals.TryGetValue(item, out LocalBuilder? local)) locals.Add(item, local = generator.DeclareLocal(predicateType));
                var save = new CodeInstruction(OpCodes.Stloc, local);
                instruction.MoveLabelsTo(save);
                foreach (ExceptionBlock block in instruction.blocks)
                    if (block.blockType != ExceptionBlockType.EndExceptionBlock) save.blocks.Add(block);
                instruction.blocks.RemoveAll(b => b.blockType != ExceptionBlockType.EndExceptionBlock);
                yield return save;
                yield return new CodeInstruction(OpCodes.Ldloc, local);
                yield return new CodeInstruction(OpCodes.Call, typeof(EncyclopediaLookupHooks).GetMethod(nameof(Narrow), BindingFlags.Static | BindingFlags.NonPublic)!.MakeGenericMethod(item));
                yield return new CodeInstruction(OpCodes.Ldloc, local);
                matches++;
            }
            yield return instruction;
        }
        if (matches != 9) throw new InvalidOperationException("Expected nine encyclopedia reference searches, found " + matches);
    }

    private sealed class Plan
    {
        internal readonly Projection Kind;
        internal readonly Func<object, (string?, int)> Wanted;
        internal Plan(Projection kind, Func<object, (string?, int)> wanted) { Kind = kind; Wanted = wanted; }
        internal bool PatchedGetters => Patched(typeof(MuseumEntry).GetProperty(Kind == Projection.DisplayName ? nameof(MuseumEntry.DisplayName) : nameof(MuseumEntry.IniRef))!.GetMethod!) ||
            (Kind == Projection.Aircraft && Patched(typeof(MuseumEntry).GetProperty(nameof(MuseumEntry.VariantIndex))!.GetMethod!));
    }

    private static class SearchMethod<T>
    {
        internal static readonly MethodInfo Original = typeof(Enumerable).GetMethods().Single(m => m.Name == nameof(Enumerable.FirstOrDefault) &&
            m.IsGenericMethodDefinition && m.GetParameters().Length == 2 && m.GetParameters()[1].ParameterType.IsGenericType &&
            m.GetParameters()[1].ParameterType.GetGenericTypeDefinition() == typeof(Func<,>)).MakeGenericMethod(typeof(T));
    }

    private sealed class Frame
    {
        internal readonly EncyclopediaLookupHooks Owner;
        internal readonly Scope Scope;
        internal readonly Frame? Parent;
        internal Frame(EncyclopediaLookupHooks owner, Scope scope, Frame? parent) { Owner = owner; Scope = scope; Parent = parent; }
    }

    private interface IIndex : IDisposable { void InvalidateItem(object item); }

    private sealed class Index<T> : IIndex where T : class
    {
        internal readonly ReferenceIndex<T, (string?, int)> Values;
        internal Index(IList<T> source, INotifyCollectionChanged events, Projection kind) => Values =
            new ReferenceIndex<T, (string?, int)>(source, events, item =>
            {
                var entry = (MuseumEntry)(object)item;
                return (kind == Projection.DisplayName ? entry.DisplayName : entry.IniRef, kind == Projection.Aircraft ? entry.VariantIndex : 0);
            });
        public void InvalidateItem(object item) => Values.InvalidateItem(item);
        public void Dispose() => Values.Dispose();
    }

    private sealed class Scope : IDisposable
    {
        private readonly Dictionary<object, Dictionary<Projection, IIndex>> sources = new Dictionary<object, Dictionary<Projection, IIndex>>(ReferenceComparer<object>.Instance);
        internal T? Find<T>(IList<T> source, INotifyCollectionChanged events, Projection kind, (string?, int) wanted) where T : class
        {
            if (!sources.TryGetValue(source, out Dictionary<Projection, IIndex>? indices)) sources.Add(source, indices = new Dictionary<Projection, IIndex>());
            if (!indices.TryGetValue(kind, out IIndex? index)) indices.Add(kind, index = new Index<T>(source, events, kind));
            return ((Index<T>)index).Values.Find(wanted);
        }
        internal void InvalidateItem(object item) { foreach (var source in sources.Values) foreach (IIndex index in source.Values) index.InvalidateItem(item); }
        public void Dispose() { foreach (var source in sources.Values) foreach (IIndex index in source.Values) index.Dispose(); sources.Clear(); }
    }
}
