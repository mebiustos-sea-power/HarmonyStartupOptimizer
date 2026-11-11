using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
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
    private static int enumerationPostfixes;
    private static bool changeEnumeration;

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
            var startupTiming = StartupTiming.ForCurrentProcess();
            startupTiming.Complete();
            Assert(startupTiming.Seconds >= 0 && !double.IsInfinity(startupTiming.Seconds.Value),
                "The game runtime could not measure elapsed time from process creation.");
            Console.WriteLine("PASS process-start timing: native process creation time and monotonic elapsed clock");
            OptionalStartupSettings();
            Assembly game = Assembly.LoadFrom(Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "Seapower-Scripts.dll"));
            _ = new StartupScreen(game);
            Console.WriteLine("PASS startup display: menu and loading-screen API bindings in the current game");
            DataOptimizationChecks.Run(game);
            observer.Patch(LookupHooks.Target,
                prefix: new HarmonyMethod(typeof(Program), nameof(OtherPrefix)),
                postfix: new HarmonyMethod(typeof(Program), nameof(OtherPostfix)),
                transpiler: new HarmonyMethod(typeof(Program), nameof(ObserveOriginalBody)), finalizer: null, ilmanipulator: null);
            SharedEnumerations();
            SharedResultsAndPatches();
            PrefixSkipsAndExceptions();
            PostfixFailure();
            NullArguments();
            NewAssemblyAndMissingType();
            ReentrantAndConcurrent();
            ResolverPatchChanges();
            StartupOnlyShutdown(game);
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

    private static void OptionalStartupSettings()
    {
        string root = Path.Combine(Path.GetTempPath(), "HarmonyStartupOptimizer-settings-" + Guid.NewGuid().ToString("N"));
        string directory = Path.Combine(root, "optional");
        string path = Path.Combine(directory, "startup.cfg");
        var warnings = new List<string>();
        try
        {
            // The game normally initializes these paths before any mod reads settings.
            Type paths = Assembly.Load("BepInEx").GetType("BepInEx.Paths", true)!;
            Method(paths, "SetExecutablePath").Invoke(null, new object?[]
                { Path.Combine(root, "Sea Power.exe"), Path.Combine(root, "BepInEx"), Path.GetDirectoryName(typeof(object).Assembly.Location), null });
            StartupSettings defaults = StartupSettings.Read(path, warnings.Add);
            Assert(defaults.OverlayEnabled && !defaults.StopAfterStartup && defaults.TypeLookup && defaults.FileListing && defaults.EncyclopediaLookup && !Directory.Exists(directory),
                "Missing settings must preserve the overlay and continuous caching without creating files or directories.");
            Directory.CreateDirectory(directory);
            foreach (var example in new[]
            {
                (Text: "[Overlay]\nEnabled = true\n", Overlay: true, Stop: false),
                (Text: "[Overlay]\nEnabled = false\n", Overlay: false, Stop: false),
                (Text: "[Cache]\nStopAfterStartup = true\n", Overlay: true, Stop: true),
                (Text: "[Overlay]\nEnabled = false\n[Cache]\nStopAfterStartup = true\n", Overlay: false, Stop: true),
                (Text: "[Overlay]\nEnabled = false\n[Cache]\nStopAfterStartup = false\n", Overlay: false, Stop: false)
            })
            {
                string contents = "# User settings = ignored comment\n" + example.Text;
                File.WriteAllText(path, contents);
                File.SetAttributes(path, FileAttributes.ReadOnly);
                try
                {
                    StartupSettings settings = StartupSettings.Read(path, warnings.Add);
                    Assert(settings.OverlayEnabled == example.Overlay && settings.StopAfterStartup == example.Stop,
                        "The user's startup settings or defaults for omitted options were ignored.");
                    Assert(File.ReadAllText(path) == contents, "Reading settings changed the user's file.");
                }
                finally { File.SetAttributes(path, FileAttributes.Normal); }
            }
            foreach (string key in new[] { "TypeLookup", "FileListing", "EncyclopediaLookup" })
            {
                File.WriteAllText(path, "[Optimizations]\n" + key + " = false\n");
                StartupSettings settings = StartupSettings.Read(path, warnings.Add);
                Assert(settings.TypeLookup == (key != "TypeLookup") && settings.FileListing == (key != "FileListing") &&
                    settings.EncyclopediaLookup == (key != "EncyclopediaLookup"), "A per-module option affected a different module or ignored omitted defaults.");
            }
            Assert(warnings.Count == 0, "Absent or valid settings, comments and omitted options must not produce warnings.");
            string misspelled = "# TypeLookpu = ignored comment\n\n  [Optimizations]  \nTypeLookpu = invalid\n FileListing = false \n" +
                "[Overaly]\nEnabled = invalid\nUnused = true\n[Cache]\nStopAfterStartup = true\n";
            File.WriteAllText(path, misspelled);
            StartupSettings misspelledSettings = StartupSettings.Read(path, warnings.Add);
            Assert(misspelledSettings.TypeLookup && !misspelledSettings.FileListing && misspelledSettings.EncyclopediaLookup &&
                misspelledSettings.OverlayEnabled && misspelledSettings.StopAfterStartup,
                "Unknown entries must preserve omitted defaults and leave valid settings effective.");
            Assert(warnings.Count == 2 && warnings[0].Contains("Optimizations") && warnings[0].Contains("TypeLookpu") &&
                warnings[1].Contains("Overaly") && warnings.All(message => message.Contains(path)),
                "Warnings must identify the unknown key or section and its file without repeating each key in an unknown section.");
            Assert(File.ReadAllText(path) == misspelled, "Unknown settings must be reported without rewriting the user's file.");
            foreach (string contents in new[] { "[Overlay]\nEnabled = invalid\n", "[Cache]\nStopAfterStartup = invalid\n", "[Optimizations]\nFileListing = invalid\n" })
            {
                File.WriteAllText(path, contents);
                try { StartupSettings.Read(path, warnings.Add); throw new Exception("An invalid startup setting silently used a default."); }
                catch (FormatException) { }
            }
            Console.WriteLine("PASS optional startup settings: absent and omitted defaults, independent options, read-only file unchanged, unknown entries warned, invalid values rejected");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static void StartupOnlyShutdown(Assembly game)
    {
        foreach (bool display in new[] { true, false })
        foreach (bool stopAfterStartup in new[] { true, false })
        {
            Reset();
            var hooks = new LookupHooks(Owner, new TypeLookupCache());
            var modules = new OptimizationSet((_, error) => throw error);
            try
            {
                modules.Install(hooks, true);
                modules.Install(new FileListingHooks(Owner + ".files", game), true);
                var encyclopedia = EncyclopediaLookupHooks.CreateForGame(Owner + ".encyclopedia", game, _ => { });
                if (encyclopedia != null) modules.Install(encyclopedia, true);
                AccessTools.TypeByName("System.String"); AccessTools.TypeByName("System.String");
                bool menu = false;
                int detections = 0, shutdowns = 0;
                var lifetime = new StartupLifetime(() => { detections++; return menu; }, display,
                    stopAfterStartup ? (Action)(() => { shutdowns++; modules.Stop(); }) : null);
                lifetime.Tick(0);
                lifetime.Tick(100);
                Assert(!lifetime.MenuDetected && shutdowns == 0 && hooks.Cache.Begin("System.String") != null,
                    "Startup-only caching stopped while the startup screen was still visible.");
                menu = true;
                lifetime.Tick(101);
                Assert(lifetime.MenuDetected, "The main menu did not complete startup detection.");
                if (display)
                {
                    lifetime.Tick(110);
                    Assert(!lifetime.Finished, "Display time elapsed before the hit count could be rendered.");
                    lifetime.DisplayRendered(110);
                }
                float end = display ? 115 : 106;
                menu = false;
                int detected = detections;
                lifetime.Tick(end - 0.1f);
                Assert(lifetime.MenuDetected && !lifetime.Finished && detections == detected,
                    "Starting another load reset the startup display or completion timer.");
                lifetime.Tick(end);
                lifetime.Tick(end + 10);
                Assert(lifetime.Finished && shutdowns == (stopAfterStartup ? 1 : 0),
                    "Startup shutdown ignored the option, required a display, or ran more than once.");
                var owners = Harmony.GetPatchInfo(LookupHooks.Target)!.Owners;
                Assert(owners.Contains(Owner) != stopAfterStartup && owners.Contains("mebiustos.harmonystartupoptimizer.smoke.other"),
                    "Startup shutdown left own patches installed or removed another mod's patches.");
                int beforeOriginal = originalBodies;
                AccessTools.TypeByName("System.String"); AccessTools.TypeByName("System.String");
                Assert(originalBodies - beforeOriginal == (stopAfterStartup ? 2 : 0),
                    "Startup shutdown did not restore uncached lookups, or continuous caching stopped.");
                Assert((hooks.Cache.Begin("System.String") == null) == stopAfterStartup,
                    "Startup shutdown retained an active cache.");
                foreach (string owner in new[] { Owner, Owner + ".files", Owner + ".encyclopedia" })
                {
                    bool expectedActive = !stopAfterStartup && (owner != Owner + ".encyclopedia" || encyclopedia != null);
                    Assert(Harmony.GetAllPatchedMethods().Any(m => Harmony.GetPatchInfo(m)!.Owners.Contains(owner)) == expectedActive,
                        "Common startup shutdown left the wrong module state: " + owner);
                }
            }
            finally { modules.Stop(); }
        }
        bool visible = false;
        int completed = 0;
        var failedDisplay = new StartupLifetime(() => visible, true, () => completed++);
        failedDisplay.Tick(0);
        failedDisplay.DisplayUnavailable(1);
        failedDisplay.Tick(100);
        Assert(!failedDisplay.Finished, "A failed display was treated as startup completion.");
        visible = true;
        failedDisplay.Tick(101);
        failedDisplay.Tick(106);
        Assert(completed == 1, "A failed display prevented startup completion.");
        Console.WriteLine("PASS startup lifetime: continuous or startup-only caching, with or without display, reload stability and display failure");
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

    // Counts actual resolver-body executions independently of the display counter.
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
            Assert(hooks.Cache.Hits == 1, "The displayed counter did not record the actual shared-cache hit.");
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
            Assert(hooks.Cache.Hits == 0, "Calls skipped by another prefix were reported as cache hits.");
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
            Assert(hooks.Cache.Hits == 1, "Failed or uncached calls were reported as cache hits.");
            postfixFailures = 1;
            try { AccessTools.TypeByName("System.String"); throw new Exception("Cached-call postfix exception was swallowed."); }
            catch (Exception error) { Assert(ReferenceEquals(error, Expected), "Cached-call postfix exception changed."); }
            Assert(hooks.Cache.Hits == 1, "A cache hit that failed in a postfix was reported as a successful reuse.");
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
            Assert(AccessTools.GetTypesFromAssembly(assembly).Contains(emitted), "Emitted type missing from enumeration.");
            Type later = emitted.Module is ModuleBuilder module
                ? module.DefineType(name + "Later", TypeAttributes.Public).CreateTypeInfo()!.AsType()
                : throw new InvalidOperationException("Dynamic module unavailable.");
            Assert(AccessTools.GetTypesFromAssembly(assembly).Contains(later), "A growing dynamic assembly retained an old type list.");
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

    private static void SharedEnumerations()
    {
        var other = new Harmony("mebiustos.harmonystartupoptimizer.smoke.types");
        other.Patch(LookupHooks.TypesTarget, postfix: new HarmonyMethod(typeof(Program), nameof(EnumerationPostfix)));
        LookupHooks hooks = Hooks();
        try
        {
            Assert(AccessTools.TypeByName(nameof(TypeLookupCache)) == typeof(TypeLookupCache), "Short-name fallback changed.");
            Assembly assembly = typeof(Program).Assembly;
            Type[] expected = assembly.GetTypes();
            enumerationPostfixes = 0;
            changeEnumeration = true;
            Type[] first = AccessTools.GetTypesFromAssembly(assembly);
            Assert(first[0] == typeof(Guid), "Another enumeration postfix was skipped.");
            changeEnumeration = false;
            first[1] = typeof(Guid);
            Assert(AccessTools.GetTypesFromAssembly(assembly).SequenceEqual(expected), "A caller or postfix mutated the shared type list.");
            Assert(enumerationPostfixes == 2, "Enumeration cache bypassed another owner's postfix.");
            hooks.Uninstall();
            var owners = Harmony.GetPatchInfo(LookupHooks.TypesTarget)!.Owners;
            Assert(!owners.Contains(Owner) && owners.Contains(other.Id), "Enumeration cleanup changed another owner's patches.");
            Assert(AccessTools.GetTypesFromAssembly(assembly).SequenceEqual(expected), "Cleanup did not restore ordinary enumeration.");
            Console.WriteLine("PASS shared enumerations: result arrays, every-call postfix and owner-specific cleanup");
        }
        finally { changeEnumeration = false; hooks.Uninstall(); other.Unpatch(LookupHooks.TypesTarget, HarmonyPatchType.All, other.Id); }
    }

    private static void EnumerationPostfix(Type[] __result)
    {
        enumerationPostfixes++;
        if (changeEnumeration && __result.Length != 0) __result[0] = typeof(Guid);
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
        var dataModules = new OptimizationSet((_, error) => throw error);
        LookupHooks? hooks = null;
        try
        {
            if (diagnosticsFirst) recorder.Install();
            hooks = Hooks();
            Assembly game = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "Seapower-Scripts");
            dataModules.Install(new FileListingHooks(Owner + ".files", game), true);
            var encyclopedia = EncyclopediaLookupHooks.CreateForGame(Owner + ".encyclopedia", game, _ => { });
            if (encyclopedia != null) dataModules.Install(encyclopedia, true);
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
            dataModules.Stop();
            Assert(Harmony.GetPatchInfo(LookupHooks.Target)!.Owners.Contains("mebiustos.startupdiagnostics"), "Optimizer cleanup removed diagnostics.");
            recorder.Enter(); try { AccessTools.TypeByName("System.String"); } finally { recorder.Leave(); }
            Assert(recorder.Completed() == 3, "Diagnostics stopped after cache removal.");
            Console.WriteLine("PASS installed Startup Diagnostics: " + (diagnosticsFirst ? "diagnostics first" : "optimizer first") + "; all calls measured, diagnostics survives removal");
        }
        finally { hooks?.Uninstall(); dataModules.Stop(); recorder.Uninstall(); }
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
        internal void Install()
        {
            Method(hooks, "InstallLookup").Invoke(null, new[] { registry });
            Type? work = hooks.Assembly.GetType("StartupDiagnostics.LoadingWorkHooks");
            if (work == null) return;
            Method(work, "Configure").Invoke(null, new object[] { session, (Action<string, Action>)Guard });
            Assembly game = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "Seapower-Scripts");
            Method(work, "Install").Invoke(null, new[] { registry, game });
        }
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
