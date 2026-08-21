using HarmonyLib;
using PeakSafeOptimizer.Config;
using PeakSafeOptimizer.Infrastructure;

namespace PeakSafeOptimizer.Patches.ScaleExperimental
{
    internal sealed class RemoteRagdollLodAnimateModule : IPatchModule
    {
        public void Register(PatchModuleContext context)
        {
            context.Register(PatchId.RemoteRagdollLodAnimate, () => new PatchDefinition(
                PatchId.RemoteRagdollLodAnimate,
                RemoteRagdollLodAnimatePatch.Initialize,
                RemoteRagdollLodAnimatePatch.ResolveAndValidateTarget,
                prefix: () => new HarmonyMethod(
                    typeof(RemoteRagdollLodAnimatePatch),
                    nameof(RemoteRagdollLodAnimatePatch.Prefix)),
                requireCurrentMvid: true,
                reset: RemoteRagdollLodAnimatePatch.Reset));
        }
    }
}
