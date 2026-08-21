using HarmonyLib;
using PeakSafeOptimizer.Config;
using PeakSafeOptimizer.Infrastructure;

namespace PeakSafeOptimizer.Patches.Scale
{
    internal sealed class ItemAudioManagerHashModule : IPatchModule
    {
        public void Register(PatchModuleContext context)
        {
            context.Register(PatchId.ItemAudioManagerHash, () => new PatchDefinition(
                PatchId.ItemAudioManagerHash,
                ItemAudioManagerHashPatch.Initialize,
                ItemAudioManagerHashPatch.ResolveAndValidateTarget,
                prefix: () => new HarmonyMethod(
                    typeof(ItemAudioManagerHashPatch),
                    nameof(ItemAudioManagerHashPatch.Prefix)),
                requireCurrentMvid: true,
                reset: ItemAudioManagerHashPatch.Reset));
        }
    }
}
