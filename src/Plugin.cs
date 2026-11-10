using System;
using System.IO;
using System.Linq;
using AnchorChain;
using BepInEx.Logging;
using UnityEngine;

namespace HarmonyStartupOptimizer;

[ACPlugin(ModId, ModInfo.Name, ModInfo.Version)]
public sealed class Plugin : IAnchorChainMod
{
    public const string ModId = ModInfo.Id;
    internal static readonly ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource(ModInfo.Name);
    private static bool initialized;

    public void TriggerEntryPoint()
    {
        if (initialized) return;
        initialized = true;
        OptimizationSet? modules = null;
        GameObject? host = null;
        try
        {
            var game = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "Seapower-Scripts");
            string package = Path.GetDirectoryName(typeof(Plugin).Assembly.Location)
                ?? throw new InvalidOperationException("The optimizer package directory is unavailable.");
            if (!ModActivation.IsEnabled(game.GetType("SeaPower.FileManager", true)!, package))
            { Log.LogInfo("Disabled in the mod manager."); return; }

            StartupSettings settings = StartupSettings.Read(Path.Combine(BepInEx.Paths.ConfigPath, ModId + ".cfg"), message => Log.LogWarning(message));
            modules = new OptimizationSet((name, error) => Log.LogError(name + " initialization failed: " + error));
            TypeLookupCache? cache = settings.TypeLookup ? new TypeLookupCache() : null;
            if (cache != null && !modules.Install(new LookupHooks(ModId, cache), true)) cache = null;
            if (settings.FileListing) modules.Install(new FileListingHooks(ModId + ".files", game), true);
            if (settings.EncyclopediaLookup)
            {
                var encyclopedia = EncyclopediaLookupHooks.CreateForGame(ModId + ".encyclopedia", game, Log.LogWarning);
                if (encyclopedia != null) modules.Install(encyclopedia, true);
            }
            if (modules.ActiveNames.Length == 0)
            { modules.Stop(); Log.LogInfo("No optimizations are active."); return; }
            host = new GameObject("HarmonyStartupOptimizer");
            UnityEngine.Object.DontDestroyOnLoad(host);
            host.hideFlags = HideFlags.HideAndDontSave;
            var optimizer = host.AddComponent<OptimizerHost>();
            optimizer.Initialize(modules);
            Log.LogInfo("Active: " + string.Join(", ", modules.ActiveNames) + " (Harmony " + LookupHooks.Target.DeclaringType!.Assembly.GetName().Version + ").");
            StartupMonitor.Attach(host, game, cache, settings, optimizer.StopAfterStartup);
        }
        catch (Exception error)
        {
            Log.LogError("Shared startup optimization initialization failed: " + error);
            if (modules != null)
                try { modules.Stop(); } catch (Exception cleanup) { Log.LogError("Startup optimization rollback failed: " + cleanup); }
            if (host != null) UnityEngine.Object.Destroy(host);
        }
    }
}

internal sealed class OptimizerHost : MonoBehaviour
{
    private OptimizationSet? modules;
    private bool closed;

    internal void Initialize(OptimizationSet adapters) { modules = adapters; }

    private void OnApplicationQuit() => Close();
    private void OnDestroy() => Close();
    internal void StopAfterStartup() => Close(logCompletion: true);

    private void Close(bool logCompletion = false)
    {
        if (closed || modules == null) return;
        try
        {
            modules.Stop();
            modules = null;
            closed = true;
            if (logCompletion) Plugin.Log.LogInfo("Startup-only optimizations stopped.");
        }
        catch (Exception error) { Plugin.Log.LogError("Startup optimization shutdown failed: " + error); }
    }
}
