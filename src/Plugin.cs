using AnchorChain;

namespace HarmonyStartupOptimizer;

[ACPlugin(ModId, ModInfo.Name, ModInfo.Version)]
public sealed class Plugin : IAnchorChainMod
{
    public const string ModId = ModInfo.Id;

    public void TriggerEntryPoint() => OptimizerStartup.Initialize(showLoadingStatus: false);
}
