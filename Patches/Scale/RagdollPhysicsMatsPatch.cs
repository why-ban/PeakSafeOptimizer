using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using PeakSafeOptimizer.Diagnostics;
using UnityEngine;

namespace PeakSafeOptimizer.Patches.Scale
{
    /// <summary>
    /// Redundancy filter for CharacterRagdoll.SetPhysicsMats() (audited source
    /// CharacterRagdoll.cs:64-70, invoked from CharacterRagdoll.FixedUpdate at
    /// CharacterRagdoll.cs:153). The original assigns sharedMaterial for every collider of every
    /// bodypart on every physics step.
    ///
    /// This patch never re-implements the write: it only skips the original call when the complete
    /// input signature is identical to the last applied one. First sight, any change in friction
    /// type, part count, per-part collider count, per-part frictionType, or either material
    /// reference forces the original method to run again, preserving write order and material
    /// selection exactly.
    ///
    /// The signature also folds in the identity of the material actually present on every collider
    /// of each part, so an external writer that changes sharedMaterial on any of them is detected.
    /// As a further safety net the original is force-allowed through at least every 300 frames or 5
    /// seconds even when nothing appears to have changed, so any undetected divergence self-heals.
    /// </summary>
    internal static class RagdollPhysicsMatsPatch
    {
        private const string PhysicsMaterialTypeName = "UnityEngine.PhysicsMaterial";
        private const string RigCreatorColliderTypeName = "RigCreatorCollider";
        private const string CapsuleColliderTypeName = "UnityEngine.CapsuleCollider";
        private const int ForceRefreshFrameInterval = 300;
        private const float ForceRefreshSecondsInterval = 5f;
        private const int MissingColliderMaterialSentinel = int.MinValue;

        private sealed class State
        {
            // Never references its ConditionalWeakTable key.
            internal bool HasApplied;
            internal int FrictionType;
            internal int PartCount;
            internal int ColliderSignature;
            internal int SlipperyMatId;
            internal int NormalMatId;
            internal int LastForcedFrame;
            internal float LastForcedTime;

            // Signature we allowed the original to write last call, not yet confirmed as applied.
            // It is promoted to the applied signature only when a later call proves the previous
            // original call has returned.
            internal bool HasPending;
            internal int PendingFrictionType;
            internal int PendingPartCount;
            internal int PendingColliderSignature;
            internal int PendingSlipperyMatId;
            internal int PendingNormalMatId;
        }

        private static ConditionalWeakTable<CharacterRagdoll, State> _states;
        private static AccessTools.FieldRef<CharacterRagdoll, Character> _character;
        private static Func<CharacterRagdoll, UnityEngine.Object> _slipperyMat;
        private static Func<CharacterRagdoll, UnityEngine.Object> _normalMat;
        private static Func<Bodypart, int> _colliderCount;
        private static Func<Bodypart, int, int> _allColliderMaterialIdHash;

        internal static void Initialize()
        {
            ResolveAndValidateTarget();

            FieldInfo characterField = ScaleValidation.RequireInstanceField(
                typeof(CharacterRagdoll), "character", typeof(Character));
            ScaleValidation.RequireInstanceField(
                typeof(CharacterRagdoll), "partList", typeof(List<Bodypart>));
            FieldInfo slipperyField = ScaleValidation.RequireInstanceFieldByTypeName(
                typeof(CharacterRagdoll), "slipperyMat", PhysicsMaterialTypeName);
            FieldInfo normalField = ScaleValidation.RequireInstanceFieldByTypeName(
                typeof(CharacterRagdoll), "normalMat", PhysicsMaterialTypeName);

            ScaleValidation.RequireInstanceField(
                typeof(Bodypart), "frictionType", typeof(Bodypart.FrictionType));
            FieldInfo collidersField = typeof(Bodypart).GetField(
                "colliders",
                BindingFlags.Instance | BindingFlags.Public |
                BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (collidersField == null || collidersField.IsStatic ||
                !ScaleEmit.IsClosedGenericListOf(collidersField.FieldType, RigCreatorColliderTypeName))
            {
                throw new InvalidOperationException("Bodypart.colliders failed structural self-check");
            }

            ScaleValidation.RequireInstanceField(typeof(Character), "data", typeof(CharacterData));
            ScaleValidation.RequireInstanceProperty(
                typeof(CharacterData), "currentRagdollControll", typeof(float));

            // Resolve RigCreatorCollider.col -> Collider.sharedMaterial so the signature can include
            // the material identity actually applied on the collider.
            Type colliderElementType = collidersField.FieldType.GetGenericArguments()[0];
            FieldInfo colField = colliderElementType.GetField(
                "col",
                BindingFlags.Instance | BindingFlags.Public |
                BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (colField == null || colField.IsStatic ||
                colField.FieldType.FullName != CapsuleColliderTypeName)
            {
                throw new InvalidOperationException("RigCreatorCollider.col failed structural self-check");
            }
            PropertyInfo sharedMaterialProperty = ScaleValidation.RequireInstancePropertyByTypeName(
                colField.FieldType, "sharedMaterial", PhysicsMaterialTypeName, requireSetter: true);

            // Proof that the original write path is unchanged; this patch never calls it directly.
            ScaleValidation.RequireInstanceMethodByTypeNames(
                typeof(Bodypart),
                "SetPhysicsMaterial",
                typeof(void).FullName,
                new[]
                {
                    typeof(Bodypart.FrictionType).FullName,
                    PhysicsMaterialTypeName,
                    PhysicsMaterialTypeName
                });
            ScaleValidation.RequireInstanceMethod(
                typeof(CharacterRagdoll),
                "GetFrictionType",
                typeof(Bodypart.FrictionType),
                Type.EmptyTypes,
                false);

            AccessTools.FieldRef<CharacterRagdoll, Character> character =
                AccessTools.FieldRefAccess<CharacterRagdoll, Character>(characterField);
            Func<CharacterRagdoll, UnityEngine.Object> slippery =
                ScaleEmit.CreateUnityObjectFieldGetter<CharacterRagdoll>(slipperyField);
            Func<CharacterRagdoll, UnityEngine.Object> normal =
                ScaleEmit.CreateUnityObjectFieldGetter<CharacterRagdoll>(normalField);
            Func<Bodypart, int> colliderCount =
                ScaleEmit.CreateCollectionCountGetter<Bodypart>(collidersField);
            Func<Bodypart, int, int> allColliderMaterialIdHash =
                ScaleEmit.CreateAllElementsNestedObjectIdHash<Bodypart>(
                    collidersField, colField, sharedMaterialProperty);
            if (character == null || slippery == null || normal == null ||
                colliderCount == null || allColliderMaterialIdHash == null)
                throw new InvalidOperationException("RagdollPhysicsMats accessor creation failed");

            _states = new ConditionalWeakTable<CharacterRagdoll, State>();
            _character = character;
            _slipperyMat = slippery;
            _normalMat = normal;
            _colliderCount = colliderCount;
            _allColliderMaterialIdHash = allColliderMaterialIdHash;
        }

        internal static MethodInfo ResolveAndValidateTarget()
        {
            return ScaleValidation.RequireInstanceMethod(
                typeof(CharacterRagdoll), "SetPhysicsMats", typeof(void), Type.EmptyTypes, false);
        }

        internal static void Reset()
        {
            _states = null;
            _character = null;
            _slipperyMat = null;
            _normalMat = null;
            _colliderCount = null;
            _allColliderMaterialIdHash = null;
        }

        internal static bool Prefix(CharacterRagdoll __instance)
        {
            ConditionalWeakTable<CharacterRagdoll, State> states = _states;
            AccessTools.FieldRef<CharacterRagdoll, Character> characterRef = _character;
            Func<CharacterRagdoll, UnityEngine.Object> slipperyRef = _slipperyMat;
            Func<CharacterRagdoll, UnityEngine.Object> normalRef = _normalMat;
            Func<Bodypart, int> colliderCountRef = _colliderCount;
            Func<Bodypart, int, int> allColliderMaterialIdHashRef = _allColliderMaterialIdHash;
            if (states == null || characterRef == null || slipperyRef == null ||
                normalRef == null || colliderCountRef == null || allColliderMaterialIdHashRef == null)
            {
                RateLimitedDiagnostics.Hit("RagdollPhysicsMats.state-unavailable");
                return true;
            }

            List<Bodypart> parts = __instance.partList;
            Character owner = characterRef(__instance);
            if (parts == null || owner == null || owner.data == null)
            {
                RateLimitedDiagnostics.Hit("RagdollPhysicsMats.inputs-incomplete");
                return true;
            }

            // Mirrors the audited GetFrictionType() threshold (CharacterRagdoll.cs:89-96)
            // only to detect change; the material assignment itself stays in the original method.
            int frictionType = owner.data.currentRagdollControll < 0.9f
                ? (int)Bodypart.FrictionType.Grippy
                : (int)Bodypart.FrictionType.Slippery;

            int partCount = parts.Count;
            int colliderSignature = 17;
            for (int i = 0; i < partCount; i++)
            {
                Bodypart part = parts[i];
                if (part == null)
                {
                    RateLimitedDiagnostics.Hit("RagdollPhysicsMats.part-null");
                    return true;
                }
                int colliders = colliderCountRef(part);
                if (colliders < 0)
                {
                    RateLimitedDiagnostics.Hit("RagdollPhysicsMats.colliders-null");
                    return true;
                }
                unchecked
                {
                    colliderSignature = colliderSignature * 31 + colliders;
                    colliderSignature = colliderSignature * 31 + (int)part.frictionType;
                    // Real applied material identity of every collider: detects external
                    // sharedMaterial writes on any collider, not just the first.
                    colliderSignature = colliderSignature * 31 +
                        allColliderMaterialIdHashRef(part, MissingColliderMaterialSentinel);
                }
            }

            UnityEngine.Object slippery = slipperyRef(__instance);
            UnityEngine.Object normal = normalRef(__instance);
            int slipperyId = slippery != null ? slippery.GetInstanceID() : 0;
            int normalId = normal != null ? normal.GetInstanceID() : 0;

            State state = states.GetOrCreateValue(__instance);
            int frame = Time.frameCount;
            float now = Time.time;

            // Promote the previously allowed signature only now that a later call proves the last
            // original invocation returned (it can only be reached again after the previous call
            // completed). This prevents marking a signature applied before the original actually
            // wrote it, which would be wrong if that invocation threw.
            if (state.HasPending)
            {
                state.HasApplied = true;
                state.FrictionType = state.PendingFrictionType;
                state.PartCount = state.PendingPartCount;
                state.ColliderSignature = state.PendingColliderSignature;
                state.SlipperyMatId = state.PendingSlipperyMatId;
                state.NormalMatId = state.PendingNormalMatId;
                state.HasPending = false;
            }

            bool forceRefresh = !state.HasApplied ||
                frame - state.LastForcedFrame >= ForceRefreshFrameInterval ||
                frame < state.LastForcedFrame ||
                now - state.LastForcedTime >= ForceRefreshSecondsInterval ||
                now < state.LastForcedTime;

            if (!forceRefresh &&
                state.HasApplied &&
                state.FrictionType == frictionType &&
                state.PartCount == partCount &&
                state.ColliderSignature == colliderSignature &&
                state.SlipperyMatId == slipperyId &&
                state.NormalMatId == normalId)
            {
                return false;
            }

            // Record the inputs we are about to let the original write as pending; they are promoted
            // to applied only on the next call, after the original has run to completion.
            state.PendingFrictionType = frictionType;
            state.PendingPartCount = partCount;
            state.PendingColliderSignature = colliderSignature;
            state.PendingSlipperyMatId = slipperyId;
            state.PendingNormalMatId = normalId;
            state.HasPending = true;
            state.LastForcedFrame = frame;
            state.LastForcedTime = now;
            return true;
        }
    }
}
