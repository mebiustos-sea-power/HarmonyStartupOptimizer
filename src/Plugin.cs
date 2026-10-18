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
        LookupHooks? hooks = null;
        GameObject? host = null;
        try
        {
            var game = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "Seapower-Scripts");
            string package = Path.GetDirectoryName(typeof(Plugin).Assembly.Location)
                ?? throw new InvalidOperationException("The optimizer package directory is unavailable.");
            if (!ModActivation.IsEnabled(game.GetType("SeaPower.FileManager", true)!, package))
            { Log.LogInfo("Disabled in the mod manager."); return; }

            hooks = new LookupHooks(ModId, new TypeLookupCache());
            hooks.Install();
            host = new GameObject("HarmonyStartupOptimizer");
            UnityEngine.Object.DontDestroyOnLoad(host);
            host.hideFlags = HideFlags.HideAndDontSave;
            var optimizer = host.AddComponent<OptimizerHost>();
            optimizer.Initialize(hooks);
            Log.LogInfo("Active: shared TypeByName cache (Harmony " + LookupHooks.Target.DeclaringType!.Assembly.GetName().Version + ").");
            StartupMonitor.Attach(host, game, hooks.Cache, optimizer.StopAfterStartup);
        }
        catch (Exception error)
        {
            Log.LogError("Shared startup optimization initialization failed: " + error);
            if (hooks != null)
                try { hooks.Uninstall(); } catch (Exception cleanup) { Log.LogError("Shared cache rollback failed: " + cleanup); }
            if (host != null) UnityEngine.Object.Destroy(host);
        }
    }
}

internal sealed class OptimizerHost : MonoBehaviour
{
    private LookupHooks? hooks;
    private bool closed;

    internal void Initialize(LookupHooks adapter) { hooks = adapter; }

    private void OnApplicationQuit() => Close();
    private void OnDestroy() => Close();
    internal void StopAfterStartup() => Close(logCompletion: true);

    private void Close(bool logCompletion = false)
    {
        if (closed || hooks == null) return;
        closed = true;
        try
        {
            hooks.Uninstall();
            hooks = null;
            if (logCompletion) Plugin.Log.LogInfo("Startup-only cache stopped.");
        }
        catch (Exception error) { Plugin.Log.LogError("Shared type-cache shutdown failed: " + error); }
    }
}
