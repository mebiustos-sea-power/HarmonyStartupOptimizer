using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using HarmonyLib;
using HarmonyStartupOptimizer;

internal static class Program
{
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const string Owner = "mebiustos.harmonystartupoptimizer.smoke.cache";
    private static readonly Exception Expected = new InvalidOperationException("another patch's failure");
    private static readonly List<Exception> DiagnosticErrors = new List<Exception>();
    private static int prefixes, postfixes, originalBodies;
    private static bool alias, transform, skip, throwPrefix, recurse;
    private static int postfixFailures;

    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static MethodInfo Method(Type type, string name) => type.GetMethod(name, Static)!;
    private static void Reset() { alias = transform = skip = throwPrefix = recurse = false; postfixFailures = 0; }
    private static LookupHooks Hooks()
    {
        Reset();
        var hooks = new LookupHooks(Owner, new TypeLookupCache());
        hooks.Install(); hooks.Cache.Invalidate(); return hooks;
    }

    private static int Main(string[] args)
    {
        var observer = new Harmony("mebiustos.harmonystartupoptimizer.smoke.other");
        try
        {
            Console.WriteLine("Runtime: " + typeof(object).Assembly.Location);
            observer.Patch(LookupHooks.Target,
                prefix: new HarmonyMethod(typeof(Program), nameof(OtherPrefix)),
                postfix: new HarmonyMethod(typeof(Program), nameof(OtherPostfix)),
                transpiler: new HarmonyMethod(typeof(Program), nameof(ObserveOriginalBody)), finalizer: null, ilmanipulator: null);
            SharedResultsAndPatches();
            PrefixSkipsAndExceptions();
            PostfixFailure();
            NullArguments();
            NewAssemblyAndMissingType();
            ReentrantAndConcurrent();
            ResolverPatchChanges();
            if (args.Length != 0)
            {
                Assembly diagnostic = Assembly.LoadFrom(args[0]);
                Diagnostics(diagnostic, diagnosticsFirst: true);
                Diagnostics(diagnostic, diagnosticsFirst: false);
            }
            else Console.WriteLine("SKIP optional installed Startup Diagnostics integration: assembly not supplied");
            Assert(DiagnosticErrors.Count == 0, "Diagnostic errors: " + string.Join("; ", DiagnosticErrors));
            Console.WriteLine("PASS: own-Mono global cache, other Harmony patches, exceptions, assembly invalidation, concurrency, diagnostics and owner-specific cleanup");
            return 0;
        }
        catch (Exception error) { Console.WriteLine(error); return 1; }
        finally { Reset(); observer.Unpatch(LookupHooks.Target, HarmonyPatchType.All, observer.Id); }
    }

    private static bool OtherPrefix(ref string? __0, ref Type? __result)
    {
        Interlocked.Increment(ref prefixes);
        if (throwPrefix) throw Expected;
        if (alias && __0 == "alias fixture") __0 = "System.String";
        if (recurse && __0 == "System.String") Assert(AccessTools.TypeByName("System.Int32") == typeof(int), "Nested query changed.");
        if (!skip) return true;
        __result = typeof(Guid); return false;
    }
    private static void OtherPostfix(ref Type? __result)
    {
        Interlocked.Increment(ref postfixes);
        if (postfixFailures > 0) { postfixFailures--; throw Expected; }
        if (transform) __result = __result == typeof(string) ? typeof(int) : typeof(bool);
    }

    // Test-only instrumentation counts actual resolver-body executions. Production
    // code no longer needs counters or snapshots for these behavior assertions.
    private static IEnumerable<CodeInstruction> ObserveOriginalBody(IEnumerable<CodeInstruction> instructions)
    {
        yield return new CodeInstruction(OpCodes.Call, Method(typeof(Program), nameof(OriginalBodyEntered)));
        foreach (CodeInstruction instruction in instructions) yield return instruction;
    }
    private static void OriginalBodyEntered() => Interlocked.Increment(ref originalBodies);

    private static void SharedResultsAndPatches()
    {
        LookupHooks hooks = Hooks();
        try
        {
            int beforeOriginal = originalBodies;
            int beforePrefix = prefixes, beforePostfix = postfixes;
            alias = transform = true;
            Assert(AccessTools.TypeByName("alias fixture") == typeof(int), "Prefix argument mutation or postfix result changed.");
            Assert(AccessTools.TypeByName("alias fixture") == typeof(int), "A postprocessed result was transformed twice on a hit.");
            Assert(originalBodies - beforeOriginal == 1, "Shared query was not reused.");
            Assert(prefixes - beforePrefix == 2 && postfixes - beforePostfix == 2, "A cache hit skipped another owner's prefix or postfix.");
            Reset();
            Assert(AccessTools.TypeByName("System.String") == typeof(string) && originalBodies - beforeOriginal == 1,
                "Cache did not use the argument after other prefixes changed it.");
            Assert(AccessTools.TypeByName("alias fixture") == null, "The pre-prefix alias was incorrectly cached.");
            Reset(); hooks.Uninstall();
            var owners = Harmony.GetPatchInfo(LookupHooks.Target)!.Owners;
            Assert(!owners.Contains(Owner) && owners.Contains("mebiustos.harmonystartupoptimizer.smoke.other"), "Uninstall removed another owner or left own patches.");
            Assert(AccessTools.TypeByName("System.String") == typeof(string), "Uninstall broke original resolution.");
            Console.WriteLine("PASS shared cache: raw Type, modified arguments, every-call prefix/postfix, owner-specific cleanup");
        }
        finally { Reset(); hooks.Uninstall(); }
    }

    private static void PrefixSkipsAndExceptions()
    {
        LookupHooks hooks = Hooks();
        try
        {
            int beforeOriginal = originalBodies;
            skip = true;
            Assert(AccessTools.TypeByName("System.String") == typeof(Guid) && AccessTools.TypeByName("System.String") == typeof(Guid), "Another prefix's result was overridden.");
            Assert(originalBodies == beforeOriginal, "An invocation skipped by another prefix entered the resolver body.");
            skip = false; throwPrefix = true;
            try { AccessTools.TypeByName("System.String"); throw new Exception("Prefix exception was swallowed."); }
            catch (Exception error) { Assert(ReferenceEquals(error, Expected), "Prefix exception changed."); }
            throwPrefix = false;
            Assert(AccessTools.TypeByName("System.String") == typeof(string), "Prefix failure leaked invocation state.");
            Console.WriteLine("PASS other prefix: skipped original, returned value, exception identity and frame cleanup");
        }
        finally { Reset(); hooks.Uninstall(); }
    }

    private static void PostfixFailure()
    {
        LookupHooks hooks = Hooks();
        try
        {
            int beforeOriginal = originalBodies;
            postfixFailures = 1;
            try { AccessTools.TypeByName("System.String"); throw new Exception("Postfix exception was swallowed."); }
            catch (Exception error) { Assert(ReferenceEquals(error, Expected), "Postfix exception changed."); }
            Assert(AccessTools.TypeByName("System.String") == typeof(string) && AccessTools.TypeByName("System.String") == typeof(string), "Resolution did not recover.");
            Assert(originalBodies - beforeOriginal == 2, "A failed call populated the cache or a successful result was not reused.");
            Console.WriteLine("PASS postfix failure: exception propagated, failed raw result not published, next call retried");
        }
        finally { Reset(); hooks.Uninstall(); }
    }

    private static Exception NullError()
    { try { AccessTools.TypeByName(null!); throw new Exception("Null unexpectedly resolved."); } catch (ArgumentNullException error) { return error; } }
    private static void NullArguments()
    {
        Exception original = NullError();
        LookupHooks hooks = Hooks();
        try
        {
            int beforeOriginal = originalBodies;
            Exception first = NullError(), second = NullError();
            Assert(first.GetType() == original.GetType() && ((ArgumentNullException)first).ParamName == ((ArgumentNullException)original).ParamName,
                "The original null-argument failure changed.");
            Assert(second.GetType() == original.GetType(), "A null failure was cached or changed on repeat.");
            Assert(originalBodies - beforeOriginal == 2, "Null failures were cached.");
            Assert(AccessTools.TypeByName("System.String") == typeof(string), "A null failure leaked invocation state.");
            Console.WriteLine("PASS real TypeByName null: original exception contract and repeat behavior");
        }
        finally { hooks.Uninstall(); }
    }

    private static void NewAssemblyAndMissingType()
    {
        LookupHooks hooks = Hooks();
        try
        {
            string name = "HarmonyStartupOptimizer.EmittedFixture";
            int beforeOriginal = originalBodies;
            Assert(AccessTools.TypeByName(name) == null && AccessTools.TypeByName(name) == null, "Missing type unexpectedly resolved.");
            Assert(originalBodies - beforeOriginal == 2, "A missing result was cached.");
            AccessTools.TypeByName("System.String"); AccessTools.TypeByName("System.String");
            var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("HarmonyStartupOptimizerSmokeFixture"), AssemblyBuilderAccess.Run);
            Type emitted = assembly.DefineDynamicModule("fixture").DefineType(name, TypeAttributes.Public).CreateTypeInfo()!.AsType();
            beforeOriginal = originalBodies;
            AccessTools.TypeByName("System.String");
            Assert(originalBodies == beforeOriginal + 1, "An entry from before assembly load was reused.");
            beforeOriginal = originalBodies;
            Assert(AccessTools.TypeByName(name) == emitted && AccessTools.TypeByName(name) == emitted, "Newly loaded type remained invisible or changed identity.");
            Assert(originalBodies == beforeOriginal + 1, "The new successful result was not reused.");
            Console.WriteLine("PASS actual assembly event: old entries invalidated, missing query retried, emitted Type visible");
        }
        finally { hooks.Uninstall(); }
    }

    private static void ReentrantAndConcurrent()
    {
        LookupHooks hooks = Hooks();
        try
        {
            recurse = true;
            Assert(AccessTools.TypeByName("System.String") == typeof(string) && AccessTools.TypeByName("System.String") == typeof(string), "Reentrant resolution failed.");
            recurse = false;
            int beforePrefix = prefixes;
            Exception? failure = null;
            var threads = Enumerable.Range(0, 8).Select(_ => new Thread(() =>
            {
                try { for (int i = 0; i < 10; i++) Assert(AccessTools.TypeByName("System.String") == typeof(string), "Concurrent Type changed."); }
                catch (Exception error) { Interlocked.CompareExchange(ref failure, error, null); }
            })).ToArray();
            foreach (Thread thread in threads) thread.Start(); foreach (Thread thread in threads) thread.Join();
            Assert(failure == null && prefixes - beforePrefix == 80,
                "Concurrent invocations shared frames or lost requests: " + failure);
            Console.WriteLine("PASS reentrant and parallel TypeByName: independent frames and original results");
        }
        finally { Reset(); hooks.Uninstall(); }
    }

    private static void ResolverPatchChanges()
    {
        LookupHooks hooks = Hooks();
        var foreign = new Harmony("mebiustos.harmonystartupoptimizer.smoke.resolverchange");
        try
        {
            Assert(AccessTools.TypeByName("System.String") == typeof(string), "Original resolver changed before another transpiler.");
            foreign.Patch(LookupHooks.Target, prefix: null, postfix: null,
                transpiler: new HarmonyMethod(typeof(Program), nameof(ChangeResolver)), finalizer: null, ilmanipulator: null);
            Assert(AccessTools.TypeByName("System.String") == typeof(int), "A result from before another resolver patch was reused.");
            foreign.Unpatch(LookupHooks.Target, HarmonyPatchType.All, foreign.Id);
            Assert(AccessTools.TypeByName("System.String") == typeof(string), "A result from a removed resolver patch was reused.");
            Console.WriteLine("PASS resolver changes: adding and removing another transpiler invalidates previous results");
        }
        finally { foreign.Unpatch(LookupHooks.Target, HarmonyPatchType.All, foreign.Id); hooks.Uninstall(); }
    }

    private static IEnumerable<CodeInstruction> ChangeResolver(IEnumerable<CodeInstruction> instructions)
    {
        foreach (CodeInstruction instruction in instructions)
        {
            if (instruction.opcode == OpCodes.Ret)
            {
                var change = new CodeInstruction(OpCodes.Call, Method(typeof(Program), nameof(ChangeRawType)));
                instruction.MoveLabelsTo(change); instruction.MoveBlocksTo(change);
                yield return change;
            }
            yield return instruction;
        }
    }
    private static Type? ChangeRawType(Type? type) => type == typeof(string) ? typeof(int) : type;

    private static void Guard(string area, Action action)
    { try { action(); } catch (Exception error) { DiagnosticErrors.Add(new Exception(area, error)); } }

    private static void Diagnostics(Assembly diagnostic, bool diagnosticsFirst)
    {
        var recorder = new DiagnosticHarness(diagnostic);
        LookupHooks? hooks = null;
        try
        {
            if (diagnosticsFirst) recorder.Install();
            hooks = Hooks();
            if (!diagnosticsFirst) recorder.Install();
            hooks.Cache.Invalidate();
            int beforeOriginal = originalBodies;
            recorder.Enter();
            try
            {
                Assert(AccessTools.TypeByName("System.String") == typeof(string), "Diagnostic miss changed.");
                Assert(AccessTools.TypeByName("System.String") == typeof(string), "Diagnostic hit changed.");
            }
            finally { recorder.Leave(); }
            Assert(originalBodies == beforeOriginal + 1 && recorder.Completed() == 2, "Diagnostics did not observe both miss and hit.");
            hooks.Uninstall();
            Assert(Harmony.GetPatchInfo(LookupHooks.Target)!.Owners.Contains("mebiustos.startupdiagnostics"), "Optimizer cleanup removed diagnostics.");
            recorder.Enter(); try { AccessTools.TypeByName("System.String"); } finally { recorder.Leave(); }
            Assert(recorder.Completed() == 3, "Diagnostics stopped after cache removal.");
            Console.WriteLine("PASS installed Startup Diagnostics: " + (diagnosticsFirst ? "diagnostics first" : "optimizer first") + "; all calls measured, diagnostics survives removal");
        }
        finally { hooks?.Uninstall(); recorder.Uninstall(); }
    }

    private sealed class DiagnosticHarness
    {
        private readonly Type hooks;
        private readonly object registry, session;
        internal DiagnosticHarness(Assembly assembly)
        {
            hooks = assembly.GetType("StartupDiagnostics.FactHooks", true)!;
            Type recorder = assembly.GetType("StartupDiagnostics.Core.DiagnosticSession", true)!;
            var timer = Stopwatch.StartNew();
            session = Activator.CreateInstance(recorder, Instance, null,
                new object[] { (Func<double>)(() => timer.Elapsed.TotalMilliseconds), (Action<object>)(_ => { }) }, CultureInfo.InvariantCulture)!;
            Type registryType = assembly.GetType("StartupDiagnostics.PatchRegistry", true)!;
            registry = Activator.CreateInstance(registryType, Instance, null, new object[] { new Harmony("mebiustos.startupdiagnostics") }, CultureInfo.InvariantCulture)!;
            Method(hooks, "Configure").Invoke(null, new object[] { session, (Action<string, Action>)Guard });
        }
        internal void Install() => Method(hooks, "InstallLookup").Invoke(null, new[] { registry });
        internal void Enter() => Method(hooks, "EnterDcm").Invoke(null, null);
        internal void Leave() => Method(hooks, "LeaveDcm").Invoke(null, new object?[] { null });
        internal void Uninstall() => registry.GetType().GetMethod("RemoveAll", Instance)!.Invoke(registry, null);
        internal int Completed()
        {
            object snapshot = session.GetType().GetMethod("Snapshot", Instance)!.Invoke(session, null)!;
            var operations = (IEnumerable)snapshot.GetType().GetField("Completed", Instance)!.GetValue(snapshot)!;
            int count = 0;
            foreach (object operation in operations)
                if ((string)operation.GetType().GetField("Kind", Instance)!.GetValue(operation)! == "type.lookup") count++;
            return count;
        }
    }
}
