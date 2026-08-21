using HarmonyLib;
using PeakSafeOptimizer.Config;
using PeakSafeOptimizer.Infrastructure;

namespace PeakSafeOptimizer.Patches.Equivalent.MathAndScan
{
    internal sealed class MathAndScanPatchModule : IPatchModule
    {
        public void Register(PatchModuleContext context)
        {
            context.Register(PatchId.Campfire, () => new PatchDefinition(
                PatchId.Campfire,
                CampfirePatch.Initialize,
                CampfirePatch.ResolveAndValidateTarget,
                prefix: () => new HarmonyMethod(
                    typeof(CampfirePatch),
                    nameof(CampfirePatch.Prefix)),
                requireCurrentMvid: true,
                reset: CampfirePatch.Reset));
        }
    }
}
