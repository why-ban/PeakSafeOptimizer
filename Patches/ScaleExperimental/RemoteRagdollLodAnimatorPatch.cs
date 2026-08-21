using System;
using System.Reflection;
using HarmonyLib;
using PeakSafeOptimizer.Diagnostics;
using PeakSafeOptimizer.Patches.Scale;

namespace PeakSafeOptimizer.Patches.ScaleExperimental
{
    /// <summary>
    /// Opt-in: skips CharacterAnimations.Update() (audited source CharacterAnimations.cs:160-286)
    /// for a distant remote character selected by <see cref="RemoteRagdollLod.ShouldDowngrade"/>.
    /// The method writes roughly thirty Animator parameters per character per frame plus a
    /// Matrix4x4 look computation, and every one of those writes only feeds the animation pose.
    ///
    /// Why no physics can break: this method applies no force and moves no Rigidbody. Its only
    /// non-Animator writes are helper-transform and bookkeeping values:
    ///   refs.animationPositionTransform.position (a helper transform; its only other consumer is
    ///     Action_Torch's particle simulation space)
    ///   throwTime countdown, data.myersDistance reset to 1000f, data.inWater reset to 1f
    /// None of these is read by a force path. data.myersDistance and data.inWater are only consumed
    /// as Animator parameters (CharacterAnimations.cs:249 and :284), so leaving them stale for a
    /// distant character has no gameplay effect on this client.
    ///
    /// Two effects are accepted by design, both cosmetic and both on a character that is at least
    /// the configured enter distance away: its Animator parameters freeze at their last values, and
    /// data.isInWater (which reads back the InWater Animator parameter) can lag, which can make its
    /// distant footstep/water SFX variant stale. HandleIK is additionally called every frame from
    /// CharacterAnimations.LateUpdate (CharacterAnimations.cs:335-338), which is not patched, so IK
    /// weights keep being maintained even while Update is skipped.
    /// </summary>
    internal static class RemoteRagdollLodAnimatorPatch
    {
        private static AccessTools.FieldRef<CharacterAnimations, Character> _character;

        internal static void Initialize()
        {
            ResolveAndValidateTarget();

            FieldInfo characterField = ScaleValidation.RequireInstanceField(
                typeof(CharacterAnimations), "character", typeof(Character));
            // Proof that HandleIK is still maintained outside Update, which the safety argument
            // above relies on.
            ScaleValidation.RequireInstanceMethod(
                typeof(CharacterAnimations), "LateUpdate", typeof(void), Type.EmptyTypes, false);
            ScaleValidation.RequireInstanceMethod(
                typeof(CharacterAnimations), "HandleIK", typeof(void), Type.EmptyTypes, false);

            AccessTools.FieldRef<CharacterAnimations, Character> character =
                AccessTools.FieldRefAccess<CharacterAnimations, Character>(characterField);
            if (character == null)
                throw new InvalidOperationException("RemoteRagdollLodAnimator FieldRef creation failed");

            // Acquired last so a failed Initialize never leaves a reference on the shared helper.
            RemoteRagdollLod.Acquire();
            _character = character;
        }

        internal static MethodInfo ResolveAndValidateTarget()
        {
            return ScaleValidation.RequireInstanceMethod(
                typeof(CharacterAnimations), "Update", typeof(void), Type.EmptyTypes, requirePublic: false);
        }

        internal static void Reset()
        {
            _character = null;
            RemoteRagdollLod.Release();
        }

        internal static bool Prefix(CharacterAnimations __instance)
        {
            AccessTools.FieldRef<CharacterAnimations, Character> characterRef = _character;
            if (characterRef == null)
            {
                RateLimitedDiagnostics.Hit("RemoteRagdollLodAnimator.accessor-unavailable");
                return true;
            }

            Character owner = characterRef(__instance);
            if (owner == null)
            {
                RateLimitedDiagnostics.Hit("RemoteRagdollLodAnimator.character-null");
                return true;
            }
            return !RemoteRagdollLod.ShouldDowngrade(owner);
        }
    }
}
