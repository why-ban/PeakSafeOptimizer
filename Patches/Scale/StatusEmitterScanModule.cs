using HarmonyLib;
using PeakSafeOptimizer.Config;
using PeakSafeOptimizer.Infrastructure;

namespace PeakSafeOptimizer.Patches.Scale
{
    internal sealed class StatusEmitterScanModule : IPatchModule
    {
        public void Register(PatchModuleContext context)
        {
            context.Register(PatchId.StatusEmitterScan, () => new PatchDefinition(
                PatchId.StatusEmitterScan,
                StatusEmitterScanPatch.Initialize,
                StatusEmitterScanPatch.ResolveAndValidateTarget,
                prefix: () => new HarmonyMethod(
                    typeof(StatusEmitterScanPatch),
                    nameof(StatusEmitterScanPatch.Prefix)),
                requireCurrentMvid: true,
                reset: StatusEmitterScanPatch.Reset));
        }
    }
}
