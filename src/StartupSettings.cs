using System;
using System.IO;
using BepInEx.Configuration;

namespace HarmonyStartupOptimizer;

internal sealed class StartupSettings
{
    internal readonly bool OverlayEnabled, StopAfterStartup;

    private StartupSettings(bool overlayEnabled, bool stopAfterStartup)
    { OverlayEnabled = overlayEnabled; StopAfterStartup = stopAfterStartup; }

    internal static StartupSettings Read(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.Directory) != 0)
                throw new IOException("The startup settings path is a directory: " + path);
        }
        catch (FileNotFoundException) { return new StartupSettings(true, false); }
        catch (DirectoryNotFoundException) { return new StartupSettings(true, false); }

        var settings = new ConfigFile(path, false) { SaveOnConfigSet = false };
        return new StartupSettings(ReadBoolean(settings, path, "Overlay", "Enabled", true),
            ReadBoolean(settings, path, "Cache", "StopAfterStartup", false));
    }

    private static bool ReadBoolean(ConfigFile settings, string path, string section, string key, bool defaultValue)
    {
        // Read as text so an invalid boolean is reported rather than replaced by a default.
        string value = settings.Bind(section, key, defaultValue ? "true" : "false", "").Value;
        if (!bool.TryParse(value, out bool result))
            throw new FormatException("Expected true or false for [" + section + "] " + key + " in " + path + ": " + value);
        return result;
    }
}
