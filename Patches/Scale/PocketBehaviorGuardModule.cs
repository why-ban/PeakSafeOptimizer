using HarmonyLib;
using PeakSafeOptimizer.Config;
using PeakSafeOptimizer.Infrastructure;

namespace PeakSafeOptimizer.Patches.Scale
{
    internal sealed class PocketBehaviorGuardModule : IPatchModule
    {
        public void Register(PatchModuleContext context)
        {
            context.Register(PatchId.PocketBehaviorGuard, () => new PatchDefinition(
                PatchId.PocketBehaviorGuard,
                PocketBehaviorGuardPatch.Initialize,
                PocketBehaviorGuardPatch.ResolveAndValidateTarget,
                prefix: () => new HarmonyMethod(
                    typeof(PocketBehaviorGuardPatch),
                    nameof(PocketBehaviorGuardPatch.Prefix)),
                requireCurrentMvid: true,
                reset: PocketBehaviorGuardPatch.Reset));
        }
    }
}
