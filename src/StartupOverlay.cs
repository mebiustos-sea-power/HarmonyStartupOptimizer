using System;
using System.Globalization;
using UnityEngine;

namespace HarmonyStartupOptimizer;

internal sealed class StartupOverlay : MonoBehaviour
{
    private TypeLookupCache? cache;
    private StartupLifetime? lifetime;
    private GUIStyle? label;
    private Font? font;
    private readonly GUIContent content = new GUIContent();
    private bool startupOnly, stopped;

    internal static StartupOverlay? Attach(GameObject host, TypeLookupCache cache, StartupLifetime lifetime, bool startupOnly)
    {
        StartupOverlay? overlay = null;
        try
        {
            overlay = host.AddComponent<StartupOverlay>();
            overlay.lifetime = lifetime;
            overlay.cache = cache;
            overlay.startupOnly = startupOnly;
            overlay.Refresh();
            return overlay;
        }
        catch (Exception error)
        {
            Plugin.Log.LogError("Startup status display initialization failed: " + error);
            if (overlay != null) overlay.Stop();
            return null;
        }
    }

    internal void Refresh()
    {
        if (stopped || cache == null || lifetime == null) return;
        content.text = ModInfo.Name + (lifetime.MenuDetected
            ? " | Cache hits: " + cache.Hits.ToString("N0", CultureInfo.InvariantCulture)
            : startupOnly ? " | Active (startup only)" : " | Active");
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
            // Start after rendering the hit count, rather than counting time spent
            // blocked in a startup operation before the screen can refresh.
            lifetime!.DisplayRendered(Time.realtimeSinceStartup);
        }
        catch (Exception error) { Fail(error); }
        finally { GUI.matrix = previousMatrix; GUI.depth = previousDepth; }
    }

    private void Fail(Exception error)
    {
        Plugin.Log.LogError("Startup status display stopped: " + error);
        Stop();
    }

    internal void Stop()
    {
        stopped = true;
        enabled = false;
        Destroy(this);
    }

    private void OnDestroy()
    {
        if (font != null) Destroy(font);
    }
}
