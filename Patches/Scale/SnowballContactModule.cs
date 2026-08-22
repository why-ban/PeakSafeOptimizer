using HarmonyLib;
using PeakSafeOptimizer.Config;
using PeakSafeOptimizer.Infrastructure;

namespace PeakSafeOptimizer.Patches.Scale
{
    internal sealed class SnowballContactModule : IPatchModule
    {
        public void Register(PatchModuleContext context)
        {
            // Two independently gated entries over one shared implementation. Both must be installed
            // before Plugin turns on Physics.reuseCollisionCallbacks, because the snapshot is only
            // useful if the consumer reads it.
            context.Register(PatchId.SnowballContactSnapshot, () => new PatchDefinition(
                PatchId.SnowballContactSnapshot,
                SnowballContactPatch.Initialize,
                SnowballContactPatch.ResolveStayTarget,
                prefix: () => new HarmonyMethod(
                    typeof(SnowballContactPatch),
                    nameof(SnowballContactPatch.StayPrefix)),
                requireCurrentMvid: true));

            context.Register(PatchId.SnowballContactConsume, () => new PatchDefinition(
                PatchId.SnowballContactConsume,
                SnowballContactPatch.Initialize,
                SnowballContactPatch.ResolveProcessTarget,
                prefix: () => new HarmonyMethod(
                    typeof(SnowballContactPatch),
                    nameof(SnowballContactPatch.ProcessPrefix)),
                requireCurrentMvid: true));
        }
    }
}
