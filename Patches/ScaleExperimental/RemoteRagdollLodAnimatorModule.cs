using HarmonyLib;
using PeakSafeOptimizer.Config;
using PeakSafeOptimizer.Infrastructure;

namespace PeakSafeOptimizer.Patches.ScaleExperimental
{
    internal sealed class RemoteRagdollLodAnimatorModule : IPatchModule
    {
        public void Register(PatchModuleContext context)
        {
            context.Register(PatchId.RemoteRagdollLodAnimator, () => new PatchDefinition(
                PatchId.RemoteRagdollLodAnimator,
                RemoteRagdollLodAnimatorPatch.Initialize,
                RemoteRagdollLodAnimatorPatch.ResolveAndValidateTarget,
                prefix: () => new HarmonyMethod(
                    typeof(RemoteRagdollLodAnimatorPatch),
                    nameof(RemoteRagdollLodAnimatorPatch.Prefix)),
                requireCurrentMvid: true,
                reset: RemoteRagdollLodAnimatorPatch.Reset));
        }
    }
}
