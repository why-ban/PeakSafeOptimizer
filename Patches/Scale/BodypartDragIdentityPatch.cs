using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace PeakSafeOptimizer.Patches.Scale
{
    /// <summary>
    /// Identity filter for Bodypart.Drag(float, bool) (audited source Bodypart.cs:213-224):
    ///
    ///   drag = ignoreRagdoll ? drag : Mathf.Lerp(1f, drag, character.data.currentRagdollControll);
    ///   if (!rig.isKinematic) { rig.linearVelocity *= drag; rig.angularVelocity *= drag; }
    ///
    /// Called once per bodypart per physics step from CharacterMovement.FixedUpdate
    /// (CharacterMovement.cs:313), i.e. players x ~100 parts x 50Hz. When the effective drag is
    /// exactly 1f, both writes are `v * 1f`, which is the IEEE identity for every finite value, for
    /// +/-Infinity, for NaN and for signed zero, so the four native Rigidbody accesses produce
    /// bit-identical state. This Prefix skips only that case.
    ///
    /// A fully ragdolled character has currentRagdollControll == 0 (Character.cs:647), and
    /// Mathf.Lerp(1f, drag, 0f) returns exactly 1f, so the win concentrates on downed/ragdolled
    /// players - the state a tumbleweed hit or a cactus stick produces in Mesa clusters.
    ///
    /// Anything that is not provably identity defers to the untouched original: a non-1f effective
    /// drag, a NaN control value (Lerp would not return 1f then anyway), and a missing accessor.
    /// The kinematic branch is irrelevant to the skip because the original also writes nothing
    /// there.
    /// </summary>
    internal static class BodypartDragIdentityPatch
    {
        private const string RigidbodyTypeName = "UnityEngine.Rigidbody";

        private static AccessTools.FieldRef<Bodypart, Character> _character;

        internal static void Initialize()
        {
            ResolveAndValidateTarget();

            FieldInfo characterField = ScaleValidation.RequireInstanceField(
                typeof(Bodypart), "character", typeof(Character));
            ScaleValidation.RequireInstanceField(typeof(Character), "data", typeof(CharacterData));
            ScaleValidation.RequireInstanceProperty(
                typeof(CharacterData), "currentRagdollControll", typeof(float));

            // Proof that the skipped work is exactly the two velocity multiplications: the field the
            // original writes through and the properties it multiplies must still be shaped as
            // audited. Nothing here is called by the patch itself.
            FieldInfo rigField = ScaleValidation.RequireInstanceFieldByTypeName(
                typeof(Bodypart), "rig", RigidbodyTypeName);
            ScaleValidation.RequireInstancePropertyByTypeName(
                rigField.FieldType, "isKinematic", typeof(bool).FullName, requireSetter: true);
            ScaleValidation.RequireInstancePropertyByTypeName(
                rigField.FieldType, "linearVelocity", "UnityEngine.Vector3", requireSetter: true);
            ScaleValidation.RequireInstancePropertyByTypeName(
                rigField.FieldType, "angularVelocity", "UnityEngine.Vector3", requireSetter: true);

            AccessTools.FieldRef<Bodypart, Character> character =
                AccessTools.FieldRefAccess<Bodypart, Character>(characterField);
            if (character == null)
                throw new InvalidOperationException("BodypartDragIdentity FieldRef creation failed");

            _character = character;
        }

        internal static MethodInfo ResolveAndValidateTarget()
        {
            return ScaleValidation.RequireInstanceMethod(
                typeof(Bodypart),
                "Drag",
                typeof(void),
                new[] { typeof(float), typeof(bool) },
                requirePublic: false);
        }

        internal static void Reset()
        {
            _character = null;
        }

        /// <summary>
        /// Hot path: once per bodypart per physics step for every character. No diagnostics on the
        /// identity or the non-identity path - both are reachable on every single call, so a
        /// RateLimitedDiagnostics hit would cost more than the work removed.
        /// </summary>
        internal static bool Prefix(Bodypart __instance, float drag, bool ignoreRagdoll)
        {
            AccessTools.FieldRef<Bodypart, Character> characterRef = _character;
            if (characterRef == null || __instance == null)
                return true;

            float effective = drag;
            if (!ignoreRagdoll)
            {
                Character owner = characterRef(__instance);
                CharacterData data = owner != null ? owner.data : null;
                if (data == null)
                {
                    // The original dereferences character.data here; let it keep its own behavior.
                    return true;
                }
                effective = Mathf.Lerp(1f, drag, data.currentRagdollControll);
            }

            // Only an exact 1f makes both multiplications identities.
            return effective != 1f;
        }
    }
}
