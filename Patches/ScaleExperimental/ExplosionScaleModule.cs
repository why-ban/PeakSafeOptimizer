using HarmonyLib;
using PeakSafeOptimizer.Config;
using PeakSafeOptimizer.Infrastructure;

namespace PeakSafeOptimizer.Patches.ScaleExperimental
{
    internal sealed class ExplosionScaleModule : IPatchModule
    {
        public void Register(PatchModuleContext context)
        {
            context.Register(PatchId.ExplosionScale, () => new PatchDefinition(
                PatchId.ExplosionScale,
                ExplosionScalePatch.Initialize,
                ExplosionScalePatch.ResolveAndValidateTarget,
                prefix: () => new HarmonyMethod(
                    typeof(ExplosionScalePatch),
                    nameof(ExplosionScalePatch.Prefix)),
                requireCurrentMvid: true,
                reset: ExplosionScalePatch.Reset));
        }
    }
}
