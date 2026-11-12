using System;
using System.Collections;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using SeaPower;

namespace HarmonyStartupOptimizer.EarlyStartup;

[BepInPlugin(Id, "Harmony Startup Optimizer Early Startup", "1.0.0")]
public sealed class EarlyStartupPlugin : BaseUnityPlugin
{
    private const string Id = "mebiustos.harmonystartupoptimizer.earlystartup";
    private static ManualLogSource? log;
    private static bool attempted;

    private void Awake()
    {
        log = Logger;
        try { InstallCompatibilityHook(new Harmony(Id)); }
        catch (Exception error) { Logger.LogError("Could not install the early startup hook: " + error); }
    }

    internal static MethodBase InstallCompatibilityHook(Harmony harmony)
    {
        var target = AccessTools.DeclaredMethod(typeof(FileManager), nameof(FileManager.CheckCompatibility), Type.EmptyTypes)
            ?? throw new MissingMethodException(typeof(FileManager).FullName, nameof(FileManager.CheckCompatibility));
        harmony.Patch(target, postfix: new HarmonyMethod(typeof(EarlyStartupPlugin), nameof(AfterCheck)));
        return target;
    }

    private static void AfterCheck(FileManager __instance, ref IEnumerator __result)
    {
        __result = RunAfterCompatibility(__result, () => StartOptimizer(__instance));
    }

    internal static IEnumerator RunAfterCompatibility(IEnumerator check, Action start)
    {
        try
        {
            while (check.MoveNext()) yield return check.Current;
        }
        finally { (check as IDisposable)?.Dispose(); }
        start();
    }

    private static void StartOptimizer(FileManager fileManager)
    {
        if (attempted) return;
        attempted = true;
        try
        {
            // Use the game's selected directory order, as Anchor Chain does when it scans packages.
            foreach (var directory in fileManager.Directories)
            {
                if (!directory.IsChecked) continue;
                string path = Path.Combine(directory.DirectoryInfo.FullName, "HarmonyStartupOptimizer.dll");
                if (!File.Exists(path)) continue;
                Assembly assembly = Assembly.LoadFile(path);
                Type startup = assembly.GetType("HarmonyStartupOptimizer.OptimizerStartup", throwOnError: true)!;
                MethodInfo entryPoint = startup.GetMethod("StartEarly", BindingFlags.Public | BindingFlags.Static,
                    null, Type.EmptyTypes, null) ?? throw new MissingMethodException(startup.FullName, "StartEarly");
                entryPoint.Invoke(null, null);
                return;
            }
        }
        catch (Exception error) { log!.LogError("Could not start Harmony Startup Optimizer early: " + error); }
    }
}
