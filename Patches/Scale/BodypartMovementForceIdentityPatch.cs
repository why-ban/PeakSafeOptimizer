using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace PeakSafeOptimizer.Patches.Scale
{
    /// <summary>
    /// Identity filter for Bodypart.AddMovementForce(float). A zero force makes the whole call
    /// forcesToAdd += Vector3.zero, which only rewrites -0 components as +0 and no consumer can see
    /// that because ApplyForces hands the vector straight to Rigidbody.AddForce.
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

            ScaleValidation.RequireInstanceMethod(
                typeof(Bodypart), "AddForce", typeof(void), new[] { typeof(Vector3), typeof(ForceMode) }, true);
            ScaleValidation.RequireInstanceMethod(
                typeof(Bodypart), "ApplyForces", typeof(void), Type.EmptyTypes, false);
            ScaleValidation.RequireInstanceField(typeof(Bodypart), "forcesToAdd", typeof(Vector3));
            FieldInfo rigField = ScaleValidation.RequireInstanceFieldByTypeName(
                typeof(Bodypart), "rig", "UnityEngine.Rigidbody");
            ScaleValidation.RequireInstancePropertyByTypeName(
                rigField.FieldType, "isKinematic", typeof(bool).FullName, requireSetter: false);
            ScaleValidation.RequireInstancePropertyByTypeName(
                rigField.FieldType, "mass", typeof(float).FullName, requireSetter: false);

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

        internal static bool Prefix(Bodypart __instance, float movementForce, bool __runOriginal)
        {
            // RemoteRagdollLodMovementForce shares this target and may already have skipped the call
            if (!__runOriginal)
                return false;

            AccessTools.FieldRef<Bodypart, Character> characterRef = _character;
            if (characterRef == null || __instance == null)
                return true;

            Character owner = characterRef(__instance);
            if (owner == null)
            {
                return true;
            }
            CharacterData data = owner.data;
            if (data == null)
                return true;

            Vector3 input = data.worldMovementInput_Lerp;
            Vector3 force = movementForce * input;
            return !(force.x == 0f && force.y == 0f && force.z == 0f);
        }
    }
}
