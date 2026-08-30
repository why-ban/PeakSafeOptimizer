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
    ///
    /// What skipping costs, argued from the writes themselves (CharacterRopeHandling.cs:124-126):
    ///
    ///   * <c>ropeClimbWorldUp = GetUp(...)</c> is the value that cannot be computed at all this
    ///     frame - that is the whole reason the original raises.
    ///   * <c>AddForce(ClimbForce())</c> is a per-frame impulse, and ClimbForce itself goes through
    ///     GetPosition (:137), which would raise on the same empty segment list.
    ///   * <c>ropeClimbWorldNormal = ropeClimbNormal</c> is NOT an idempotent copy that gets
    ///     recomputed next frame. <c>ropeClimbNormal</c> has exactly one writer in the whole
    ///     assembly, the grab RPC at CharacterRopeHandling.cs:158
    ///     (<c>= -character.data.lookDirection_Flat</c>), so it is constant for the entire climb;
    ///     the copy at :124 only has an effect the first time Climbing() executes successfully during
    ///     that climb. If the segment list is empty from the moment of the grab - precisely the
    ///     just-joined remote client this patch exists for - <c>ropeClimbWorldNormal</c> retains the
    ///     residue of the previous climb, or <c>Vector3.zero</c> on the first climb of the session.
    ///
    /// Skipping is nevertheless the right call, for two reasons that do not depend on that copy:
    ///   1. The only surviving consumer of <c>ropeClimbWorldNormal</c> outside the skipped method is
    ///      Character.SetRotation (Character.cs:1527-1545), whose rope branch is
    ///      <c>Quaternion.LookRotation(-data.ropeClimbWorldNormal, data.ropeClimbWorldUp)</c> at
    ///      :1535. That call is reached from CharacterRagdoll.RotateCharacter (CharacterRagdoll.cs:
    ///      193-197) at the top of CharacterRagdoll.FixedUpdate (:159) and the rotation it writes is
    ///      restored from <c>rotationBefore</c> by ResetRotation (:199-202) later in the very same
    ///      physics step (:169). So the stale or zero normal perturbs an intermediate rotation used
    ///      for animation sampling within one step, not a persisted transform.
    ///   2. In this state the original is not producing a good rotation either - it is raising
    ///      ArgumentOutOfRangeException every physics step, so the assignment at :124 never completes
    ///      in vanilla on these frames either.
    ///
    /// The one visible artefact worth naming: with a zero <c>ropeClimbWorldNormal</c>,
    /// <c>Quaternion.LookRotation</c> returns identity and Unity may emit its
    /// "Look rotation viewing vector is zero" warning. That is a log line instead of an exception with
    /// a stack capture, on a frame that is broken in vanilla too.
    ///
    /// No counter, timer or latched flag is advanced in the skipped body, and Update() (which owns
    /// ropePercent, stamina and the stop RPCs) is a different method and stays untouched.
    ///
    /// Two of the five diagnostics are fallbacks rather than repairs, and both are unreachable in
    /// vanilla: <c>accessors-unavailable</c> and <c>character-unavailable</c> both return true, and
    /// FixedUpdate has already dereferenced the same <c>character.data</c> chain before it calls
    /// Climbing() (CharacterRopeHandling.cs:101-106), so a null character or data cannot get this far
    /// unless a third party calls Climbing() directly.
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
