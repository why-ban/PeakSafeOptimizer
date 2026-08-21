using HarmonyLib;
using PeakSafeOptimizer.Config;
using PeakSafeOptimizer.Infrastructure;

namespace PeakSafeOptimizer.Patches.Scale
{
    internal sealed class BarAfflictionLayoutModule : IPatchModule
    {
        public void Register(PatchModuleContext context)
        {
            context.Register(PatchId.BarAfflictionLayout, () => new PatchDefinition(
                PatchId.BarAfflictionLayout,
                BarAfflictionLayoutPatch.Initialize,
                BarAfflictionLayoutPatch.ResolveAndValidateTarget,
                prefix: () => new HarmonyMethod(
                    typeof(BarAfflictionLayoutPatch),
                    nameof(BarAfflictionLayoutPatch.Prefix)),
                requireCurrentMvid: true,
                reset: BarAfflictionLayoutPatch.Reset));
        }
    }
}
