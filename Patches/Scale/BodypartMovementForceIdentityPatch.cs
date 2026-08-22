using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace PeakSafeOptimizer.Patches.Scale
{
    /// <summary>
    /// Identity filter for Bodypart.AddMovementForce(float) (audited source Bodypart.cs:290-297):
    ///
    ///   if ((bool)character) {
    ///       Vector3 worldMovementInput_Lerp = character.data.worldMovementInput_Lerp;
    ///       AddForce(movementForce * worldMovementInput_Lerp, ForceMode.Acceleration);
    ///   }
    ///
    /// and AddForce (Bodypart.cs:261-271):
    ///
    ///   if (!rig.isKinematic) {
    ///       if (forceMode == ForceMode.Acceleration) force *= rig.mass;
    ///       forcesToAdd += force;
    ///   }
    ///
    /// Called once per bodypart per physics step for every character
    /// (CharacterMovement.cs:310), i.e. players x ~100 parts x 50Hz, and the two Rigidbody accesses
    /// inside AddForce are native calls. When the force is exactly the zero vector the whole call is
    /// <c>forcesToAdd += Vector3.zero</c>, which leaves every component bit-identical (adding +0 to a
    /// finite value, to an infinity or to a NaN all return the original value; a -0 component would
    /// become +0, which no consumer can distinguish because ApplyForces passes the vector straight to
    /// Rigidbody.AddForce). This Prefix skips exactly that case.
    ///
    /// The zero case is not an edge case, it is the cluster case. GetMovementForce returns 0f for
    /// anyone whose CheckMovement is false (CharacterMovement.cs:376-392), the caller multiplies it by
    /// currentRagdollControll, and worldMovementInput_Lerp converges to Vector3.zero for anyone not
    /// holding a movement key (Character.cs:1340-1342). A lobby standing around a campfire therefore
    /// hits the identity on every part of every character.
    ///
    /// Anything not provably identity - a non-zero force, a missing character or data, a missing
    /// Rigidbody - defers to the untouched original.
    /// </summary>
    internal static class BodypartMovementForceIdentityPatch
    {
        private static AccessTools.FieldRef<Bodypart, Character> _character;

        internal static void Initialize()
        {
            ResolveAndValidateTarget();

            FieldInfo characterField = ScaleValidation.RequireInstanceField(
                typeof(Bodypart), "character", typeof(Character));
            ScaleValidation.RequireInstanceField(typeof(Character), "data", typeof(CharacterData));
            ScaleValidation.RequireInstanceField(
                typeof(CharacterData), "worldMovementInput_Lerp", typeof(Vector3));

            // Proof that the skipped work is exactly the accumulate-into-forcesToAdd path, and that
            // the flush which makes a skipped accumulation harmless is still there. Neither is called
            // by this patch.
            ScaleValidation.RequireInstanceMethod(
                typeof(Bodypart), "AddForce", typeof(void), new[] { typeof(Vector3), typeof(ForceMode) }, true);
            ScaleValidation.RequireInstanceMethod(
                typeof(Bodypart), "ApplyForces", typeof(void), Type.EmptyTypes, false);
            ScaleValidation.RequireInstanceField(typeof(Bodypart), "forcesToAdd", typeof(Vector3));
            ScaleValidation.RequireInstanceFieldByTypeName(
                typeof(Bodypart), "rig", "UnityEngine.Rigidbody");

            AccessTools.FieldRef<Bodypart, Character> character =
                AccessTools.FieldRefAccess<Bodypart, Character>(characterField);
            if (character == null)
                throw new InvalidOperationException("BodypartMovementForceIdentity FieldRef creation failed");

            _character = character;
        }

        internal static MethodInfo ResolveAndValidateTarget()
        {
            return ScaleValidation.RequireInstanceMethod(
                typeof(Bodypart),
                "AddMovementForce",
                typeof(void),
                new[] { typeof(float) },
                requirePublic: false);
        }

        internal static void Reset()
        {
            _character = null;
        }

        /// <summary>
        /// Hot path: once per bodypart per physics step. No diagnostics, because both outcomes are
        /// reachable on every call and a Hit would cost more than the work removed.
        /// </summary>
        internal static bool Prefix(Bodypart __instance, float movementForce, bool __runOriginal)
        {
            // RemoteRagdollLodMovementForce shares this target and may already have skipped the call.
            if (!__runOriginal)
                return false;

            AccessTools.FieldRef<Bodypart, Character> characterRef = _character;
            if (characterRef == null || __instance == null)
                return true;

            Character owner = characterRef(__instance);
            if (owner == null)
            {
                // Vanilla's own (bool)character test also skips everything here, so returning false
                // would be equivalent - but the original is the cheapest correct answer.
                return true;
            }
            CharacterData data = owner.data;
            if (data == null)
                return true;

            Vector3 input = data.worldMovementInput_Lerp;
            Vector3 force = movementForce * input;
            // Only an exactly zero component is an identity for `forcesToAdd += force * rig.mass`.
            // NaN and infinity both fail these comparisons and therefore fall through to the original,
            // which is what keeps a degenerate input observable instead of silently swallowed. (Unity
            // rejects a non-finite Rigidbody.mass, so a zero force cannot become non-zero in the
            // multiplication AddForce performs.)
            return !(force.x == 0f && force.y == 0f && force.z == 0f);
        }
    }
}
