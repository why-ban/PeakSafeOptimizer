using HarmonyLib;
using PeakSafeOptimizer.Config;
using PeakSafeOptimizer.Infrastructure;

namespace PeakSafeOptimizer.Patches.Stability
{
    internal sealed class BugleClipGuardModule : IPatchModule
    {
        public void Register(PatchModuleContext context)
        {
            context.Register(PatchId.BugleClipGuard, () => new PatchDefinition(
                PatchId.BugleClipGuard,
                BugleClipGuardPatch.Initialize,
                BugleClipGuardPatch.ResolveAndValidateTarget,
                prefix: () => new HarmonyMethod(
                    typeof(BugleClipGuardPatch),
                    nameof(BugleClipGuardPatch.Prefix)),
                requireCurrentMvid: true,
                reset: BugleClipGuardPatch.Reset));
        }
    }
}
