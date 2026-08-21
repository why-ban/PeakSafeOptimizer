using HarmonyLib;
using PeakSafeOptimizer.Config;
using PeakSafeOptimizer.Infrastructure;

namespace PeakSafeOptimizer.Patches.ScaleExperimental
{
    internal sealed class RemoteRagdollLodMovementForceModule : IPatchModule
    {
        public void Register(PatchModuleContext context)
        {
            context.Register(PatchId.RemoteRagdollLodMovementForce, () => new PatchDefinition(
                PatchId.RemoteRagdollLodMovementForce,
                RemoteRagdollLodMovementForcePatch.Initialize,
                RemoteRagdollLodMovementForcePatch.ResolveAndValidateTarget,
                prefix: () => new HarmonyMethod(
                    typeof(RemoteRagdollLodMovementForcePatch),
                    nameof(RemoteRagdollLodMovementForcePatch.Prefix)),
                requireCurrentMvid: true,
                reset: RemoteRagdollLodMovementForcePatch.Reset));
        }
    }
}
