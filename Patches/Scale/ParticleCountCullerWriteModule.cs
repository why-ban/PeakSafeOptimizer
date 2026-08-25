using HarmonyLib;
using PeakSafeOptimizer.Config;
using PeakSafeOptimizer.Infrastructure;

namespace PeakSafeOptimizer.Patches.Scale
{
    internal sealed class ParticleCountCullerWriteModule : IPatchModule
    {
        public void Register(PatchModuleContext context)
        {
            context.Register(PatchId.ParticleCountCullerWrite, () => new PatchDefinition(
                PatchId.ParticleCountCullerWrite,
                ParticleCountCullerWritePatch.Initialize,
                ParticleCountCullerWritePatch.ResolveAndValidateTarget,
                prefix: () => new HarmonyMethod(
                    typeof(ParticleCountCullerWritePatch),
                    nameof(ParticleCountCullerWritePatch.Prefix)),
                requireCurrentMvid: true,
                reset: ParticleCountCullerWritePatch.Reset));
        }
    }
}
