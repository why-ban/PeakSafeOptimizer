using System;
using System.Reflection;
using HarmonyLib;
using PeakSafeOptimizer.Patches.Scale;

namespace PeakSafeOptimizer.Patches.ScaleExperimental
{
    /// <summary>
    /// Skips CharacterRagdoll.FixedUpdate for a character ShouldDowngrade selects: animation graph and IK
    /// evaluation, SaveAnimationData, SaveAdditionalTransformPositions, ResetRotation, ResetTransform.
    /// SetPhysicsMats is no skipped work, vanilla calls it only when character.IsLocal. The transform
    /// positions must stay skipped: they read the animated pose and would write the sagging height into Stand.
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
            ScaleValidation.RequireInstanceMethod(
                typeof(CharacterRagdoll), "SaveAdditionalTransformPositions", typeof(void),
                Type.EmptyTypes, requirePublic: false);
            ScaleValidation.RequireInstanceMethod(
                typeof(CharacterRagdoll), "SetPhysicsMats", typeof(void), Type.EmptyTypes, requirePublic: false);
            ScaleValidation.RequireInstanceProperty(typeof(Character), "IsLocal", typeof(bool));
            ScaleValidation.RequireInstanceField(typeof(CharacterData), "targetHeadHeight", typeof(float));
            ScaleValidation.RequireInstanceField(typeof(CharacterData), "targetHipHeight", typeof(float));

            AccessTools.FieldRef<CharacterRagdoll, Character> character =
                AccessTools.FieldRefAccess<CharacterRagdoll, Character>(characterField);
            if (character == null)
                throw new InvalidOperationException("RemoteRagdollLodPhysics FieldRef creation failed");

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
