using System;
using System.Collections.Generic;
using System.Reflection;
using Peak;
using PeakSafeOptimizer.Diagnostics;

namespace PeakSafeOptimizer.Patches.Stability
{
    /// <summary>
    /// Skips Peak.ItemOptimizationManager.DeregisterItem when the item is null, which ItemOptimizer.OnEnable
    /// produces by setting enabled = false and synchronously running OnDisable. Remove throws on a null key and
    /// a null key can never be present, since Add is the only insertion and it rejects null. Reference equality
    /// on purpose: a destroyed wrapper is still a valid key and must reach the original to be removed.
    /// </summary>
    internal static class ItemOptimizerDeregisterGuardPatch
    {
        internal static void Initialize()
        {
            ResolveAndValidateTarget();

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
            if ((object)item == null)
            {
                RateLimitedDiagnostics.Hit("ItemOptimizerDeregisterGuard.item-null");
                return false;
            }
            return true;
        }
    }
}
