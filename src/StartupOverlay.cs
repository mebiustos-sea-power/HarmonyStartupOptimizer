using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace HarmonyStartupOptimizer;

internal sealed class StartupOverlay : MonoBehaviour
{
    private TypeLookupCache? cache;
    private StartupScreen? screen;
    private GUIStyle? label;
    private Font? font;
    private readonly GUIContent content = new GUIContent();
    private float nextSample, hideAt = -1;
    private bool menuVisible, stopped;

    internal static void Attach(GameObject host, Assembly game, TypeLookupCache cache)
    {
        StartupOverlay? overlay = null;
        try
        {
            string settingsPath = Path.Combine(BepInEx.Paths.ConfigPath, ModInfo.Id + ".cfg");
            if (!OverlaySettings.ReadEnabled(settingsPath)) return;
            var screen = new StartupScreen(game);
            overlay = host.AddComponent<StartupOverlay>();
            overlay.screen = screen;
            overlay.cache = cache;
        }
        catch (Exception error)
        {
            Plugin.Log.LogError("Startup status display initialization failed: " + error);
            if (overlay != null) overlay.Stop();
        }
    }

    private void Update()
    {
        if (stopped || cache == null) return;
        float now = Time.realtimeSinceStartup;
        if (hideAt >= 0 && now >= hideAt) { Stop(); return; }
        if (now < nextSample) return;
        nextSample = now + 0.25f;
        try
        {
            menuVisible = screen!.MenuVisible();
            content.text = ModInfo.Name + (menuVisible
                ? " | Cache hits: " + cache.Hits.ToString("N0", CultureInfo.InvariantCulture)
                : " | Active");
        }
        catch (Exception error) { Fail(error); }
    }

    private void OnGUI()
    {
        if (stopped || cache == null || Event.current.type != EventType.Repaint) return;
        Matrix4x4 previousMatrix = GUI.matrix;
        int previousDepth = GUI.depth;
        try
        {
            if (label == null)
            {
                font = Font.CreateDynamicFontFromOSFont(new[] { "Arial", "Yu Gothic UI" }, 16);
                if (font == null) throw new InvalidOperationException("A startup status font could not be created.");
                label = new GUIStyle(GUI.skin.label) { font = font, alignment = TextAnchor.MiddleCenter, wordWrap = false };
            }
            float scale = Mathf.Clamp(Screen.height / 1080f, 0.75f, 2f);
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(scale, scale, 1));
            GUI.depth = -10000;
            float width = Screen.width / scale - 32;
            label.fontSize = 16;
            Vector2 textSize = label.CalcSize(content);
            if (textSize.x > width)
            {
                label.fontSize = Mathf.Max(10, Mathf.FloorToInt(16 * width / textSize.x));
                textSize = label.CalcSize(content);
            }
            // Use the gap between the loading tip and progress bar on both screens.
            float centerY = Screen.height / scale * 0.875f;
            var rect = new Rect(16, centerY - textSize.y / 2, width, textSize.y);
            label.normal.textColor = Color.black;
            GUI.Label(new Rect(rect.x + 1, rect.y + 1, rect.width, rect.height), content, label);
            label.normal.textColor = Color.white;
            GUI.Label(rect, content, label);
            // Start after a rendered menu frame, rather than counting time spent
            // blocked in a startup operation before the screen can refresh.
            if (menuVisible && hideAt < 0) hideAt = Time.realtimeSinceStartup + 5;
        }
        catch (Exception error) { Fail(error); }
        finally { GUI.matrix = previousMatrix; GUI.depth = previousDepth; }
    }

    private void Fail(Exception error)
    {
        Plugin.Log.LogError("Startup status display stopped: " + error);
        Stop();
    }

    private void Stop()
    {
        stopped = true;
        enabled = false;
        // Only the display is removed; OptimizerHost keeps the cache installed.
        Destroy(this);
    }

    private void OnDestroy()
    {
        if (font != null) Destroy(font);
    }
}
