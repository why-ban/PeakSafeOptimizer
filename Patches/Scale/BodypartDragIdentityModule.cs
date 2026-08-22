using HarmonyLib;
using PeakSafeOptimizer.Config;
using PeakSafeOptimizer.Infrastructure;

namespace PeakSafeOptimizer.Patches.Scale
{
    internal sealed class BodypartDragIdentityModule : IPatchModule
    {
        public void Register(PatchModuleContext context)
        {
            context.Register(PatchId.BodypartDragIdentity, () => new PatchDefinition(
                PatchId.BodypartDragIdentity,
                BodypartDragIdentityPatch.Initialize,
                BodypartDragIdentityPatch.ResolveAndValidateTarget,
                prefix: () => new HarmonyMethod(
                    typeof(BodypartDragIdentityPatch),
                    nameof(BodypartDragIdentityPatch.Prefix)),
                requireCurrentMvid: true,
                reset: BodypartDragIdentityPatch.Reset));
        }
    }
}
