using HarmonyLib;
using PeakSafeOptimizer.Config;
using PeakSafeOptimizer.Infrastructure;

namespace PeakSafeOptimizer.Patches.Scale
{
    internal sealed class GenericOptimizerRangeModule : IPatchModule
    {
        public void Register(PatchModuleContext context)
        {
            context.Register(PatchId.GenericOptimizerRange, () => new PatchDefinition(
                PatchId.GenericOptimizerRange,
                GenericOptimizerRangePatch.Initialize,
                GenericOptimizerRangePatch.ResolveAndValidateTarget,
                prefix: () => new HarmonyMethod(
                    typeof(GenericOptimizerRangePatch),
                    nameof(GenericOptimizerRangePatch.Prefix)),
                requireCurrentMvid: true,
                reset: GenericOptimizerRangePatch.Reset));
        }
    }
}
