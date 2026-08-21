using HarmonyLib;
using PeakSafeOptimizer.Config;
using PeakSafeOptimizer.Infrastructure;

namespace PeakSafeOptimizer.Patches.ScaleExperimental
{
    internal sealed class DetailBodypartThrottleModule : IPatchModule
    {
        public void Register(PatchModuleContext context)
        {
            context.Register(PatchId.DetailBodypartThrottle, () => new PatchDefinition(
                PatchId.DetailBodypartThrottle,
                DetailBodypartThrottlePatch.Initialize,
                DetailBodypartThrottlePatch.ResolveAndValidateTarget,
                prefix: () => new HarmonyMethod(
                    typeof(DetailBodypartThrottlePatch),
                    nameof(DetailBodypartThrottlePatch.Prefix)),
                requireCurrentMvid: true,
                reset: DetailBodypartThrottlePatch.Reset));
        }
    }
}
