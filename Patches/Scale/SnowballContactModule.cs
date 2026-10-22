using HarmonyLib;
using PeakSafeOptimizer.Config;
using PeakSafeOptimizer.Infrastructure;

namespace PeakSafeOptimizer.Patches.Scale
{
    internal sealed class SnowballContactModule : IPatchModule
    {
        public void Register(PatchModuleContext context)
        {
            // One shared implementation behind two ids and one config switch, because a writer without
            // its reader is a state nobody audited. Reset is registered on both rather than reference
            // counted: each Prefix copies every accessor into a local and null-checks it, so a Reset
            // landing while the other half is patched degrades that half to vanilla instead of throwing
            context.Register(PatchId.SnowballContactSnapshot, () => new PatchDefinition(
                PatchId.SnowballContactSnapshot,
                SnowballContactPatch.Initialize,
                SnowballContactPatch.ResolveStayTarget,
                prefix: () => new HarmonyMethod(
                    typeof(SnowballContactPatch),
                    nameof(SnowballContactPatch.StayPrefix)),
                requireCurrentMvid: true,
                reset: SnowballContactPatch.Reset));

            context.Register(PatchId.SnowballContactConsume, () => new PatchDefinition(
                PatchId.SnowballContactConsume,
                SnowballContactPatch.Initialize,
                SnowballContactPatch.ResolveProcessTarget,
                prefix: () => new HarmonyMethod(
                    typeof(SnowballContactPatch),
                    nameof(SnowballContactPatch.ProcessPrefix)),
                requireCurrentMvid: true,
                reset: SnowballContactPatch.Reset));
        }
    }
}
