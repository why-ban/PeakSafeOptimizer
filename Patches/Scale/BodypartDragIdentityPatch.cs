using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace PeakSafeOptimizer.Patches.Scale
{
    /// <summary>
    /// Identity filter for Bodypart.Drag(float, bool) (audited source Bodypart.cs:214-225):
    ///
    ///   drag = ignoreRagdoll ? drag : Mathf.Lerp(1f, drag, character.data.currentRagdollControll);
    ///   if (!rig.isKinematic) { rig.linearVelocity *= drag; rig.angularVelocity *= drag; }
    ///
    /// Called once per bodypart per physics step from CharacterMovement.FixedUpdate
    /// (CharacterMovement.cs:321), i.e. players x ~100 parts x 50Hz. When the effective drag is
    /// exactly 1f, both writes are `v * 1f`, which is the IEEE identity for every finite value, for
    /// +/-Infinity, for NaN and for signed zero, so the four native Rigidbody accesses produce
    /// bit-identical state. This Prefix skips only that case.
    ///
    /// A fully ragdolled character has currentRagdollControll == 0 (Character.cs:644), and
    /// Mathf.Lerp(1f, drag, 0f) returns exactly 1f, so the win concentrates on downed/ragdolled
    /// players - the state a tumbleweed hit or a cactus stick produces in Mesa clusters.
    ///
    /// The identity survives the new CharacterMovement.AdjustDrag rescaling, and it is worth writing
    /// the arithmetic out because the answer is not obvious from the shape of the expression. Every
    /// producer of a drag argument now passes it through
    /// `AdjustDrag(d) = 1f - (1f - d) * (Time.fixedDeltaTime / (1f/60f))` (CharacterMovement.cs:384-388):
    /// the two serialized fields once in Start (:163-164, drag and rocketDrag) and the three extra-drag
    /// entry points on every call (ApplyExtraDrag :342, ApplyParasolDrag :355, ApplyGlider :368). Write
    /// k = fixedDeltaTime / (1/60). Then:
    ///   * AdjustDrag(1f) = 1 - (1 - 1) * k = 1 - 0 * k = 1 exactly, for every finite k, including
    ///     k != 1 - and `0f * k` is +0 for any finite k, so no rounding enters. The four
    ///     `ApplyExtraDrag(0f, ...)` / `Drag(0f, ignoreRagdoll: true)` call sites are unaffected
    ///     because 0 is not 1 either before or after the map. So the fixed point at 1 is preserved and
    ///     the set of arguments that reach this Prefix as an exact 1f is unchanged.
    ///   * The dominant path does not depend on that at all. With ignoreRagdoll false the effective
    ///     value is Mathf.Lerp(1f, drag, t) = 1 + (drag - 1) * Clamp01(t) (Mathf.cs:305-308), which is
    ///     exactly 1f whenever Clamp01(t) is +0 - i.e. for every t <= 0, which is where the
    ///     fully-ragdolled case lives - regardless of what `drag` holds. AdjustDrag can therefore
    ///     change how often the *non*-ragdoll path is an identity (at the default 50Hz step k = 1.2,
    ///     so a serialized 0.85 becomes 0.82; neither is 1, so nothing changes there either), but it
    ///     cannot move the ragdoll case off the identity.
    /// Two consequences worth naming. First, the k = 1 assumption is not made anywhere: this Prefix
    /// reads the already-adjusted argument the caller passes and compares the result to 1f, so it is
    /// correct for any fixed step. Second, the preimage of exactly 1f under AdjustDrag is {1} for
    /// every k != 0 - `1 - (1 - d) * k == 1` forces `(1 - d) * k == 0` - so no argument that used to
    /// miss the identity can start hitting it, and for d < 1 with k > 1 (k = 1.2 at the default 50Hz
    /// step) the map pushes the value further below 1, i.e. away from the identity. The one degenerate
    /// case, k == 0, would map every input to 1f, and skipping there is still exactly right: the
    /// original would have multiplied both velocities by that same 1f. So this patch cannot silently
    /// widen what it skips into something non-equivalent under any k.
    ///
    /// AdjustDrag is deliberately *not* asserted in Initialize, unlike every member the Prefix
    /// touches. The equivalence above does not depend on it existing: the Prefix is handed whatever
    /// float the caller computed and only ever skips an exact 1f. Asserting it would fail this patch
    /// closed on a build that merely dropped the rescaling again, which is a build where the patch is
    /// still correct.
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
