using System;
using System.Collections;
using System.IO;
using System.Reflection;

namespace HarmonyStartupOptimizer;

internal static class ModActivation
{
    internal static bool IsEnabled(Type fileManagerType, string packagePath)
    {
        var instance = Required(fileManagerType, "Instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
        object manager = instance.GetValue(null) ?? throw new InvalidOperationException("Sea Power's file manager is not ready.");
        var entries = Required(fileManagerType, "Directories", BindingFlags.Public | BindingFlags.Instance).GetValue(manager) as IEnumerable
            ?? throw new InvalidOperationException("Sea Power's mod directories are unavailable.");
        foreach (object entry in entries)
        {
            if (entry == null) throw new InvalidOperationException("An empty mod-directory entry was registered.");
            Type type = entry.GetType();
            var directory = Required(type, "DirectoryInfo", BindingFlags.Public | BindingFlags.Instance).GetValue(entry) as DirectoryInfo
                ?? throw new InvalidOperationException("A mod-directory entry has no directory information.");
            if (!string.Equals(Normalize(directory.FullName), Normalize(packagePath), StringComparison.OrdinalIgnoreCase)) continue;
            var checkedProperty = Required(type, "IsChecked", BindingFlags.Public | BindingFlags.Instance);
            if (checkedProperty.PropertyType != typeof(bool)) throw new InvalidOperationException("IsChecked is not a Boolean.");
            return (bool)checkedProperty.GetValue(entry)!;
        }
        throw new InvalidOperationException("The mod folder is not registered: " + packagePath);
    }

    private static PropertyInfo Required(Type type, string name, BindingFlags flags) =>
        type.GetProperty(name, flags) ?? throw new MissingMemberException(type.FullName, name);
    private static string Normalize(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
