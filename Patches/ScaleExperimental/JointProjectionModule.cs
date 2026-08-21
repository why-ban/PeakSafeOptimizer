using HarmonyLib;
using PeakSafeOptimizer.Config;
using PeakSafeOptimizer.Infrastructure;

namespace PeakSafeOptimizer.Patches.ScaleExperimental
{
    internal sealed class JointProjectionModule : IPatchModule
    {
        public void Register(PatchModuleContext context)
        {
            context.Register(PatchId.JointProjection, () => new PatchDefinition(
                PatchId.JointProjection,
                JointProjectionPatch.Initialize,
                JointProjectionPatch.ResolveAndValidateTarget,
                postfix: () => new HarmonyMethod(
                    typeof(JointProjectionPatch),
                    nameof(JointProjectionPatch.Postfix)),
                requireCurrentMvid: true,
                reset: JointProjectionPatch.Reset));
        }
    }
}
