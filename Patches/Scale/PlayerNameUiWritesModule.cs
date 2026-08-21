using HarmonyLib;
using PeakSafeOptimizer.Config;
using PeakSafeOptimizer.Infrastructure;

namespace PeakSafeOptimizer.Patches.Scale
{
    internal sealed class PlayerNameUiWritesModule : IPatchModule
    {
        public void Register(PatchModuleContext context)
        {
            context.Register(PatchId.PlayerNameUiWrites, () => new PatchDefinition(
                PatchId.PlayerNameUiWrites,
                PlayerNameUiWritesPatch.Initialize,
                PlayerNameUiWritesPatch.ResolveAndValidateTarget,
                prefix: () => new HarmonyMethod(
                    typeof(PlayerNameUiWritesPatch),
                    nameof(PlayerNameUiWritesPatch.Prefix)),
                requireCurrentMvid: true,
                reset: PlayerNameUiWritesPatch.Reset));
        }
    }
}
