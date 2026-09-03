using System;
using System.Collections.Generic;
using System.Reflection;
using Peak;
using PeakSafeOptimizer.Diagnostics;

namespace PeakSafeOptimizer.Patches.Stability
{
    /// <summary>
    /// Null guard for Peak.ItemOptimizationManager.DeregisterItem(Item), whose body is a bare
    /// <c>ITEM_DATA_LIST.Remove(item)</c> on a <c>Dictionary&lt;Item, ItemOptimizationData&gt;</c>.
    /// <c>Dictionary.Remove</c> throws <c>ArgumentNullException</c> for a null key.
    ///
    /// Vanilla reaches that with null on its own. Peak.ItemOptimizer.OnEnable writes
    /// <c>base.enabled = false</c> when its item reference is null, and that write synchronously
    /// invokes OnDisable, which calls <c>DeregisterItem(item)</c> unconditionally - passing the same
    /// null. Confirmed in-game: 67 occurrences in one session, all under a Photon NetworkInstantiate,
    /// i.e. every time an item prefab spawns with an unset reference. Peak.ItemOptimizer is byte for
    /// byte the same in this build, so the producing path is unchanged.
    ///
    /// This Prefix skips the call only for a null item, which is exactly the case where the original
    /// cannot do anything except throw: a null key is never in the dictionary, so nothing is lost.
    ///
    /// Why "never in the dictionary" still holds on this build. The dictionary is private and the only
    /// mutations in the whole assembly are RegisterItem's <c>Add</c>, DeregisterItem's <c>Remove</c> and
    /// the <c>Clear</c> inside the newly added ClearSceneData - and <c>Add</c> is the only one that can
    /// introduce a key, which it cannot do with null because <c>Dictionary.Add</c> throws on a null key
    /// before storing anything. ClearSceneData only ever makes the dictionary smaller, so it cannot
    /// introduce a null key either; all it does to this guard's surroundings is make a later
    /// <c>Remove</c> on a live item find nothing, which <c>Remove</c> reports as false and the game
    /// ignores. The one member that disappeared in this build, the <c>ALL_ITEMS</c> key enumeration, was
    /// never read or asserted by this patch and carried no part of the argument.
    /// </summary>
    internal static class ItemOptimizerDeregisterGuardPatch
    {
        internal static void Initialize()
        {
            ResolveAndValidateTarget();

            // Proof that the skipped work really is a dictionary removal keyed by the Item, so that
            // skipping a null key cannot discard a real entry. Hand-rolled: the Stability lane's shared
            // validation has no static-field helper, and per project convention the missing check is
            // written locally rather than added to the shared helper.
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

            // The two other mutators of that dictionary are RegisterItem's Add and the Clear inside the
            // newly added ClearSceneData. Neither is asserted, and deliberately so: the guard's
            // correctness does not depend on knowing who they are. A null key cannot be present no
            // matter how many mutators exist, because every insertion path a Dictionary offers - Add and
            // the indexer setter alike - throws on a null key before storing anything. Asserting
            // members this patch never reads would only add fail-closed surface to a crash fix that is
            // on by default.

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
