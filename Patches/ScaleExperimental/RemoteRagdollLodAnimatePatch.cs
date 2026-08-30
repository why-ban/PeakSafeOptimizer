using System.Reflection;
using HarmonyLib;
using PeakSafeOptimizer.Patches.Scale;

namespace PeakSafeOptimizer.Patches.ScaleExperimental
{
    /// <summary>
    /// Opt-in: skips Bodypart.Animate(float, float) (audited source Bodypart.cs:143-153) for a
    /// distant remote character selected by <see cref="RemoteRagdollLod.ShouldDowngrade"/>. Animate
    /// runs once per bodypart per physics step from CharacterMovement.FixedUpdate
    /// (CharacterMovement.cs:306), so for a 4-player lobby it is by far the highest-frequency
    /// ragdoll call in the game.
    ///
    /// What is skipped: FollowRotation_Joint (SetTargetRotationLocal on the ConfigurableJoint),
    /// FollowRotation_Rotation (AddTorque towards the animated pose) and FollowRotation_Position
    /// (AddForce towards the animated pose, plus the two reaction forces on jointParent and hip).
    /// All three only push the ragdoll towards its animation pose; skipping them leaves a limp
    /// ragdoll, which is exactly the intended visual trade.
    ///
    /// Why it cannot destabilise physics: every skipped call only adds force or torque, so skipping
    /// them strictly reduces the energy put into the ragdoll. Gravity, ToggleUseGravity, Drag and
    /// ApplyForces still run for the same part in the same loop iteration
    /// (CharacterMovement.cs:307-314), so gravity is still applied, useGravity still tracks
    /// currentRagdollControll, velocity is still damped every step and forcesToAdd is still flushed
    /// and zeroed. The kinematic branch of Animate (SnapToAnim) is unreachable for a downgraded
    /// character, because CharacterRagdoll.ToggleKinematic sets data.isKinecmatic together with
    /// every part's Rigidbody.isKinematic (CharacterRagdoll.cs:80-87) and data.isKinecmatic is one
    /// of the fallback conditions.
    /// </summary>
    internal static class RemoteRagdollLodAnimatePatch
    {
        private static bool _acquired;

        internal static void Initialize()
        {
            ResolveAndValidateTarget();
            // Acquire is the last statement here that can throw - it runs
            // RemoteRagdollLod.ValidateStructure and builds the shared accessors - and the only thing
            // after it is a plain bool assignment. This patch holds no accessors of its own, so there
            // is nothing else to order.
            RemoteRagdollLod.Acquire();
            _acquired = true;
        }

        internal static MethodInfo ResolveAndValidateTarget()
        {
            return ScaleValidation.RequireInstanceMethod(
                typeof(Bodypart),
                "Animate",
                typeof(void),
                new[] { typeof(float), typeof(float) },
                requirePublic: false);
        }

        internal static void Reset()
        {
            if (!_acquired)
                return;
            _acquired = false;
            RemoteRagdollLod.Release();
        }

        [HarmonyPriority(Priority.High)]
        internal static bool Prefix(Bodypart __instance, bool __runOriginal)
        {
            if (!__runOriginal)
                return false;

            Character owner = RemoteRagdollLod.ResolveOwner(__instance);
            // Bodypart.character is assigned in Bodypart.Start; before that, or on a stray part
            // without a Character parent, the original must run unchanged. No diagnostics: this is
            // reachable once per bodypart per physics step, so accounting it would cost more than the
            // work this patch removes.
            if (owner == null)
                return true;
            return !RemoteRagdollLod.ShouldDowngrade(owner);
        }
    }
}
