using HarmonyLib;
using PeakSafeOptimizer.Config;
using PeakSafeOptimizer.Infrastructure;

namespace PeakSafeOptimizer.Patches.Scale
{
    internal sealed class ItemCollisionModeModule : IPatchModule
    {
        public void Register(PatchModuleContext context)
        {
            context.Register(PatchId.ItemCollisionMode, () => new PatchDefinition(
                PatchId.ItemCollisionMode,
                ItemCollisionModePatch.Initialize,
                ItemCollisionModePatch.ResolveAndValidateTarget,
                prefix: () => new HarmonyMethod(
                    typeof(ItemCollisionModePatch),
                    nameof(ItemCollisionModePatch.Prefix)),
                requireCurrentMvid: true,
                reset: ItemCollisionModePatch.Reset));
        }
    }
}
