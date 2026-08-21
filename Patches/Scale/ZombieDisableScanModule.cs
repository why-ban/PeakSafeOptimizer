using HarmonyLib;
using PeakSafeOptimizer.Config;
using PeakSafeOptimizer.Infrastructure;

namespace PeakSafeOptimizer.Patches.Scale
{
    internal sealed class ZombieDisableScanModule : IPatchModule
    {
        public void Register(PatchModuleContext context)
        {
            context.Register(PatchId.ZombieScanRange, () => new PatchDefinition(
                PatchId.ZombieScanRange,
                ZombieDisableScanPatch.Initialize,
                ZombieDisableScanPatch.ResolveAndValidateTarget,
                prefix: () => new HarmonyMethod(
                    typeof(ZombieDisableScanPatch),
                    nameof(ZombieDisableScanPatch.Prefix)),
                requireCurrentMvid: true,
                reset: ZombieDisableScanPatch.Reset));
            context.RegisterCleanup(ZombieDisableScanPatch.Reset);
        }
    }
}
