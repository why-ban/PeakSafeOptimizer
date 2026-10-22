using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace PeakSafeOptimizer.Patches.Scale
{
    /// <summary>
    /// Identity filter for Bodypart.Drag(float, bool). At an effective drag of exactly 1f both writes are
    /// v * 1f and cannot change the stored value, which is what makes the skip equivalent. Owner and data
    /// are reference tested because the original reads only managed members there. AdjustDrag is
    /// deliberately not asserted: 1 - (1 - d) * k == 1 forces d == 1, so it cannot widen the skip.
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

        internal static bool Prefix(Bodypart __instance, float drag, bool ignoreRagdoll)
        {
            AccessTools.FieldRef<Bodypart, Character> characterRef = _character;
            if (characterRef == null || __instance == null)
                return true;

            float effective = drag;
            if (!ignoreRagdoll)
            {
                Character owner = characterRef(__instance);
                if ((object)owner == null)
                    return true;
                CharacterData data = owner.data;
                if ((object)data == null)
                    return true;
                effective = Mathf.Lerp(1f, drag, data.currentRagdollControll);
            }

            return effective != 1f;
        }
    }
}
