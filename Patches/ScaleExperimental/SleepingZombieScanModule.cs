using HarmonyLib;
using PeakSafeOptimizer.Config;
using PeakSafeOptimizer.Infrastructure;

namespace PeakSafeOptimizer.Patches.ScaleExperimental
{
    internal sealed class SleepingZombieScanModule : IPatchModule
    {
        public void Register(PatchModuleContext context)
        {
            context.Register(PatchId.SleepingZombieScan, () => new PatchDefinition(
                PatchId.SleepingZombieScan,
                SleepingZombieScanPatch.Initialize,
                SleepingZombieScanPatch.ResolveAndValidateTarget,
                prefix: () => new HarmonyMethod(
                    typeof(SleepingZombieScanPatch),
                    nameof(SleepingZombieScanPatch.Prefix)),
                requireCurrentMvid: true,
                reset: SleepingZombieScanPatch.Reset));
        }
    }
}
