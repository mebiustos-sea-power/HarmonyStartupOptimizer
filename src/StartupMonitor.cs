using System;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace HarmonyStartupOptimizer;

internal sealed class StartupMonitor : MonoBehaviour
{
    private StartupLifetime? lifetime;
    private StartupOverlay? overlay;

    internal static void Attach(GameObject host, Assembly game, TypeLookupCache cache, Action shutdown)
    {
        StartupMonitor? monitor = null;
        try
        {
            string path = Path.Combine(BepInEx.Paths.ConfigPath, ModInfo.Id + ".cfg");
            StartupSettings settings = StartupSettings.Read(path);
            if (!settings.OverlayEnabled && !settings.StopAfterStartup) return;
            var screen = new StartupScreen(game);
            var lifetime = new StartupLifetime(screen.MenuVisible, settings.OverlayEnabled,
                settings.StopAfterStartup ? shutdown : null);
            monitor = host.AddComponent<StartupMonitor>();
            monitor.lifetime = lifetime;
            if (settings.OverlayEnabled)
                monitor.overlay = StartupOverlay.Attach(host, cache, lifetime, settings.StopAfterStartup);
        }
        catch (Exception error)
        {
            Plugin.Log.LogError("Startup completion monitoring initialization failed: " + error);
            if (monitor != null) monitor.Stop();
        }
    }

    private void Update()
    {
        if (lifetime == null) return;
        try
        {
            float now = Time.realtimeSinceStartup;
            if (overlay == null) lifetime.DisplayUnavailable(now);
            if (lifetime.Tick(now) && overlay != null) overlay.Refresh();
            if (lifetime.Finished) Stop();
        }
        catch (Exception error)
        {
            Plugin.Log.LogError("Startup completion monitoring stopped: " + error);
            Stop();
        }
    }

    private void Stop()
    {
        if (overlay != null) overlay.Stop();
        enabled = false;
        Destroy(this);
    }
}
