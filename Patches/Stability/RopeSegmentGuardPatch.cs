using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using PeakSafeOptimizer.Diagnostics;
using UnityEngine;

namespace PeakSafeOptimizer.Patches.Stability
{
    /// <summary>
    /// Guards CharacterRopeHandling.Climbing when the held rope's segment list is empty, which makes
    /// GetSegmentFromPercent index past the end. A deferred frame first lands the one write the original
    /// completes before it raises, and the null-rope exit stays with the original so StopAnyClimbing runs.
    /// Guarding the caller rather than the API keeps the Transform contract its callers dereference at once.
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

            StabilityPatchValidation.RequireInstanceField(
                typeof(CharacterData), "ropeClimbWorldNormal", typeof(Vector3));
            StabilityPatchValidation.RequireInstanceField(
                typeof(CharacterData), "ropeClimbWorldUp", typeof(Vector3));
            StabilityPatchValidation.RequireInstanceField(
                typeof(CharacterData), "ropeClimbNormal", typeof(Vector3));
            StabilityPatchValidation.RequireInstanceField(
                typeof(Rope), "climbingAPI", typeof(RopeClimbingAPI));
            PropertyInfo ropeView = typeof(Rope).GetProperty(
                "photonView", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            MethodInfo ropeViewGetter = ropeView?.GetGetMethod(true);
            if (ropeView == null || ropeViewGetter == null || ropeViewGetter.IsStatic ||
                ropeView.GetIndexParameters().Length != 0 || ropeViewGetter.ReturnType.FullName != "Photon.Pun.PhotonView")
            {
                throw new InvalidOperationException("Rope.photonView failed structural self-check");
            }
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
                RateLimitedDiagnostics.Hit("RopeSegmentGuard.character-unavailable");
                return true;
            }

            Rope rope = data.heldRope;
            if (rope == null || rope.photonView == null)
            {
                return true;
            }

            if (rope.climbingAPI == null)
            {
                RateLimitedDiagnostics.Hit("RopeSegmentGuard.climbingAPI-null");
                return Defer(data);
            }

            List<Transform> segments = rope.GetRopeSegments();
            if (segments == null)
            {
                RateLimitedDiagnostics.Hit("RopeSegmentGuard.segments-null");
                return Defer(data);
            }
            if (segments.Count == 0)
            {
                RateLimitedDiagnostics.Hit("RopeSegmentGuard.segments-empty");
                return Defer(data);
            }

            return true;
        }

        private static bool Defer(CharacterData data)
        {
            data.ropeClimbWorldNormal = data.ropeClimbNormal;
            return false;
        }
    }
}
