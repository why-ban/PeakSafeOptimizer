using HarmonyLib;
using PeakSafeOptimizer.Config;
using PeakSafeOptimizer.Infrastructure;

namespace PeakSafeOptimizer.Patches.Stability
{
    internal sealed class RopeSegmentGuardModule : IPatchModule
    {
        public void Register(PatchModuleContext context)
        {
            context.Register(PatchId.RopeSegmentGuard, () => new PatchDefinition(
                PatchId.RopeSegmentGuard,
                RopeSegmentGuardPatch.Initialize,
                RopeSegmentGuardPatch.ResolveAndValidateTarget,
                prefix: () => new HarmonyMethod(
                    typeof(RopeSegmentGuardPatch),
                    nameof(RopeSegmentGuardPatch.Prefix)),
                requireCurrentMvid: true,
                reset: RopeSegmentGuardPatch.Reset));
        }
    }
}
