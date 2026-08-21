using HarmonyLib;
using PeakSafeOptimizer.Config;
using PeakSafeOptimizer.Infrastructure;

namespace PeakSafeOptimizer.Patches.ScaleExperimental
{
    internal sealed class RemoteItemInterpolationThresholdModule : IPatchModule
    {
        public void Register(PatchModuleContext context)
        {
            context.Register(PatchId.RemoteItemInterpolationThreshold, () => new PatchDefinition(
                PatchId.RemoteItemInterpolationThreshold,
                RemoteItemInterpolationThresholdPatch.Initialize,
                RemoteItemInterpolationThresholdPatch.ResolveAndValidateTarget,
                prefix: () => new HarmonyMethod(
                    typeof(RemoteItemInterpolationThresholdPatch),
                    nameof(RemoteItemInterpolationThresholdPatch.Prefix)),
                requireCurrentMvid: true,
                reset: RemoteItemInterpolationThresholdPatch.Reset));
        }
    }
}
