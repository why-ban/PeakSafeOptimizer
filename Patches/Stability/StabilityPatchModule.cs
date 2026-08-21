using HarmonyLib;
using PeakSafeOptimizer.Config;
using PeakSafeOptimizer.Infrastructure;

namespace PeakSafeOptimizer.Patches.Stability
{
    internal sealed class StabilityPatchModule : IPatchModule
    {
        public void Register(PatchModuleContext context)
        {
            context.Register(PatchId.RunBasedValues, () => new PatchDefinition(
                PatchId.RunBasedValues,
                RunBasedValuesPatch.Initialize,
                RunBasedValuesPatch.ResolveAndValidateTarget,
                prefix: () => new HarmonyMethod(
                    typeof(RunBasedValuesPatch),
                    nameof(RunBasedValuesPatch.Prefix)),
                reset: RunBasedValuesPatch.Reset));

            context.Register(PatchId.Parachute, () => new PatchDefinition(
                PatchId.Parachute,
                ParachutePatch.Initialize,
                ParachutePatch.ResolveAndValidateTarget,
                prefix: () => new HarmonyMethod(
                    typeof(ParachutePatch),
                    nameof(ParachutePatch.Prefix)),
                requireCurrentMvid: true,
                reset: ParachutePatch.Reset));
        }
    }
}
