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

            context.Register(PatchId.BinocularSunGuard, () => new PatchDefinition(
                PatchId.BinocularSunGuard,
                BinocularSunGuardPatch.Initialize,
                BinocularSunGuardPatch.ResolveAndValidateTarget,
                prefix: () => new HarmonyMethod(
                    typeof(BinocularSunGuardPatch),
                    nameof(BinocularSunGuardPatch.Prefix)),
                requireCurrentMvid: true,
                reset: BinocularSunGuardPatch.Reset));

            context.Register(PatchId.ItemOptimizerDeregisterGuard, () => new PatchDefinition(
                PatchId.ItemOptimizerDeregisterGuard,
                ItemOptimizerDeregisterGuardPatch.Initialize,
                ItemOptimizerDeregisterGuardPatch.ResolveAndValidateTarget,
                prefix: () => new HarmonyMethod(
                    typeof(ItemOptimizerDeregisterGuardPatch),
                    nameof(ItemOptimizerDeregisterGuardPatch.Prefix)),
                requireCurrentMvid: true,
                reset: ItemOptimizerDeregisterGuardPatch.Reset));
        }
    }
}
