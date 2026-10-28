using System;
using System.IO;
using BepInEx.Configuration;

namespace HarmonyStartupOptimizer;

internal sealed class StartupSettings
{
    internal readonly bool OverlayEnabled, StopAfterStartup;
    internal readonly bool TypeLookup, FileListing, EncyclopediaLookup;

    private StartupSettings(bool overlayEnabled = true, bool stopAfterStartup = false,
        bool typeLookup = true, bool fileListing = true, bool encyclopediaLookup = true)
    {
        OverlayEnabled = overlayEnabled; StopAfterStartup = stopAfterStartup;
        TypeLookup = typeLookup; FileListing = fileListing; EncyclopediaLookup = encyclopediaLookup;
    }

    internal static StartupSettings Read(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.Directory) != 0)
                throw new IOException("The startup settings path is a directory: " + path);
        }
        catch (FileNotFoundException) { return new StartupSettings(); }
        catch (DirectoryNotFoundException) { return new StartupSettings(); }

        var settings = new ConfigFile(path, false) { SaveOnConfigSet = false };
        return new StartupSettings(ReadBoolean(settings, path, "Overlay", "Enabled", true),
            ReadBoolean(settings, path, "Cache", "StopAfterStartup", false),
            ReadBoolean(settings, path, "Optimizations", "TypeLookup", true),
            ReadBoolean(settings, path, "Optimizations", "FileListing", true),
            ReadBoolean(settings, path, "Optimizations", "EncyclopediaLookup", true));
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
