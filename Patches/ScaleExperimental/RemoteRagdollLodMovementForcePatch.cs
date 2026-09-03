using System.Reflection;
using PeakSafeOptimizer.Patches.Scale;

namespace PeakSafeOptimizer.Patches.ScaleExperimental
{
    /// <summary>
    /// Opt-in: skips Bodypart.AddMovementForce(float) (audited source Bodypart.cs:291-298) for a
    /// distant remote character selected by <see cref="RemoteRagdollLod.ShouldDowngrade"/>. Like
    /// Animate it runs once per bodypart per physics step (CharacterMovement.cs:320).
    ///
    /// The original reads character.data.worldMovementInput_Lerp and adds an acceleration force in
    /// that direction. For a remote character that vector is locally simulated from replicated
    /// input, and the authoritative position still arrives through CharacterSyncer, which moves the
    /// rigs directly (CharacterSyncer.cs:166-187) and is not patched. Skipping the force therefore
    /// only removes locally predicted movement of a body that is already being pulled towards its
    /// networked position.
    ///
    /// Safety: this is a pure force-addition skip, so it can only reduce the energy in the ragdoll,
    /// and gravity, drag, useGravity syncing and the ApplyForces flush all still run in the same
    /// loop iteration. Bodypart.FollowRotation_Rotation losing its `if (!rig.isKinematic)` guard in
    /// this build does not touch that argument twice over: the guard was dead (its only caller,
    /// Bodypart.Animate, already returns early when rig.isKinematic), and this patch's target does not
    /// reach FollowRotation_Rotation at all - only Animate does. The kinematic filtering the skip
    /// relies on is the one inside Bodypart.AddForce, which is unchanged.
    /// </summary>
    internal static class RemoteRagdollLodMovementForcePatch
    {
        private static bool _acquired;

        internal static void Initialize()
        {
            ResolveAndValidateTarget();

            // Read by the original; verified so an unexpected build fails closed rather than
            // silently skipping a call whose inputs have changed meaning.
            ScaleValidation.RequireInstanceField(
                typeof(CharacterData), "worldMovementInput_Lerp", typeof(UnityEngine.Vector3));

            // Acquired last: it is the final statement here that can throw (it runs
            // RemoteRagdollLod.ValidateStructure and builds the shared accessors), and the only thing
            // after it is a plain bool assignment, so a failed Initialize never leaves a reference on
            // the shared helper.
            RemoteRagdollLod.Acquire();
            _acquired = true;
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
            if (!_acquired)
                return;
            _acquired = false;
            RemoteRagdollLod.Release();
        }

        internal static bool Prefix(Bodypart __instance, bool __runOriginal)
        {
            // BodypartMovementForceIdentity shares this target.
            if (!__runOriginal)
                return false;

            Character owner = RemoteRagdollLod.ResolveOwner(__instance);
            // No diagnostics: reachable once per bodypart per physics step.
            if (owner == null)
                return true;
            return !RemoteRagdollLod.ShouldDowngrade(owner);
        }
    }
}
