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
            //
            // Both entries share one Initialize and one set of static accessors, so they also share
            // one Reset (SnowballContactPatch.cs:177-191). Reset is deliberately registered on both
            // rather than reference counted, unlike RemoteRagdollLod:
            //   * Both Prefixes open by copying every accessor they need into locals and returning
            //     true when any of them is null (SnowballContactPatch.cs:203-210 and :267-276), so a
            //     Reset that lands while the other half is still patched degrades that half to the
            //     untouched vanilla method. It cannot NRE and it cannot half-execute.
            //   * Vanilla is the coherent target state here. These two are one config switch
            //     (Fixes.SnowballContactGuard, OptimizerConfig.cs:306-312) precisely because a
            //     writer without a reader, or a reader without a writer, is a state nobody audited.
            //     Keeping the accessors alive for a lone survivor would instead preserve that
            //     unaudited state - and for the lone consumer it is measurably worse: the Snapshots
            //     table outlives the writer, so ProcessPrefix would consume one tick of stale
            //     collider/normal data before the table self-clears, whereas a null accessor sends
            //     it straight down the vanilla path.
            //   * Only an abnormal path can produce a lone survivor at all: the group switch toggles
            //     both, so it takes one uninstall succeeding while the other fails, or one install
            //     failing after the other succeeded (PatchInstaller.cs:161/174/193 call Reset for a
            //     definition that never got installed).
            // The remaining cost is the intended one: after the group is switched off, the FieldRefs
            // and the UpdateMass delegate are released instead of being pinned for the process.
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
