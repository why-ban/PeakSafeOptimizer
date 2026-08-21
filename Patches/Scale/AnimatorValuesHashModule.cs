using HarmonyLib;
using PeakSafeOptimizer.Config;
using PeakSafeOptimizer.Infrastructure;

namespace PeakSafeOptimizer.Patches.Scale
{
    internal sealed class AnimatorValuesHashModule : IPatchModule
    {
        public void Register(PatchModuleContext context)
        {
            context.Register(PatchId.AnimatorValuesHash, () => new PatchDefinition(
                PatchId.AnimatorValuesHash,
                AnimatorValuesHashPatch.Initialize,
                AnimatorValuesHashPatch.ResolveAndValidateTarget,
                prefix: () => new HarmonyMethod(
                    typeof(AnimatorValuesHashPatch),
                    nameof(AnimatorValuesHashPatch.Prefix)),
                requireCurrentMvid: true,
                reset: AnimatorValuesHashPatch.Reset));
        }
    }
}
