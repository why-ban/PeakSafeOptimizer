using HarmonyLib;
using PeakSafeOptimizer.Config;
using PeakSafeOptimizer.Infrastructure;

namespace PeakSafeOptimizer.Patches.ScaleExperimental
{
    internal sealed class EyeLookComponentCacheModule : IPatchModule
    {
        public void Register(PatchModuleContext context)
        {
            context.Register(PatchId.EyeLookComponentCache, () => new PatchDefinition(
                PatchId.EyeLookComponentCache,
                EyeLookComponentCachePatch.Initialize,
                EyeLookComponentCachePatch.ResolveAndValidateTarget,
                prefix: () => new HarmonyMethod(
                    typeof(EyeLookComponentCachePatch),
                    nameof(EyeLookComponentCachePatch.Prefix)),
                requireCurrentMvid: true,
                reset: EyeLookComponentCachePatch.Reset));
        }
    }
}
