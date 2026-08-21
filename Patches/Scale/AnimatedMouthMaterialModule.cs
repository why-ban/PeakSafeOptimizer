using HarmonyLib;
using PeakSafeOptimizer.Config;
using PeakSafeOptimizer.Infrastructure;

namespace PeakSafeOptimizer.Patches.Scale
{
    internal sealed class AnimatedMouthMaterialModule : IPatchModule
    {
        public void Register(PatchModuleContext context)
        {
            context.Register(PatchId.AnimatedMouthMaterial, () => new PatchDefinition(
                PatchId.AnimatedMouthMaterial,
                AnimatedMouthMaterialPatch.Initialize,
                AnimatedMouthMaterialPatch.ResolveAndValidateTarget,
                prefix: () => new HarmonyMethod(
                    typeof(AnimatedMouthMaterialPatch),
                    nameof(AnimatedMouthMaterialPatch.Prefix)),
                requireCurrentMvid: true,
                reset: AnimatedMouthMaterialPatch.Reset));
            context.RegisterCleanup(AnimatedMouthMaterialPatch.Reset);
        }
    }
}
