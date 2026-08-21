using HarmonyLib;
using PeakSafeOptimizer.Config;
using PeakSafeOptimizer.Infrastructure;

namespace PeakSafeOptimizer.Patches.Scale
{
    internal sealed class ZombieSpawnScanModule : IPatchModule
    {
        public void Register(PatchModuleContext context)
        {
            context.Register(PatchId.ZombieSpawnScanRange, () => new PatchDefinition(
                PatchId.ZombieSpawnScanRange,
                ZombieSpawnScanPatch.Initialize,
                ZombieSpawnScanPatch.ResolveAndValidateTarget,
                prefix: () => new HarmonyMethod(
                    typeof(ZombieSpawnScanPatch),
                    nameof(ZombieSpawnScanPatch.Prefix)),
                requireCurrentMvid: true,
                reset: ZombieSpawnScanPatch.Reset));
        }
    }
}
