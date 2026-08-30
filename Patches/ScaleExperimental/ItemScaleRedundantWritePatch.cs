using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Photon.Pun;
using PeakSafeOptimizer.Diagnostics;
using PeakSafeOptimizer.Patches.Scale;
using UnityEngine;

namespace PeakSafeOptimizer.Patches.ScaleExperimental
{
    /// <summary>
    /// Redundant-write filter for ItemScaleSyncer.Update() (audited source
    /// ItemScaleSyncer.cs:62-70). The original recomputes
    /// scale = (itemState == InBackpack) ? currentScale * 0.5f : currentScale
    /// and unconditionally calls ApplyScale(scale) every frame, writing localScale on either
    /// item.transform or (mainRenderer.transform + colliders[0].transform).
    ///
    /// This patch only skips the original call when every input of that write is unchanged since
    /// the last time the original was allowed to run:
    ///   - owner-side scale change branch outcome (currentScale vs previousScale, 0.01f threshold)
    ///   - itemState (covers the InBackpack 0.5x factor)
    ///   - currentScale bit pattern (covers RPC_SyncScale and Snowball-style growth)
    ///   - _applyDirectlyToMeshAndCollider routing flag
    ///   - the identity of every target transform it would write to
    ///   - the localScale currently present on those transforms (covers external writers)
    /// Anything else, including the first sight of an instance or any unprovable dependency,
    /// returns true so the original method runs unchanged.
    ///
    /// Cost of the signature itself, since it is paid every frame by every ground item: two
    /// GetInstanceID calls, two localScale reads and one ConditionalWeakTable lookup, against the one
    /// or two localScale *writes* it removes. Writes dirty the transform hierarchy and reads do not,
    /// which is why the trade is taken. Two things that used to be on top of that are gone: the
    /// presence of a secondary target is now answered by the routing flag instead of by three
    /// UnityEngine.Object alive tests (see the Prefix), and the table is probed with TryGetValue before
    /// GetOrCreateValue so the steady-state path does not re-enter the table's lock.
    /// </summary>
    internal static class ItemScaleRedundantWritePatch
    {
        private const string ColliderArrayTypeName = "UnityEngine.Collider[]";

        private sealed class State
        {
            // Never references its ConditionalWeakTable key.
            internal bool HasApplied;
            internal int CurrentScaleBits;
            internal int PreviousScaleBits;
            internal int ItemState;
            internal bool ApplyDirectly;
            internal bool IsMine;
            internal int PrimaryTransformId;
            internal int SecondaryTransformId;
            internal Vector3 PrimaryLocalScale;
            internal Vector3 SecondaryLocalScale;
        }

        private static ConditionalWeakTable<ItemScaleSyncer, State> _states;
        private static AccessTools.FieldRef<ItemScaleSyncer, float> _currentScale;
        private static AccessTools.FieldRef<ItemScaleSyncer, float> _previousScale;
        private static AccessTools.FieldRef<ItemScaleSyncer, bool> _applyDirectly;
        private static Func<Item, Renderer> _mainRenderer;
        private static Func<Item, Transform> _firstColliderTransform;

        internal static void Initialize()
        {
            ResolveAndValidateTarget();

            FieldInfo currentScaleField = ScaleValidation.RequireInstanceField(
                typeof(ItemScaleSyncer), "currentScale", typeof(float));
            FieldInfo previousScaleField = ScaleValidation.RequireInstanceField(
                typeof(ItemScaleSyncer), "previousScale", typeof(float));
            FieldInfo applyDirectlyField = ScaleValidation.RequireInstanceField(
                typeof(ItemScaleSyncer), "_applyDirectlyToMeshAndCollider", typeof(bool));
            ScaleValidation.RequireInstanceField(
                typeof(ItemScaleSyncer), "_isInitialized", typeof(bool));

            ScaleValidation.RequireInstanceField(typeof(ItemComponent), "item", typeof(Item));
            ScaleValidation.RequireInstanceProperty(typeof(Item), "itemState", typeof(ItemState));
            if (!Enum.IsDefined(typeof(ItemState), ItemState.InBackpack))
                throw new InvalidOperationException("ItemState.InBackpack is no longer defined");

            FieldInfo mainRendererField = ScaleValidation.RequireInstanceField(
                typeof(Item), "mainRenderer", typeof(Renderer));
            FieldInfo collidersField = ScaleValidation.RequireInstanceFieldByTypeName(
                typeof(Item), "colliders", ColliderArrayTypeName);

            // Proof that the write path this patch guards is still the audited one; never called here.
            ScaleValidation.RequireInstanceMethod(
                typeof(ItemScaleSyncer), "ApplyScale", typeof(void), new[] { typeof(float) }, false);
            ScaleValidation.RequireInstanceMethod(
                typeof(ItemScaleSyncer), "OnScaleChanged", typeof(void), Type.EmptyTypes, false);
            ScaleValidation.RequireInstanceMethod(
                typeof(ItemScaleSyncer), "InitScale", typeof(void), Type.EmptyTypes, true);
            ScaleValidation.RequireInstanceMethodByTypeNames(
                typeof(ItemScaleSyncer), "RPC_SyncScale", typeof(void).FullName,
                new[] { typeof(float).FullName });

            AccessTools.FieldRef<ItemScaleSyncer, float> currentScale =
                AccessTools.FieldRefAccess<ItemScaleSyncer, float>(currentScaleField);
            AccessTools.FieldRef<ItemScaleSyncer, float> previousScale =
                AccessTools.FieldRefAccess<ItemScaleSyncer, float>(previousScaleField);
            AccessTools.FieldRef<ItemScaleSyncer, bool> applyDirectly =
                AccessTools.FieldRefAccess<ItemScaleSyncer, bool>(applyDirectlyField);
            Func<Item, Renderer> mainRenderer =
                ScaleExperimentalEmit.CreateFieldGetter<Item, Renderer>(mainRendererField);
            Func<Item, Transform> firstColliderTransform =
                ScaleExperimentalEmit.CreateFirstElementTransformGetter<Item>(collidersField);
            if (currentScale == null || previousScale == null || applyDirectly == null ||
                mainRenderer == null || firstColliderTransform == null)
            {
                throw new InvalidOperationException("ItemScaleRedundantWrite accessor creation failed");
            }

            _states = new ConditionalWeakTable<ItemScaleSyncer, State>();
            _currentScale = currentScale;
            _previousScale = previousScale;
            _applyDirectly = applyDirectly;
            _mainRenderer = mainRenderer;
            _firstColliderTransform = firstColliderTransform;
        }

        internal static MethodInfo ResolveAndValidateTarget()
        {
            return ScaleValidation.RequireInstanceMethod(
                typeof(ItemScaleSyncer), "Update", typeof(void), Type.EmptyTypes, true);
        }

        internal static void Reset()
        {
            _states = null;
            _currentScale = null;
            _previousScale = null;
            _applyDirectly = null;
            _mainRenderer = null;
            _firstColliderTransform = null;
        }

        internal static bool Prefix(ItemScaleSyncer __instance)
        {
            ConditionalWeakTable<ItemScaleSyncer, State> states = _states;
            AccessTools.FieldRef<ItemScaleSyncer, float> currentScaleRef = _currentScale;
            AccessTools.FieldRef<ItemScaleSyncer, float> previousScaleRef = _previousScale;
            AccessTools.FieldRef<ItemScaleSyncer, bool> applyDirectlyRef = _applyDirectly;
            Func<Item, Renderer> mainRendererRef = _mainRenderer;
            Func<Item, Transform> firstColliderTransformRef = _firstColliderTransform;
            if (states == null || currentScaleRef == null || previousScaleRef == null ||
                applyDirectlyRef == null || mainRendererRef == null || firstColliderTransformRef == null)
            {
                RateLimitedDiagnostics.Hit("ItemScaleRedundantWrite.state-unavailable");
                return true;
            }

            Item item = __instance.item;
            if (item == null)
            {
                RateLimitedDiagnostics.Hit("ItemScaleRedundantWrite.item-null");
                return true;
            }

            PhotonView view = __instance.photonView;
            if (view == null)
            {
                RateLimitedDiagnostics.Hit("ItemScaleRedundantWrite.photonView-null");
                return true;
            }

            bool applyDirectly = applyDirectlyRef(__instance);
            Transform primary;
            Transform secondary;
            if (applyDirectly)
            {
                Renderer renderer = mainRendererRef(item);
                primary = renderer != null ? renderer.transform : null;
                secondary = firstColliderTransformRef(item);
                if (primary == null || secondary == null)
                {
                    // Mirrors the original's dependency set; let it run (and fail the same way).
                    RateLimitedDiagnostics.Hit("ItemScaleRedundantWrite.direct-targets-incomplete");
                    return true;
                }
            }
            else
            {
                primary = item.transform;
                secondary = null;
                if (primary == null)
                {
                    RateLimitedDiagnostics.Hit("ItemScaleRedundantWrite.item-transform-null");
                    return true;
                }
            }

            float currentScale = currentScaleRef(__instance);
            float previousScale = previousScaleRef(__instance);
            bool isMine = view.IsMine;
            int itemState = (int)item.itemState;

            // The owner-side branch can raise an RPC (ItemScaleSyncer.cs:64-67); never suppress it.
            if (isMine && Mathf.Abs(currentScale - previousScale) > 0.01f)
            {
                InvalidateAndRecord(states, __instance, currentScale, previousScale, itemState,
                    applyDirectly, isMine, primary, secondary, recordApplied: false);
                return true;
            }

            // `applyDirectly` stands in for `secondary != null` from here on, which removes three
            // UnityEngine.Object.op_Inequality calls per frame per syncer. The substitution is exact by
            // construction: the direct branch above only reaches this point after proving secondary is
            // Unity-non-null, and the indirect branch assigns the literal null. Nothing in between can
            // change either, since this Prefix performs no writes at all.
            bool hasSecondary = applyDirectly;

            int currentBits = BitConverter.SingleToInt32Bits(currentScale);
            int previousBits = BitConverter.SingleToInt32Bits(previousScale);
            int primaryId = primary.GetInstanceID();
            int secondaryId = hasSecondary ? secondary.GetInstanceID() : 0;
            Vector3 primaryScale = primary.localScale;
            Vector3 secondaryScale = hasSecondary ? secondary.localScale : Vector3.zero;

            // TryGetValue before GetOrCreateValue, matching the other lanes. Both take the same
            // ConditionalWeakTable lock and GetValue calls TryGetValue inside it
            // (mscorlib ConditionalWeakTable.cs:405-437, :461-477), so this saves one uncontended
            // recursive Monitor entry on the steady-state path rather than a whole lock - but the
            // steady state is every frame of every ground item, and the shape is what the rest of the
            // plugin uses.
            if (!states.TryGetValue(__instance, out State state))
                state = states.GetOrCreateValue(__instance);
            if (state.HasApplied &&
                state.CurrentScaleBits == currentBits &&
                state.PreviousScaleBits == previousBits &&
                state.ItemState == itemState &&
                state.ApplyDirectly == applyDirectly &&
                state.IsMine == isMine &&
                state.PrimaryTransformId == primaryId &&
                state.SecondaryTransformId == secondaryId &&
                state.PrimaryLocalScale == primaryScale &&
                state.SecondaryLocalScale == secondaryScale)
            {
                return false;
            }

            state.CurrentScaleBits = currentBits;
            state.PreviousScaleBits = previousBits;
            state.ItemState = itemState;
            state.ApplyDirectly = applyDirectly;
            state.IsMine = isMine;
            state.PrimaryTransformId = primaryId;
            state.SecondaryTransformId = secondaryId;

            // Record the scale the original is about to write so the next frame can compare against
            // the post-write state instead of the pre-write one.
            float applied = itemState == (int)ItemState.InBackpack ? currentScale * 0.5f : currentScale;
            Vector3 appliedScale = applied * Vector3.one;
            state.PrimaryLocalScale = appliedScale;
            state.SecondaryLocalScale = hasSecondary ? appliedScale : Vector3.zero;
            state.HasApplied = true;
            return true;
        }

        /// <summary>
        /// Records the inputs of a frame this patch is *not* allowed to skip, so the next frame
        /// compares against them rather than against a stale signature.
        ///
        /// The two Unity alive tests this used to perform on <paramref name="primary"/> and
        /// <paramref name="secondary"/> are gone, because both are statically decided at the one call
        /// site: primary is proven Unity-non-null by the routing block above, and secondary is proven
        /// non-null when <paramref name="applyDirectly"/> is true and is the literal null when it is
        /// false. The routing flag therefore answers "is there a secondary" without a native call.
        ///
        /// The table is probed with TryGetValue first for the same reason as in the Prefix: GetValue
        /// takes the table lock and then calls TryGetValue inside it (mscorlib
        /// ConditionalWeakTable.cs:405-437, :461-477), so a hit is one uncontended Monitor entry rather
        /// than two.
        /// </summary>
        private static void InvalidateAndRecord(
            ConditionalWeakTable<ItemScaleSyncer, State> states,
            ItemScaleSyncer instance,
            float currentScale,
            float previousScale,
            int itemState,
            bool applyDirectly,
            bool isMine,
            Transform primary,
            Transform secondary,
            bool recordApplied)
        {
            if (!states.TryGetValue(instance, out State state))
                state = states.GetOrCreateValue(instance);
            state.HasApplied = recordApplied;
            state.CurrentScaleBits = BitConverter.SingleToInt32Bits(currentScale);
            state.PreviousScaleBits = BitConverter.SingleToInt32Bits(previousScale);
            state.ItemState = itemState;
            state.ApplyDirectly = applyDirectly;
            state.IsMine = isMine;
            state.PrimaryTransformId = primary.GetInstanceID();
            state.SecondaryTransformId = applyDirectly ? secondary.GetInstanceID() : 0;
            state.PrimaryLocalScale = Vector3.zero;
            state.SecondaryLocalScale = Vector3.zero;
        }
    }
}
