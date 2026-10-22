using HarmonyLib;
using PeakSafeOptimizer.Config;
using PeakSafeOptimizer.Infrastructure;

namespace PeakSafeOptimizer.Patches.Scale
{
    internal sealed class ItemPromptDuplicateRefreshModule : IPatchModule
    {
        public void Register(PatchModuleContext context)
        {
            context.Register(PatchId.ItemPromptDuplicateRefresh, () => new PatchDefinition(
                PatchId.ItemPromptDuplicateRefresh,
                ItemPromptDuplicateRefreshPatch.Initialize,
                ItemPromptDuplicateRefreshPatch.ResolveAndValidateTarget,
                prefix: () => new HarmonyMethod(
                    typeof(ItemPromptDuplicateRefreshPatch),
                    nameof(ItemPromptDuplicateRefreshPatch.Prefix)),
                requireCurrentMvid: true,
                reset: ItemPromptDuplicateRefreshPatch.Reset));
        }
    }
}
