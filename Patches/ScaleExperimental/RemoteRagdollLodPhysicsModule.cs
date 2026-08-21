using HarmonyLib;
using PeakSafeOptimizer.Config;
using PeakSafeOptimizer.Infrastructure;

namespace PeakSafeOptimizer.Patches.ScaleExperimental
{
    internal sealed class RemoteRagdollLodPhysicsModule : IPatchModule
    {
        public void Register(PatchModuleContext context)
        {
            context.Register(PatchId.RemoteRagdollLodPhysics, () => new PatchDefinition(
                PatchId.RemoteRagdollLodPhysics,
                RemoteRagdollLodPhysicsPatch.Initialize,
                RemoteRagdollLodPhysicsPatch.ResolveAndValidateTarget,
                prefix: () => new HarmonyMethod(
                    typeof(RemoteRagdollLodPhysicsPatch),
                    nameof(RemoteRagdollLodPhysicsPatch.Prefix)),
                requireCurrentMvid: true,
                reset: RemoteRagdollLodPhysicsPatch.Reset));
        }
    }
}
