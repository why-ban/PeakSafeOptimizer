using System;
using System.Reflection;
using HarmonyLib;
using PeakSafeOptimizer.Diagnostics;
using PeakSafeOptimizer.Patches.Scale;

namespace PeakSafeOptimizer.Patches.ScaleExperimental
{
    /// <summary>
    /// Opt-in: skips CharacterRagdoll.FixedUpdate() (audited source CharacterRagdoll.cs:151-174)
    /// for a distant remote character selected by <see cref="RemoteRagdollLod.ShouldDowngrade"/>.
    ///
    /// What is skipped: SetPhysicsMats, RotateCharacter, the IK rig SyncLayers/Evaluate, the
    /// animator PlayableGraph.Evaluate, ConfigureIK, per-part SaveAnimationData,
    /// SaveAdditionalTransformPositions, ResetRotation and per-part ResetTransform. This is the
    /// single most expensive per-character physics-step cost in the game and it is purely
    /// animation-pose work: none of it applies a force, and none of it moves a Rigidbody. The
    /// visible effect is that the distant character stops playing its animation pose and hangs in
    /// its last pose while physics keeps carrying it.
    ///
    /// Why it is safe: the rig transforms only stop being re-synced from the animation graph, the
    /// Rigidbodies themselves are untouched, and CharacterMovement.FixedUpdate keeps calling
    /// Gravity/ToggleUseGravity/Drag/ApplyForces on every part, so nothing floats, velocity keeps
    /// converging and forcesToAdd keeps being flushed. Collider state, kinematic state and the
    /// CharacterSyncer network interpolation are not involved at all.
    ///
    /// One consequence is intentional: with FixedUpdate skipped, this character's
    /// data.targetHeadHeight / data.targetHipHeight (written by SaveAdditionalTransformPositions)
    /// stop updating. Both are only consumed by the owning client's own stand/ground logic
    /// (CharacterMovement.cs:692-694 and :877), which does not run for a remote character here,
    /// so no local decision reads a stale value.
    ///
    /// Likewise SetPhysicsMats stops re-asserting the collider friction material while downgraded.
    /// That is safe because the material selection is driven by currentRagdollControll
    /// (CharacterRagdoll.cs:89-96) and a value below 0.9 is one of the shared fallback conditions:
    /// the moment a downgraded character stops being fully controlled, this Prefix lets the original
    /// run again and the material is re-applied on that same physics step.
    /// </summary>
    internal static class RemoteRagdollLodPhysicsPatch
    {
        private static AccessTools.FieldRef<CharacterRagdoll, Character> _character;

        internal static void Initialize()
        {
            ResolveAndValidateTarget();

            FieldInfo characterField = ScaleValidation.RequireInstanceField(
                typeof(CharacterRagdoll), "character", typeof(Character));
            AccessTools.FieldRef<CharacterRagdoll, Character> character =
                AccessTools.FieldRefAccess<CharacterRagdoll, Character>(characterField);
            if (character == null)
                throw new InvalidOperationException("RemoteRagdollLodPhysics FieldRef creation failed");

            // Acquired last: everything that can still throw has already run, so a failed
            // Initialize never leaves a reference behind on the shared helper.
            RemoteRagdollLod.Acquire();
            _character = character;
        }

        internal static MethodInfo ResolveAndValidateTarget()
        {
            return ScaleValidation.RequireInstanceMethod(
                typeof(CharacterRagdoll), "FixedUpdate", typeof(void), Type.EmptyTypes, requirePublic: true);
        }

        internal static void Reset()
        {
            _character = null;
            RemoteRagdollLod.Release();
        }

        internal static bool Prefix(CharacterRagdoll __instance)
        {
            AccessTools.FieldRef<CharacterRagdoll, Character> characterRef = _character;
            if (characterRef == null)
            {
                RateLimitedDiagnostics.Hit("RemoteRagdollLodPhysics.accessor-unavailable");
                return true;
            }

            Character owner = characterRef(__instance);
            if (owner == null)
            {
                RateLimitedDiagnostics.Hit("RemoteRagdollLodPhysics.character-null");
                return true;
            }
            return !RemoteRagdollLod.ShouldDowngrade(owner);
        }
    }
}
