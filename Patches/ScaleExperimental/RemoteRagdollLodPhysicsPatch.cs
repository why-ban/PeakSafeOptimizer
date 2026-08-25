using System;
using System.Reflection;
using HarmonyLib;
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
    /// Why SaveAdditionalTransformPositions must NOT be called here, even though
    /// CharacterMovement.FixedUpdate reads its outputs: it reads the *animated* pose off the
    /// bodypart transforms (CharacterRagdoll.cs:204-215), and in the audited order it runs after
    /// the graph evaluation and before ResetTransform (CharacterRagdoll.cs:162-173). With the
    /// evaluation skipped, the transforms still hold the physics pose that the previous tick's
    /// ResetTransform wrote, so calling it would assign the character's *current sagging* height to
    /// data.targetHeadHeight / targetHipHeight - the very values Stand() uses as its target
    /// (CharacterMovement.cs:689-698) and RaycastGroundCheck uses as its ray length (:877).
    /// Target converging on current makes both InverseLerps zero, the standing force goes to zero,
    /// and the character sags further, which lowers the target again. Earlier versions of this
    /// patch did call it and were wrong. Leaving both heights at the value from the last real
    /// evaluation keeps them stale but semantically correct, which is the same choice
    /// RemoteClusterAnimationThrottle makes for the same reason.
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
        private static bool _acquired;

        internal static void Initialize()
        {
            ResolveAndValidateTarget();

            FieldInfo characterField = ScaleValidation.RequireInstanceField(
                typeof(CharacterRagdoll), "character", typeof(Character));
            // Proof that the two members the safety argument reasons about still exist. Neither is
            // called by this patch: the whole method body is skipped.
            ScaleValidation.RequireInstanceMethod(
                typeof(CharacterRagdoll), "SaveAdditionalTransformPositions", typeof(void),
                Type.EmptyTypes, requirePublic: false);
            ScaleValidation.RequireInstanceMethod(
                typeof(CharacterRagdoll), "SetPhysicsMats", typeof(void), Type.EmptyTypes, requirePublic: false);
            ScaleValidation.RequireInstanceField(typeof(CharacterData), "targetHeadHeight", typeof(float));
            ScaleValidation.RequireInstanceField(typeof(CharacterData), "targetHipHeight", typeof(float));

            AccessTools.FieldRef<CharacterRagdoll, Character> character =
                AccessTools.FieldRefAccess<CharacterRagdoll, Character>(characterField);
            if (character == null)
                throw new InvalidOperationException("RemoteRagdollLodPhysics FieldRef creation failed");

            // Acquired last: everything that can still throw has already run, so a failed
            // Initialize never leaves a reference behind on the shared helper.
            RemoteRagdollLod.Acquire();
            _acquired = true;
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
            if (_acquired)
            {
                _acquired = false;
                RemoteRagdollLod.Release();
            }
        }

        internal static bool Prefix(CharacterRagdoll __instance, bool __runOriginal)
        {
            // Another Prefix on this method may already have decided to skip it.
            if (!__runOriginal)
                return false;

            AccessTools.FieldRef<CharacterRagdoll, Character> characterRef = _character;
            if (characterRef == null)
                return true;

            Character owner = characterRef(__instance);
            if (owner == null)
                return true;
            return !RemoteRagdollLod.ShouldDowngrade(owner);
        }
    }
}
