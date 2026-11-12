using System;
using System.Collections;
using System.Linq;
using HarmonyLib;
using HarmonyStartupOptimizer.EarlyStartup;

internal static class EarlyStartupChecks
{
    internal static void Run()
    {
        var harmony = new Harmony("mebiustos.harmonystartupoptimizer.smoke.earlystartup");
        var target = EarlyStartupPlugin.InstallCompatibilityHook(harmony);
        try
        {
            Check(Harmony.GetPatchInfo(target)!.Owners.Contains(harmony.Id), "The native compatibility hook did not install.");
        }
        finally { harmony.Unpatch(target, HarmonyPatchType.All, harmony.Id); }

        object wait = new object();
        int starts = 0, disposals = 0;
        var expectedFailure = new InvalidOperationException("compatibility check fixture");
        IEnumerator CompatibilityCheck(bool fail)
        {
            try
            {
                yield return wait;
                if (fail) throw expectedFailure;
            }
            finally { disposals++; }
        }

        var normal = EarlyStartupPlugin.RunAfterCompatibility(CompatibilityCheck(false), () =>
        {
            Check(disposals == 1, "The optimizer started before the compatibility check completed.");
            starts++;
        });
        Check(normal.MoveNext() && ReferenceEquals(normal.Current, wait) && starts == 0,
            "Early startup changed the game's yield or started before compatibility completed.");
        Check(!normal.MoveNext() && !normal.MoveNext() && starts == 1, "A successful check must start the optimizer once.");

        var canceled = EarlyStartupPlugin.RunAfterCompatibility(CompatibilityCheck(false), () => starts++);
        Check(canceled.MoveNext(), "The cancellation fixture did not reach its yield.");
        ((IDisposable)canceled).Dispose();
        Check(starts == 1 && disposals == 2, "Canceling compatibility must dispose it without starting the optimizer.");

        var failed = EarlyStartupPlugin.RunAfterCompatibility(CompatibilityCheck(true), () => starts++);
        Check(failed.MoveNext(), "The failure fixture did not reach its yield.");
        try
        {
            failed.MoveNext();
            throw new InvalidOperationException("Early startup swallowed the compatibility check failure.");
        }
        catch (InvalidOperationException error) when (ReferenceEquals(error, expectedFailure)) { }
        Check(starts == 1 && disposals == 3, "A failed compatibility check must not start the optimizer.");
        Console.WriteLine("PASS early startup: native compatibility hook, preserved yields, and startup only after successful completion");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
