using HarmonyLib;
using PeakSafeOptimizer.Config;
using PeakSafeOptimizer.Infrastructure;

namespace PeakSafeOptimizer.Patches.Scale
{
    internal sealed class HeatEmissionScanModule : IPatchModule
    {
        public void Register(PatchModuleContext context)
        {
            context.Register(PatchId.HeatEmissionScan, () => new PatchDefinition(
                PatchId.HeatEmissionScan,
                HeatEmissionScanPatch.Initialize,
                HeatEmissionScanPatch.ResolveAndValidateTarget,
                prefix: () => new HarmonyMethod(
                    typeof(HeatEmissionScanPatch),
                    nameof(HeatEmissionScanPatch.Prefix)),
                requireCurrentMvid: true,
                reset: HeatEmissionScanPatch.Reset));
        }
    }
}
