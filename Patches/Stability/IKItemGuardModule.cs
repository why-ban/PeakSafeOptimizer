using HarmonyLib;
using PeakSafeOptimizer.Config;
using PeakSafeOptimizer.Infrastructure;

namespace PeakSafeOptimizer.Patches.Stability
{
    internal sealed class IKItemGuardModule : IPatchModule
    {
        public void Register(PatchModuleContext context)
        {
            context.Register(PatchId.IKItemGuard, () => new PatchDefinition(
                PatchId.IKItemGuard,
                IKItemGuardPatch.Initialize,
                IKItemGuardPatch.ResolveAndValidateTarget,
                prefix: () => new HarmonyMethod(
                    typeof(IKItemGuardPatch),
                    nameof(IKItemGuardPatch.Prefix)),
                requireCurrentMvid: true,
                reset: IKItemGuardPatch.Reset));
        }
    }
}
