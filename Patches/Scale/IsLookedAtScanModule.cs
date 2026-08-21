using HarmonyLib;
using PeakSafeOptimizer.Config;
using PeakSafeOptimizer.Infrastructure;

namespace PeakSafeOptimizer.Patches.Scale
{
    internal sealed class IsLookedAtScanModule : IPatchModule
    {
        public void Register(PatchModuleContext context)
        {
            context.Register(PatchId.IsLookedAtScan, () => new PatchDefinition(
                PatchId.IsLookedAtScan,
                IsLookedAtScanPatch.Initialize,
                IsLookedAtScanPatch.ResolveAndValidateTarget,
                prefix: () => new HarmonyMethod(
                    typeof(IsLookedAtScanPatch),
                    nameof(IsLookedAtScanPatch.Prefix)),
                requireCurrentMvid: true,
                reset: IsLookedAtScanPatch.Reset));
        }
    }
}
