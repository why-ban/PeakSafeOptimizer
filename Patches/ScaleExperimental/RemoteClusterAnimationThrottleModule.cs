using HarmonyLib;
using PeakSafeOptimizer.Config;
using PeakSafeOptimizer.Infrastructure;

namespace PeakSafeOptimizer.Patches.ScaleExperimental
{
    internal sealed class RemoteClusterAnimationThrottleModule : IPatchModule
    {
        public void Register(PatchModuleContext context)
        {
            context.Register(PatchId.RemoteClusterAnimationThrottle, () => new PatchDefinition(
                PatchId.RemoteClusterAnimationThrottle,
                RemoteClusterAnimationThrottlePatch.Initialize,
                RemoteClusterAnimationThrottlePatch.ResolveAndValidateTarget,
                prefix: () => new HarmonyMethod(
                    typeof(RemoteClusterAnimationThrottlePatch),
                    nameof(RemoteClusterAnimationThrottlePatch.Prefix)),
                requireCurrentMvid: true,
                reset: RemoteClusterAnimationThrottlePatch.Reset));
        }
    }
}
