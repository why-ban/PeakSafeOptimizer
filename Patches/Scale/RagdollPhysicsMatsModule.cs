using HarmonyLib;
using PeakSafeOptimizer.Config;
using PeakSafeOptimizer.Infrastructure;

namespace PeakSafeOptimizer.Patches.Scale
{
    internal sealed class RagdollPhysicsMatsModule : IPatchModule
    {
        public void Register(PatchModuleContext context)
        {
            context.Register(PatchId.RagdollPhysicsMats, () => new PatchDefinition(
                PatchId.RagdollPhysicsMats,
                RagdollPhysicsMatsPatch.Initialize,
                RagdollPhysicsMatsPatch.ResolveAndValidateTarget,
                prefix: () => new HarmonyMethod(
                    typeof(RagdollPhysicsMatsPatch),
                    nameof(RagdollPhysicsMatsPatch.Prefix)),
                postfix: () => new HarmonyMethod(
                    typeof(RagdollPhysicsMatsPatch),
                    nameof(RagdollPhysicsMatsPatch.Postfix)),
                requireCurrentMvid: true,
                reset: RagdollPhysicsMatsPatch.Reset));
        }
    }
}
