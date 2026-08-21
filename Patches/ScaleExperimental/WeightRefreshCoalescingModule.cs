using HarmonyLib;
using PeakSafeOptimizer.Config;
using PeakSafeOptimizer.Infrastructure;

namespace PeakSafeOptimizer.Patches.ScaleExperimental
{
    internal sealed class WeightRefreshCoalescingModule : IPatchModule
    {
        public void Register(PatchModuleContext context)
        {
            context.Register(PatchId.WeightRefreshCoalescing, () => new PatchDefinition(
                PatchId.WeightRefreshCoalescing,
                WeightRefreshCoalescingPatch.Initialize,
                WeightRefreshCoalescingPatch.ResolveAndValidateTarget,
                prefix: () => new HarmonyMethod(
                    typeof(WeightRefreshCoalescingPatch),
                    nameof(WeightRefreshCoalescingPatch.Prefix)),
                requireCurrentMvid: true,
                reset: WeightRefreshCoalescingPatch.Reset));
        }
    }
}
