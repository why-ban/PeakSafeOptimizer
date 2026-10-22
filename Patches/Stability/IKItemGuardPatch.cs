using System;
using System.Reflection;
using HarmonyLib;
using PeakSafeOptimizer.Diagnostics;
using UnityEngine;

namespace PeakSafeOptimizer.Patches.Stability
{
    /// <summary>
    /// Guards CharacterAnimations.ConfigureIK's item branch when the held item has no Hand_L or Hand_R child,
    /// which makes the CharacterItems helpers dereference null.
    ///
    /// Vanilla evaluates the left hand first: a missing Hand_L raises inside GetItemPosLeft before any write,
    /// so skipping the branch is equivalent, while a missing Hand_R raises inside GetItemPosRight only after
    /// both left writes have landed, so those two must be reproduced or the left IK target freezes. Cost is
    /// two extra Transform.Find per physics frame on the healthy path.
    /// </summary>
    internal static class IKItemGuardPatch
    {
        private static AccessTools.FieldRef<CharacterAnimations, Character> _character;
        private static Func<CharacterItems, Item, Vector3> _getItemPosLeft;
        private static Func<CharacterItems, Item, Quaternion> _getItemRotLeft;

        internal static void Initialize()
        {
            ResolveAndValidateTarget();

            FieldInfo characterField = StabilityPatchValidation.RequireInstanceField(
                typeof(CharacterAnimations), "character", typeof(Character));
            if (characterField.IsPublic)
                throw new InvalidOperationException(
                    "CharacterAnimations.character is no longer a private instance field");

            StabilityPatchValidation.RequireInstanceField(typeof(Character), "refs", typeof(Character.CharacterRefs));
            StabilityPatchValidation.RequireInstanceField(
                typeof(Character.CharacterRefs), "IKHandTargetLeft", typeof(Transform));
            StabilityPatchValidation.RequireInstanceField(typeof(Character), "data", typeof(CharacterData));
            StabilityPatchValidation.RequireInstanceProperty(
                typeof(CharacterData), "currentItem", typeof(Item));

            StabilityPatchValidation.RequireInstanceField(
                typeof(Character.CharacterRefs), "IKHandTargetRight", typeof(Transform));

            MethodInfo getItemPosLeft = StabilityPatchValidation.RequireInstanceMethod(
                typeof(CharacterItems), "GetItemPosLeft", typeof(Vector3), new[] { typeof(Item) },
                requirePublic: false);
            MethodInfo getItemRotLeft = StabilityPatchValidation.RequireInstanceMethod(
                typeof(CharacterItems), "GetItemRotLeft", typeof(Quaternion), new[] { typeof(Item) },
                requirePublic: false);
            StabilityPatchValidation.RequireInstanceMethod(
                typeof(CharacterItems), "GetItemPosRight", typeof(Vector3), new[] { typeof(Item) },
                requirePublic: false);
            StabilityPatchValidation.RequireInstanceMethod(
                typeof(CharacterItems), "GetItemRotRight", typeof(Quaternion), new[] { typeof(Item) },
                requirePublic: false);

            StabilityPatchValidation.RequireInstanceField(
                typeof(Character.CharacterRefs), "items", typeof(CharacterItems));
            StabilityPatchValidation.RequireInstanceField(
                typeof(Character.CharacterRefs), "animationItemTransform", typeof(Transform));

            PropertyInfo itemTransform = typeof(Item).GetProperty(
                "transform", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            MethodInfo itemTransformGetter = itemTransform?.GetGetMethod(true);
            if (itemTransform == null || itemTransform.PropertyType != typeof(Transform) ||
                itemTransform.GetIndexParameters().Length != 0 ||
                itemTransformGetter == null || itemTransformGetter.IsStatic)
            {
                throw new InvalidOperationException("Item.transform failed structural self-check");
            }

            AccessTools.FieldRef<CharacterAnimations, Character> character =
                AccessTools.FieldRefAccess<CharacterAnimations, Character>(characterField);
            Func<CharacterItems, Item, Vector3> posLeft =
                (Func<CharacterItems, Item, Vector3>)getItemPosLeft.CreateDelegate(
                    typeof(Func<CharacterItems, Item, Vector3>));
            Func<CharacterItems, Item, Quaternion> rotLeft =
                (Func<CharacterItems, Item, Quaternion>)getItemRotLeft.CreateDelegate(
                    typeof(Func<CharacterItems, Item, Quaternion>));
            if (character == null || posLeft == null || rotLeft == null)
                throw new InvalidOperationException("IKItemGuard accessor creation failed");
            _character = character;
            _getItemPosLeft = posLeft;
            _getItemRotLeft = rotLeft;
        }

        internal static MethodInfo ResolveAndValidateTarget()
        {
            return StabilityPatchValidation.RequireInstanceMethod(
                typeof(CharacterAnimations), "ConfigureIK", typeof(void), Type.EmptyTypes,
                requirePublic: true);
        }

        internal static void Reset()
        {
            _character = null;
            _getItemPosLeft = null;
            _getItemRotLeft = null;
        }

        internal static bool Prefix(CharacterAnimations __instance)
        {
            AccessTools.FieldRef<CharacterAnimations, Character> characterRef = _character;
            if (characterRef == null)
            {
                RateLimitedDiagnostics.Hit("IKItemGuard.accessors-unavailable");
                return true;
            }

            Character character = characterRef(__instance);
            if (character == null || character.refs == null || character.data == null)
                return true;

            Transform leftTarget = character.refs.IKHandTargetLeft;
            if (leftTarget == null)
                return true;

            Item item = character.data.currentItem;
            if (item == null)
                return true;

            Transform handLeft = item.transform.Find("Hand_L");
            if (handLeft == null)
            {
                RateLimitedDiagnostics.Hit("IKItemGuard.left-hand-node-missing");
                return false;
            }

            Transform handRight = item.transform.Find("Hand_R");
            if (handRight == null)
            {
                return ReproduceLeftHandWrites(character, item);
            }

            return true;
        }

        private static bool ReproduceLeftHandWrites(Character character, Item item)
        {
            Func<CharacterItems, Item, Vector3> posLeft = _getItemPosLeft;
            Func<CharacterItems, Item, Quaternion> rotLeft = _getItemRotLeft;
            Transform leftTarget = character.refs.IKHandTargetLeft;
            CharacterItems items = character.refs.items;
            Transform animationItem = character.refs.animationItemTransform;

            if (posLeft == null || rotLeft == null || leftTarget == null || items == null ||
                animationItem == null)
            {
                RateLimitedDiagnostics.Hit("IKItemGuard.left-hand-write-unavailable");
                return false;
            }

            try
            {
                // Vanilla order: GetItemPosLeft, GetItemRotLeft, set_rotation, SetPositionAndRotation
                Vector3 position = posLeft(items, item);
                Quaternion rotation = rotLeft(items, item);
                leftTarget.rotation = rotation;
                leftTarget.SetPositionAndRotation(position, rotation);
            }
            catch (Exception)
            {
                RateLimitedDiagnostics.Hit("IKItemGuard.left-hand-write-failed");
                return false;
            }

            RateLimitedDiagnostics.Hit("IKItemGuard.right-hand-node-missing");
            return false;
        }
    }
}
