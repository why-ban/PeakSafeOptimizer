using HarmonyLib;
using PeakSafeOptimizer.Config;
using PeakSafeOptimizer.Infrastructure;

namespace PeakSafeOptimizer.Patches.Scale
{
    internal sealed class LightVolumeSampleCacheModule : IPatchModule
    {
        public void Register(PatchModuleContext context)
        {
            context.Register(PatchId.LightVolumeSampleCache, () => new PatchDefinition(
                PatchId.LightVolumeSampleCache,
                LightVolumeSampleCachePatch.Initialize,
                LightVolumeSampleCachePatch.ResolveAndValidateTarget,
                prefix: () => new HarmonyMethod(
                    typeof(LightVolumeSampleCachePatch),
                    nameof(LightVolumeSampleCachePatch.Prefix)),
                postfix: () => new HarmonyMethod(
                    typeof(LightVolumeSampleCachePatch),
                    nameof(LightVolumeSampleCachePatch.Postfix)),
                requireCurrentMvid: true,
                reset: LightVolumeSampleCachePatch.Reset));
        }
    }
}
