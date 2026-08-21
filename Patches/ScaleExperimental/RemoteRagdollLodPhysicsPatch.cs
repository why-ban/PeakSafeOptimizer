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
    /// ResetRotation and per-part ResetTransform. SaveAdditionalTransformPositions is still called
    /// by this Prefix because CharacterMovement.FixedUpdate reads its height outputs for every
    /// character. This is the
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
    /// The target head/hip heights therefore continue to follow the current remote ragdoll pose,
    /// while the expensive animation graph and per-part pose reset remain skipped.
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
        private static Action<CharacterRagdoll> _setPhysicsMats;
        private static Action<CharacterRagdoll> _saveAdditionalTransformPositions;
        private static bool _acquired;

        internal static void Initialize()
        {
            ResolveAndValidateTarget();

            FieldInfo characterField = ScaleValidation.RequireInstanceField(
                typeof(CharacterRagdoll), "character", typeof(Character));
            MethodInfo saveAdditionalTransformPositions = ScaleValidation.RequireInstanceMethod(
                typeof(CharacterRagdoll), "SaveAdditionalTransformPositions", typeof(void),
                Type.EmptyTypes, requirePublic: false);
            MethodInfo setPhysicsMats = ScaleValidation.RequireInstanceMethod(
                typeof(CharacterRagdoll), "SetPhysicsMats", typeof(void), Type.EmptyTypes, requirePublic: false);
            AccessTools.FieldRef<CharacterRagdoll, Character> character =
                AccessTools.FieldRefAccess<CharacterRagdoll, Character>(characterField);
            Action<CharacterRagdoll> saveAdditional =
                ScaleExperimentalEmit.CreateDelegate<Action<CharacterRagdoll>>(saveAdditionalTransformPositions);
            Action<CharacterRagdoll> setPhysics =
                ScaleExperimentalEmit.CreateDelegate<Action<CharacterRagdoll>>(setPhysicsMats);
            if (character == null)
                throw new InvalidOperationException("RemoteRagdollLodPhysics FieldRef creation failed");
            if (saveAdditional == null || setPhysics == null)
                throw new InvalidOperationException("RemoteRagdollLodPhysics height accessor creation failed");

            // Acquired last: everything that can still throw has already run, so a failed
            // Initialize never leaves a reference behind on the shared helper.
            RemoteRagdollLod.Acquire();
            _acquired = true;
            _character = character;
            _setPhysicsMats = setPhysics;
            _saveAdditionalTransformPositions = saveAdditional;
        }

        internal static MethodInfo ResolveAndValidateTarget()
        {
            return ScaleValidation.RequireInstanceMethod(
                typeof(CharacterRagdoll), "FixedUpdate", typeof(void), Type.EmptyTypes, requirePublic: true);
        }

        internal static void Reset()
        {
            _character = null;
            _setPhysicsMats = null;
            _saveAdditionalTransformPositions = null;
            if (_acquired)
            {
                _acquired = false;
                RemoteRagdollLod.Release();
            }
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
            if (!RemoteRagdollLod.ShouldDowngrade(owner))
                return true;

            // CharacterMovement.FixedUpdate still reads targetHeadHeight and targetHipHeight for
            // every character. Keep the original producer running while leaving the expensive IK,
            // graph evaluation and per-part pose reset skipped.
            Action<CharacterRagdoll> saveAdditional = _saveAdditionalTransformPositions;
            if (saveAdditional == null)
                return true;
            try
            {
                _setPhysicsMats(__instance);
                saveAdditional(__instance);
                return false;
            }
            catch (Exception)
            {
                RateLimitedDiagnostics.Hit("RemoteRagdollLodPhysics.height-update-failed");
                return true;
            }
        }
    }
}
