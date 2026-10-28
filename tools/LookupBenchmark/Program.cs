using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;

internal static class Program
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static int Main(string[] args)
    {
        object? hooks = null;
        MethodInfo? uninstall = null;
        try
        {
            if (args.Length != 2) throw new ArgumentException("Expected optimizer DLL and lookup manifest paths.");
            Assembly optimizer = Assembly.LoadFrom(args[0]);
            var names = new List<string>();
            foreach (string line in File.ReadAllLines(args[1]))
            {
                string[] item = line.Split(new[] { '\t' }, 2);
                if (item.Length != 2) throw new FormatException("Invalid lookup manifest line.");
                if (item[0] == "assembly")
                {
                    string fullName = AssemblyName.GetAssemblyName(item[1]).FullName;
                    if (!AppDomain.CurrentDomain.GetAssemblies().Any(a => a.FullName == fullName)) Assembly.LoadFrom(item[1]);
                }
                else if (item[0] == "lookup") names.Add(item[1]);
                else throw new FormatException("Unknown lookup manifest record: " + item[0]);
            }
            Type cacheType = optimizer.GetType("HarmonyStartupOptimizer.TypeLookupCache", true)!;
            object cache = Activator.CreateInstance(cacheType, true)!;
            Type hooksType = optimizer.GetType("HarmonyStartupOptimizer.LookupHooks", true)!;
            hooks = Activator.CreateInstance(hooksType, Instance, null,
                new[] { (object)"mebiustos.harmonystartupoptimizer.lookupbenchmark", cache }, CultureInfo.InvariantCulture)!;
            uninstall = hooksType.GetMethod("Uninstall", Instance)!;
            var timer = Stopwatch.StartNew();
            hooksType.GetMethod("Install", Instance)!.Invoke(hooks, null);
            double installMs = timer.Elapsed.TotalMilliseconds;
            timer.Restart();
            int found = 0;
            ulong fingerprint = 14695981039346656037UL;
            foreach (string name in names)
            {
                Type? type = AccessTools.TypeByName(name);
                if (type != null) found++;
                string identity = type?.AssemblyQualifiedName ?? "<missing>";
                foreach (char c in identity) fingerprint = unchecked((fingerprint ^ c) * 1099511628211UL);
            }
            double lookupMs = timer.Elapsed.TotalMilliseconds;
            long hits = (long)cacheType.GetProperty("Hits", Instance)!.GetValue(cache)!;
            Console.WriteLine("BENCHMARK calls=" + names.Count + " found=" + found + " cache_hits=" + hits +
                " install_ms=" + installMs.ToString("F3", CultureInfo.InvariantCulture) +
                " lookup_ms=" + lookupMs.ToString("F3", CultureInfo.InvariantCulture) + " fingerprint=" + fingerprint);
            Console.WriteLine("Benchmark only: assemblies and type metadata; no game entry point or Unity scene was started.");
            return 0;
        }
        catch (Exception error) { Console.WriteLine(error); return 1; }
        finally { if (hooks != null && uninstall != null) uninstall.Invoke(hooks, null); }
    }
}
