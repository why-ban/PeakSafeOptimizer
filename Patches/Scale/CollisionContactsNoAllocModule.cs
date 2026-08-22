using HarmonyLib;
using PeakSafeOptimizer.Config;
using PeakSafeOptimizer.Infrastructure;

namespace PeakSafeOptimizer.Patches.Scale
{
    internal sealed class CollisionContactsNoAllocModule : IPatchModule
    {
        public void Register(PatchModuleContext context)
        {
            context.Register(PatchId.CollisionContactsNoAlloc, () => new PatchDefinition(
                PatchId.CollisionContactsNoAlloc,
                CollisionContactsNoAllocPatch.Initialize,
                CollisionContactsNoAllocPatch.ResolveAndValidateTarget,
                prefix: () => new HarmonyMethod(
                    typeof(CollisionContactsNoAllocPatch),
                    nameof(CollisionContactsNoAllocPatch.Prefix)),
                requireCurrentMvid: true,
                reset: CollisionContactsNoAllocPatch.Reset));
        }
    }
}
