using HarmonyLib;
using PeakSafeOptimizer.Config;
using PeakSafeOptimizer.Infrastructure;

namespace PeakSafeOptimizer.Patches.Scale
{
    internal sealed class BodypartMovementForceIdentityModule : IPatchModule
    {
        public void Register(PatchModuleContext context)
        {
            context.Register(PatchId.BodypartMovementForceIdentity, () => new PatchDefinition(
                PatchId.BodypartMovementForceIdentity,
                BodypartMovementForceIdentityPatch.Initialize,
                BodypartMovementForceIdentityPatch.ResolveAndValidateTarget,
                prefix: () => new HarmonyMethod(
                    typeof(BodypartMovementForceIdentityPatch),
                    nameof(BodypartMovementForceIdentityPatch.Prefix)),
                requireCurrentMvid: true,
                reset: BodypartMovementForceIdentityPatch.Reset));
        }
    }
}
