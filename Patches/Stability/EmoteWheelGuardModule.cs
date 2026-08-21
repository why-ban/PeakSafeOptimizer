using HarmonyLib;
using PeakSafeOptimizer.Config;
using PeakSafeOptimizer.Infrastructure;

namespace PeakSafeOptimizer.Patches.Stability
{
    internal sealed class EmoteWheelGuardModule : IPatchModule
    {
        public void Register(PatchModuleContext context)
        {
            context.Register(PatchId.EmoteWheelGuard, () => new PatchDefinition(
                PatchId.EmoteWheelGuard,
                EmoteWheelGuardPatch.Initialize,
                EmoteWheelGuardPatch.ResolveAndValidateTarget,
                prefix: () => new HarmonyMethod(
                    typeof(EmoteWheelGuardPatch),
                    nameof(EmoteWheelGuardPatch.Prefix)),
                requireCurrentMvid: true,
                reset: EmoteWheelGuardPatch.Reset));
        }
    }
}
