using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using PeakSafeOptimizer.Diagnostics;
using UnityEngine;

namespace PeakSafeOptimizer.Patches.Stability
{
    /// <summary>
    /// Per-frame guard for CharacterRopeHandling.Climbing() (audited source
    /// CharacterRopeHandling.cs:113-128, invoked every physics step from FixedUpdate at
    /// CharacterRopeHandling.cs:101-111).
    ///
    /// Root cause (confirmed in-game, 184 occurrences in one multiplayer session):
    /// RopeClimbingAPI.GetSegmentFromPercent (RopeClimbingAPI.cs:79-98) forces its low index to 1
    /// when the computed floor index is 0 (line 87), then clamps with
    /// math.clamp(index, 0, ropeSegments.Count - 1). When the segment list is empty the upper bound
    /// becomes -1, math.clamp returns -1, and ropeSegments[-1] throws
    /// ArgumentOutOfRangeException. Both Rope.SegmentCount (Rope.cs:79-89) and
    /// Rope.GetRopeSegments() (Rope.cs:186-193) switch between simulationSegments and
    /// remoteColliderSegments by photonView.IsMine, so a remote client that just joined, or a rope
    /// that has not synced its collider segments yet, legitimately sees an empty list.
    /// Climbing() -> GetUp() -> GetSegmentFromPercent() runs every FixedUpdate, hence the flood.
    ///
    /// Why this guards Climbing() and not GetSegmentFromPercent():
    /// GetSegmentFromPercent returns Transform, and every caller dereferences it immediately
    /// (GetUp reads .up at RopeClimbingAPI.cs:45, UpMult at :55, GetAngleAtPercent at :31,
    /// Rope.cs:170). Making the low-level API return null would only convert
    /// ArgumentOutOfRangeException into NullReferenceException at the same rate. Guarding the
    /// per-frame entry instead skips exactly the frames whose inputs are unusable.
    ///
    /// Semantics: the original already early-exits when heldRope or its photonView is null
    /// (CharacterRopeHandling.cs:115) and in that branch only calls StopAnyClimbing for the local
    /// owner. This patch extends the same "inputs unusable" early-exit with the empty/unavailable
    /// segment list case and skips the frame instead. It never reproduces the original body.
    /// Skipping is state-safe: the two writes in the else branch are
    /// ropeClimbWorldNormal = ropeClimbNormal (idempotent copy, recomputed next frame) and
    /// ropeClimbWorldUp = GetUp(...) (the value that cannot be computed at all this frame), plus an
    /// AddForce that is a per-frame impulse. No counter, timer or latched flag is advanced here,
    /// and Update() (which owns ropePercent, stamina and the stop RPCs) is a different method and
    /// stays untouched.
    /// </summary>
    internal static class RopeSegmentGuardPatch
    {
        private static AccessTools.FieldRef<CharacterRopeHandling, Character> _character;

        internal static void Initialize()
        {
            ResolveAndValidateTarget();

            FieldInfo characterField = StabilityPatchValidation.RequireInstanceField(
                typeof(CharacterRopeHandling), "character", typeof(Character));
            if (characterField.IsPublic)
                throw new InvalidOperationException(
                    "CharacterRopeHandling.character is no longer a private instance field");

            StabilityPatchValidation.RequireInstanceField(typeof(Character), "data", typeof(CharacterData));
            StabilityPatchValidation.RequireInstanceField(typeof(CharacterData), "heldRope", typeof(Rope));
            StabilityPatchValidation.RequireInstanceField(
                typeof(CharacterData), "isRopeClimbing", typeof(bool));
            StabilityPatchValidation.RequireInstanceField(
                typeof(CharacterData), "ropePercent", typeof(float));

            // The members the original body would touch; validated so a reshaped class fails closed.
            StabilityPatchValidation.RequireInstanceField(
                typeof(CharacterData), "ropeClimbWorldNormal", typeof(Vector3));
            StabilityPatchValidation.RequireInstanceField(
                typeof(CharacterData), "ropeClimbWorldUp", typeof(Vector3));
            StabilityPatchValidation.RequireInstanceField(
                typeof(CharacterData), "ropeClimbNormal", typeof(Vector3));
            StabilityPatchValidation.RequireInstanceField(
                typeof(Rope), "climbingAPI", typeof(RopeClimbingAPI));
            StabilityPatchValidation.RequireInstanceMethod(
                typeof(Rope), "GetRopeSegments", typeof(List<Transform>), Type.EmptyTypes);
            StabilityPatchValidation.RequireInstanceMethod(
                typeof(RopeClimbingAPI), "GetUp", typeof(Vector3), new[] { typeof(float) });
            StabilityPatchValidation.RequireInstanceMethod(
                typeof(RopeClimbingAPI), "GetSegmentFromPercent", typeof(Transform), new[] { typeof(float) });

            AccessTools.FieldRef<CharacterRopeHandling, Character> character =
                AccessTools.FieldRefAccess<CharacterRopeHandling, Character>(characterField);
            if (character == null)
                throw new InvalidOperationException("RopeSegmentGuard accessor creation failed");
            _character = character;
        }

        internal static MethodInfo ResolveAndValidateTarget()
        {
            return StabilityPatchValidation.RequireInstanceMethod(
                typeof(CharacterRopeHandling), "Climbing", typeof(void), Type.EmptyTypes,
                requirePublic: false);
        }

        internal static void Reset()
        {
            _character = null;
        }

        internal static bool Prefix(CharacterRopeHandling __instance)
        {
            AccessTools.FieldRef<CharacterRopeHandling, Character> characterRef = _character;
            if (characterRef == null)
            {
                RateLimitedDiagnostics.Hit("RopeSegmentGuard.accessors-unavailable");
                return true;
            }

            Character character = characterRef(__instance);
            CharacterData data = character != null ? character.data : null;
            if (character == null || data == null)
            {
                // The original would dereference these immediately; let it behave as before.
                RateLimitedDiagnostics.Hit("RopeSegmentGuard.character-unavailable");
                return true;
            }

            Rope rope = data.heldRope;
            if (rope == null || rope.photonView == null)
            {
                // Already handled by the original's own early-exit at CharacterRopeHandling.cs:115,
                // which also performs StopAnyClimbing for the owner. Do not take that over.
                return true;
            }

            if (rope.climbingAPI == null)
            {
                // Original would throw on climbingAPI.GetUp; skipping is the conservative choice.
                RateLimitedDiagnostics.Hit("RopeSegmentGuard.climbingAPI-null");
                return false;
            }

            List<Transform> segments = rope.GetRopeSegments();
            if (segments == null)
            {
                RateLimitedDiagnostics.Hit("RopeSegmentGuard.segments-null");
                return false;
            }
            if (segments.Count == 0)
            {
                RateLimitedDiagnostics.Hit("RopeSegmentGuard.segments-empty");
                return false;
            }

            return true;
        }
    }
}
