using System;
using System.Reflection;
using UnityEngine;

namespace HarmonyStartupOptimizer;

internal sealed class StartupMonitor : MonoBehaviour
{
    private StartupLifetime? lifetime;
    private StartupOverlay? overlay;

    internal static void Attach(GameObject host, Assembly game, TypeLookupCache? cache, StartupSettings settings,
        bool showLoadingStatus, Action shutdown)
    {
        StartupMonitor? monitor = null;
        try
        {
            if (!settings.OverlayEnabled && !settings.StopAfterStartup) return;
            var screen = new StartupScreen(game);
            var lifetime = new StartupLifetime(screen.MenuVisible, settings.OverlayEnabled, showLoadingStatus,
                settings.StopAfterStartup ? shutdown : null);
            monitor = host.AddComponent<StartupMonitor>();
            monitor.lifetime = lifetime;
            if (settings.OverlayEnabled)
                monitor.overlay = StartupOverlay.Attach(host, cache, lifetime, settings.StopAfterStartup);
        }
        catch (Exception error)
        {
            OptimizerStartup.Log.LogError("Startup completion monitoring initialization failed: " + error);
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
            OptimizerStartup.Log.LogError("Startup completion monitoring stopped: " + error);
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
