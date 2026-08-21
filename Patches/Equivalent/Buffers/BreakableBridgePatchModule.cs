using HarmonyLib;
using PeakSafeOptimizer.Config;
using PeakSafeOptimizer.Infrastructure;

namespace PeakSafeOptimizer.Patches.Equivalent.Buffers
{
    internal sealed class BreakableBridgePatchModule : IPatchModule
    {
        public void Register(PatchModuleContext context)
        {
            context.Register(PatchId.BreakableBridge, () => new PatchDefinition(
                PatchId.BreakableBridge,
                BreakableBridgePatch.Initialize,
                BreakableBridgePatch.ResolveAndValidateTarget,
                transpiler: () => new HarmonyMethod(
                    typeof(BreakableBridgePatch),
                    nameof(BreakableBridgePatch.Transpiler)),
                requireCurrentMvid: true,
                reset: BreakableBridgePatch.Reset));
        }
    }
}
