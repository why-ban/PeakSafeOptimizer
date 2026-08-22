using HarmonyLib;
using PeakSafeOptimizer.Config;
using PeakSafeOptimizer.Infrastructure;

namespace PeakSafeOptimizer.Patches.ScaleExperimental
{
    internal sealed class TumbleWeedTargetScanModule : IPatchModule
    {
        public void Register(PatchModuleContext context)
        {
            context.Register(PatchId.TumbleWeedTargetScan, () => new PatchDefinition(
                PatchId.TumbleWeedTargetScan,
                TumbleWeedTargetScanPatch.Initialize,
                TumbleWeedTargetScanPatch.ResolveAndValidateTarget,
                prefix: () => new HarmonyMethod(
                    typeof(TumbleWeedTargetScanPatch),
                    nameof(TumbleWeedTargetScanPatch.Prefix)),
                requireCurrentMvid: true,
                reset: TumbleWeedTargetScanPatch.Reset));
        }
    }
}
