using HarmonyLib;
using PeakSafeOptimizer.Config;
using PeakSafeOptimizer.Infrastructure;

namespace PeakSafeOptimizer.Patches.Scale
{
    internal sealed class ItemDatabaseNameLookupModule : IPatchModule
    {
        public void Register(PatchModuleContext context)
        {
            context.Register(PatchId.ItemDatabaseNameLookup, () => new PatchDefinition(
                PatchId.ItemDatabaseNameLookup,
                ItemDatabaseNameLookupPatch.Initialize,
                ItemDatabaseNameLookupPatch.ResolveAndValidateTarget,
                prefix: () => new HarmonyMethod(
                    typeof(ItemDatabaseNameLookupPatch),
                    nameof(ItemDatabaseNameLookupPatch.Prefix)),
                requireCurrentMvid: true,
                reset: ItemDatabaseNameLookupPatch.Reset));
            context.RegisterCleanup(ItemDatabaseNameLookupPatch.Reset);
        }
    }
}
