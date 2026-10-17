using System;
using System.IO;
using BepInEx.Configuration;

namespace HarmonyStartupOptimizer;

internal static class OverlaySettings
{
    internal static bool ReadEnabled(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.Directory) != 0)
                throw new IOException("The overlay settings path is a directory: " + path);
        }
        catch (FileNotFoundException) { return true; }
        catch (DirectoryNotFoundException) { return true; }

        var settings = new ConfigFile(path, false) { SaveOnConfigSet = false };
        // Read as text so an invalid boolean is reported rather than replaced by a default.
        string value = settings.Bind("Overlay", "Enabled", "true", "").Value;
        if (!bool.TryParse(value, out bool enabled))
            throw new FormatException("Expected true or false for [Overlay] Enabled in " + path + ": " + value);
        return enabled;
    }
}
