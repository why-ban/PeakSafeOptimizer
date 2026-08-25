using System;
using System.Collections.Generic;
using System.Reflection;
using Peak;
using PeakSafeOptimizer.Diagnostics;

namespace PeakSafeOptimizer.Patches.Stability
{
    /// <summary>
    /// Null guard for Peak.ItemOptimizationManager.DeregisterItem(Item) (audited source
    /// Peak\ItemOptimizationManager.cs:42-45), whose body is a bare
    /// <c>ITEM_DATA_LIST.Remove(item)</c> on a <c>Dictionary&lt;Item, ItemOptimizationData&gt;</c>.
    /// <c>Dictionary.Remove</c> throws <c>ArgumentNullException</c> for a null key.
    ///
    /// Vanilla reaches that with null on its own. ItemOptimizer.OnEnable writes
    /// <c>base.enabled = false</c> when its item reference is null (Peak\ItemOptimizer.cs:9-19), and
    /// that write synchronously invokes OnDisable, which calls <c>DeregisterItem(item)</c>
    /// unconditionally (:21-24) - passing the same null. Confirmed in-game: 67 occurrences in one
    /// session, all under a Photon NetworkInstantiate, i.e. every time an item prefab spawns with an
    /// unset reference.
    ///
    /// This Prefix skips the call only for a null item, which is exactly the case where the original
    /// cannot do anything except throw: a null key is not in the dictionary, so nothing is lost.
    /// </summary>
    internal static class ItemOptimizerDeregisterGuardPatch
    {
        internal static void Initialize()
        {
            ResolveAndValidateTarget();

            // Proof that the skipped work really is a dictionary removal keyed by the Item, so that
            // skipping a null key cannot discard a real entry.
            FieldInfo list = typeof(ItemOptimizationManager).GetField(
                "ITEM_DATA_LIST",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (list == null || !list.IsStatic || !list.FieldType.IsGenericType ||
                list.FieldType.GetGenericTypeDefinition() != typeof(Dictionary<,>) ||
                list.FieldType.GetGenericArguments()[0] != typeof(Item))
            {
                throw new InvalidOperationException(
                    "ItemOptimizationManager.ITEM_DATA_LIST is no longer a Dictionary keyed by Item");
            }

            // The path that produces the null: OnEnable's disable-on-null, and the OnDisable it
            // synchronously triggers.
            StabilityPatchValidation.RequireInstanceField(typeof(ItemOptimizer), "item", typeof(Item));
        }

        internal static MethodInfo ResolveAndValidateTarget()
        {
            return StabilityPatchValidation.RequireStaticMethod(
                typeof(ItemOptimizationManager),
                "DeregisterItem",
                typeof(void),
                new[] { typeof(Item) });
        }

        internal static void Reset()
        {
        }

        internal static bool Prefix(Item item)
        {
            // Reference equality only. A destroyed-but-not-null Item is still a valid dictionary key
            // and must reach the original so its entry is actually removed.
            if ((object)item == null)
            {
                RateLimitedDiagnostics.Hit("ItemOptimizerDeregisterGuard.item-null");
                return false;
            }
            return true;
        }
    }
}
