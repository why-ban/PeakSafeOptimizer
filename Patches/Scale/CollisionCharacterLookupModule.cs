using HarmonyLib;
using PeakSafeOptimizer.Config;
using PeakSafeOptimizer.Infrastructure;

namespace PeakSafeOptimizer.Patches.Scale
{
    internal sealed class CollisionCharacterLookupModule : IPatchModule
    {
        public void Register(PatchModuleContext context)
        {
            context.Register(PatchId.CollisionCharacterLookup, () => new PatchDefinition(
                PatchId.CollisionCharacterLookup,
                CollisionCharacterLookupPatch.Initialize,
                CollisionCharacterLookupPatch.ResolveAndValidateTarget,
                prefix: () => new HarmonyMethod(
                    typeof(CollisionCharacterLookupPatch),
                    nameof(CollisionCharacterLookupPatch.Prefix)),
                requireCurrentMvid: true,
                reset: CollisionCharacterLookupPatch.Reset));
        }
    }
}
