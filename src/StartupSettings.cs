using System;
using System.Collections.Generic;
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

    internal static StartupSettings Read(string path, Action<string> warning)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.Directory) != 0)
                throw new IOException("The startup settings path is a directory: " + path);
        }
        catch (FileNotFoundException) { return new StartupSettings(); }
        catch (DirectoryNotFoundException) { return new StartupSettings(); }

        var settings = new ConfigFile(path, false) { SaveOnConfigSet = false };
        var result = new StartupSettings(ReadBoolean(settings, path, "Overlay", "Enabled", true),
            ReadBoolean(settings, path, "Cache", "StopAfterStartup", false),
            ReadBoolean(settings, path, "Optimizations", "TypeLookup", true),
            ReadBoolean(settings, path, "Optimizations", "FileListing", true),
            ReadBoolean(settings, path, "Optimizations", "EncyclopediaLookup", true));
        WarnUnknownSettings(settings, path, warning);
        return result;
    }

    private static void WarnUnknownSettings(ConfigFile settings, string path, Action<string> warning)
    {
        var sections = new HashSet<string>(StringComparer.Ordinal);
        foreach (ConfigDefinition definition in settings.Keys) sections.Add(definition.Section);
        string section = "";
        bool knownSection = true;
        int lineNumber = 0;
        // Match ConfigFile's section/key syntax; use the bound settings as the accepted names.
        foreach (string raw in File.ReadLines(path))
        {
            lineNumber++;
            string line = raw.Trim();
            if (line.StartsWith("#", StringComparison.Ordinal)) continue;
            if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal))
            {
                section = line.Substring(1, line.Length - 2);
                knownSection = sections.Contains(section);
                if (!knownSection)
                    warning("Unknown configuration section [" + section + "] at " + path + ":" + lineNumber + "; entries in this section are ignored.");
                continue;
            }
            int separator = line.IndexOf('=');
            if (!knownSection || separator < 0) continue;
            string key = line.Substring(0, separator).Trim();
            if (!settings.ContainsKey(new ConfigDefinition(section, key)))
                warning("Unknown configuration key [" + section + "] " + key + " at " + path + ":" + lineNumber + "; this entry is ignored.");
        }
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
