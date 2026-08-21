using HarmonyLib;
using PeakSafeOptimizer.Config;
using PeakSafeOptimizer.Infrastructure;

namespace PeakSafeOptimizer.Patches.Scale
{
    internal sealed class CampfireProtectionMergeModule : IPatchModule
    {
        public void Register(PatchModuleContext context)
        {
            context.Register(PatchId.CampfireProtectionMerge, () => new PatchDefinition(
                PatchId.CampfireProtectionMerge,
                CampfireProtectionMergePatch.Initialize,
                CampfireProtectionMergePatch.ResolveAndValidateTarget,
                prefix: () => new HarmonyMethod(
                    typeof(CampfireProtectionMergePatch),
                    nameof(CampfireProtectionMergePatch.Prefix)),
                requireCurrentMvid: true,
                reset: CampfireProtectionMergePatch.Reset));
        }
    }
}
