using HarmonyLib;
using PeakSafeOptimizer.Config;
using PeakSafeOptimizer.Infrastructure;

namespace PeakSafeOptimizer.Patches.ScaleExperimental
{
    internal sealed class ItemScaleRedundantWriteModule : IPatchModule
    {
        public void Register(PatchModuleContext context)
        {
            context.Register(PatchId.ItemScaleRedundantWrite, () => new PatchDefinition(
                PatchId.ItemScaleRedundantWrite,
                ItemScaleRedundantWritePatch.Initialize,
                ItemScaleRedundantWritePatch.ResolveAndValidateTarget,
                prefix: () => new HarmonyMethod(
                    typeof(ItemScaleRedundantWritePatch),
                    nameof(ItemScaleRedundantWritePatch.Prefix)),
                requireCurrentMvid: true,
                reset: ItemScaleRedundantWritePatch.Reset));
            context.RegisterCleanup(ItemScaleRedundantWritePatch.Reset);
        }
    }
}
