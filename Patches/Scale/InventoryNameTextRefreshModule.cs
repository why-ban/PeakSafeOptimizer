using HarmonyLib;
using PeakSafeOptimizer.Config;
using PeakSafeOptimizer.Infrastructure;

namespace PeakSafeOptimizer.Patches.Scale
{
    internal sealed class InventoryNameTextRefreshModule : IPatchModule
    {
        public void Register(PatchModuleContext context)
        {
            context.Register(PatchId.InventoryNameTextRefresh, () => new PatchDefinition(
                PatchId.InventoryNameTextRefresh,
                InventoryNameTextRefreshPatch.Initialize,
                InventoryNameTextRefreshPatch.ResolveAndValidateTarget,
                prefix: () => new HarmonyMethod(
                    typeof(InventoryNameTextRefreshPatch),
                    nameof(InventoryNameTextRefreshPatch.Prefix)),
                postfix: () => new HarmonyMethod(
                    typeof(InventoryNameTextRefreshPatch),
                    nameof(InventoryNameTextRefreshPatch.Postfix)),
                requireCurrentMvid: true,
                reset: InventoryNameTextRefreshPatch.Reset));
        }
    }
}
