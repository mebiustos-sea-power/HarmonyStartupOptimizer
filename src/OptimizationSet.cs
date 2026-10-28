using System;
using System.Collections.Generic;
using System.Linq;

namespace HarmonyStartupOptimizer;

internal interface IOptimizerModule
{
    string Name { get; }
    void Install();
    void Uninstall();
}

internal sealed class OptimizationSet
{
    private readonly Dictionary<IOptimizerModule, bool> installed = new Dictionary<IOptimizerModule, bool>();
    private readonly Action<string, Exception> failed;
    internal OptimizationSet(Action<string, Exception> failed) { this.failed = failed; }
    internal string[] ActiveNames => installed.Where(m => m.Value).Select(m => m.Key.Name).ToArray();

    internal bool Install(IOptimizerModule module, bool enabled)
    {
        if (!enabled) return false;
        installed.Add(module, false);
        try { module.Install(); installed[module] = true; }
        catch (Exception error)
        {
            try { module.Uninstall(); installed.Remove(module); }
            catch (Exception cleanup)
            {
                error = new AggregateException("Module installation and rollback failed.", error, cleanup);
            }
            failed(module.Name, error);
        }
        return installed.TryGetValue(module, out bool active) && active;
    }

    internal void Stop()
    {
        var errors = new List<Exception>();
        foreach (IOptimizerModule module in Enumerable.Reverse(installed.Keys).ToArray())
        {
            try { module.Uninstall(); installed.Remove(module); }
            catch (Exception error) { errors.Add(new InvalidOperationException(module.Name + " shutdown failed.", error)); }
        }
        if (errors.Count != 0) throw new AggregateException("Optimizer shutdown failed.", errors);
    }
}
