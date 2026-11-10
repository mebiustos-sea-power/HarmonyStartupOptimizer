using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using HarmonyStartupOptimizer;
using SeaPower;

internal static class DataOptimizationChecks
{
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly InvalidOperationException Expected = new InvalidOperationException("file fixture");
    private static bool skip;
    private static int postfixes;

    internal static void Run(Assembly game)
    {
        FileLists(game);
        Encyclopedia(game);
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

    private static void FileLists(Assembly game)
    {
        MethodInfo target = typeof(DataOptimizationChecks).GetMethod(nameof(Files), Static)!;
        var module = new FileListingHooks("mebiustos.harmonystartupoptimizer.smoke.files", target);
        var other = new Harmony("mebiustos.harmonystartupoptimizer.smoke.files.other");
        var modules = new OptimizationSet((_, error) => throw error);
        try
        {
            modules.Install(module, true);
            other.Patch(target, prefix: new HarmonyMethod(typeof(DataOptimizationChecks), nameof(Prefix)),
                postfix: new HarmonyMethod(typeof(DataOptimizationChecks), nameof(Postfix)));
            postfixes = 0;
            Check(Files("", "*").SequenceEqual(new[] { "a", "b", "A", "postfix" }), "File optimization changed case, priority or another postfix.");
            skip = true;
            Check(Files("", "*").SequenceEqual(new[] { "skipped", "postfix" }), "File optimization replaced another prefix's result.");
            skip = false;
            try { Files("", "throw"); throw new Exception("File error swallowed."); }
            catch (InvalidOperationException error) { Check(ReferenceEquals(error, Expected), "File error identity changed."); }
            Check(Files("", "*").SequenceEqual(new[] { "a", "b", "A", "postfix" }), "File failure leaked per-call membership data.");
            modules.Stop();
            Check(Harmony.GetPatchInfo(target)!.Owners.Contains(other.Id), "File cleanup removed another owner's patch.");
            var native = new FileListingHooks("mebiustos.harmonystartupoptimizer.smoke.files.native", game);
            try { native.Install(); } finally { native.Uninstall(); }
            Console.WriteLine("PASS file listings: native IL, stable order/case, other prefixes/postfixes, exception identity and common shutdown");
        }
        finally { skip = false; modules.Stop(); other.Unpatch(target, HarmonyPatchType.All, other.Id); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static List<string> Files(string path, string pattern)
    {
        if (pattern == "throw") throw Expected;
        var result = new List<string>();
        foreach (string file in new[] { "a", "b", "a", "A", "b" }) if (!result.Contains(file)) result.Add(file);
        return result;
    }

    private static bool Prefix(ref List<string>? __result)
    { if (!skip) return true; __result = new List<string> { "skipped" }; return false; }
    private static void Postfix(List<string> __result) { postfixes++; __result.Add("postfix"); }

    private static void Encyclopedia(Assembly game)
    {
        const string owner = "mebiustos.harmonystartupoptimizer.smoke.encyclopedia";
        var warnings = new List<string>();
        var unsupported = EncyclopediaLookupHooks.CreateForGame(owner, typeof(DataOptimizationChecks).Assembly, warnings.Add);
        Check(unsupported == null && warnings.Count == 1, "An unsupported game build should skip the encyclopedia optimization with one warning.");

        var module = EncyclopediaLookupHooks.CreateForGame(owner, game, warning => throw new Exception(warning))
            ?? throw new Exception("The verified game build skipped the encyclopedia optimization.");
        Type type = typeof(EncyclopediaLookupHooks);
        object iterator = new object();
        MethodInfo enter = type.GetMethod("Prefix", Static)!;
        MethodInfo leave = type.GetMethod("Finalizer", Static)!;
        MethodInfo narrow = type.GetMethod("Narrow", Static)!.MakeGenericMethod(typeof(MuseumSensorEntry));
        object?[] frame = { iterator, null };
        try
        {
            module.Install();
            Type manager = game.GetType("SeaPower.EncyclopedicDataManager", true)!;
            MethodInfo predicateMethod = manager.GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public)
                .SelectMany(t => t.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly))
                .Single(m => m.Name == "<LoadUnitsFromMetadata>b__13");
            object closure = Activator.CreateInstance(predicateMethod.DeclaringType!, true)!;
            FieldInfo wanted = predicateMethod.DeclaringType!.GetField("sensorRef", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!;
            wanted.SetValue(closure, "a");
            var predicate = (Func<MuseumSensorEntry, bool>)predicateMethod.CreateDelegate(typeof(Func<MuseumSensorEntry, bool>), closure);
            var first = new MuseumSensorEntry { IniRef = "a" }; var second = new MuseumSensorEntry { IniRef = "a" };
            var values = new ObservableCollection<MuseumSensorEntry> { first, second };
            enter.Invoke(null, frame);
            MuseumSensorEntry? Find()
            {
                var candidates = (IEnumerable<MuseumSensorEntry>)narrow.Invoke(null, new object[] { values, predicate })!;
                Check(candidates.Count() <= 1, "Known native lookup was not narrowed to a single candidate.");
                return candidates.FirstOrDefault(predicate);
            }
            Check(ReferenceEquals(Find(), first), "The native predicate selected a different duplicate.");
            // Invoke the patched setter itself; this test method was JIT-compiled
            // before installation and its direct auto-property writes may be inlined.
            MethodInfo keySetter = typeof(MuseumEntry).GetProperty(nameof(MuseumEntry.IniRef))!.SetMethod!;
            keySetter.Invoke(first, new object[] { "other" });
            Check(ReferenceEquals(Find(), second), "Native key setters did not invalidate the index.");
            values.Move(1, 0); keySetter.Invoke(first, new object[] { "a" });
            Check(ReferenceEquals(Find(), second), "Reordering changed native first-match priority.");
            wanted.SetValue(closure, "missing"); Check(Find() == null, "Native missing reference changed.");
            var added = new MuseumSensorEntry { IniRef = "missing" }; values.Add(added);
            Check(ReferenceEquals(Find(), added), "A newly appended native item remained missing.");
            leave.Invoke(null, new object?[] { iterator, false, null, frame[1] });
            frame[1] = null;
            module.Uninstall();
            MethodInfo setter = typeof(MuseumEntry).GetProperty(nameof(MuseumEntry.IniRef))!.SetMethod!;
            Check(Harmony.GetPatchInfo(setter)?.Owners.Contains(owner) != true, "Native model hooks remained after shutdown.");
            Console.WriteLine("PASS encyclopedia: native iterator IL/predicate, duplicate priority, live key changes, append/reorder and cleanup");
        }
        finally
        {
            if (frame[1] != null) leave.Invoke(null, new object?[] { iterator, false, null, frame[1] });
            module.Uninstall();
        }
    }
}
