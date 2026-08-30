using System;
using System.Reflection;
using HarmonyLib;
using PeakSafeOptimizer.Diagnostics;
using UnityEngine;

namespace PeakSafeOptimizer.Patches.Stability
{
    /// <summary>
    /// Item-branch guard for CharacterAnimations.ConfigureIK() (audited source
    /// CharacterAnimations.cs:391-408), invoked every physics frame from
    /// CharacterRagdoll.FixedUpdate (CharacterRagdoll.cs:163).
    ///
    /// Root cause (confirmed in-game, 135 occurrences in one multiplayer session):
    /// ConfigureIK writes IKHandTargetRight/Left position and rotation from
    /// CharacterItems.GetItemPosRight/RotRight/PosLeft/RotLeft. Those helpers dereference
    /// item.transform.Find("Hand_R") / Find("Hand_L") (CharacterItems.cs:887, 893, 903, 913). When
    /// the current item is non-null but its prefab has no "Hand_R"/"Hand_L" child (an item that is
    /// not a two-handed holdable, or a prefab variant without those nodes), Find returns null and
    /// .localPosition throws NullReferenceException every physics frame during the equip transition.
    ///
    /// Why this guards ConfigureIK's item branch and not the GetItem* helpers: returning a synthetic
    /// default from four helpers would still write IKHandTarget positions/rotations, potentially
    /// snapping the hands for a frame. Skipping just the item-based hand-IK write for a frame is
    /// cleaner and preserves every other part of ConfigureIK. ConfigureIK is only 16 lines, so the
    /// guarded case is precisely bounded:
    ///
    ///   - If IKHandTargetLeft is null, the original outer guard is false and the body is a no-op;
    ///     this patch returns true to keep that behavior.
    ///   - If currentItem is null/destroyed, the original takes the else-if ReachIK() branch; this
    ///     patch returns true and does not touch it.
    ///   - Only when currentItem is present and one of Find("Hand_R") / Find("Hand_L") is null does
    ///     the original throw. The two cases are NOT symmetric and this patch must not treat them
    ///     as one; see below.
    ///
    /// The asymmetry, and why it matters. C# evaluates <c>a.position = f()</c> as receiver, then
    /// argument, then setter, and the four statements at CharacterAnimations.cs:397-400 run in source
    /// order. So:
    ///
    ///   - Find("Hand_R") == null: the original raises inside GetItemPosRight
    ///     (CharacterItems.cs:887) while evaluating the argument of :397. Not one write has landed.
    ///     Skipping the whole item branch is exactly equivalent, minus the exception, so this patch
    ///     counts IKItemGuard.right-hand-node-missing and returns false.
    ///   - Find("Hand_R") != null &amp;&amp; Find("Hand_L") == null: the original has already
    ///     completed BOTH right-hand writes - :397 position from GetItemPosRight
    ///     (CharacterItems.cs:885-889) and :398 rotation from GetItemRotRight
    ///     (CharacterItems.cs:891-899) - and only then raises inside GetItemPosLeft
    ///     (CharacterItems.cs:913) at :399. Returning false without those two writes would freeze the
    ///     right IK hand target for every physics frame the item is held, i.e. the right hand stops
    ///     following the item. That is the same class of regression as the 1.1.0 emote-wheel one:
    ///     skipping the writes that happen *before* the throw. So this patch reproduces :397 and
    ///     :398 through reflection delegates over the two internal helpers and only then returns
    ///     false, counting IKItemGuard.left-hand-node-missing.
    ///
    /// Prerequisites of that reproduction are checked individually rather than caught, because the
    /// original raises on exactly the same references: GetItemPosRight/GetItemRotRight dereference
    /// <c>character.refs.animationItemTransform</c> (CharacterItems.cs:888, 896-897), so when that is
    /// unavailable the original also raises at :397 with no write having landed - which is again the
    /// no-write case, reported as IKItemGuard.right-hand-write-unavailable and skipped. Only the two
    /// helper invocations themselves are additionally wrapped, since CharacterItems resolves
    /// <c>animationItemTransform</c> through its own <c>character</c> field (CharacterItems.cs:31,144)
    /// rather than the one this Prefix inspected; a failure there is reported as
    /// IKItemGuard.right-hand-write-failed and, matching the original, the position write may already
    /// have landed before the rotation write raised.
    ///
    /// ReachIK() is never called from this patch, so ReachHandPos and any other state it manages are
    /// left exactly as they were.
    ///
    /// Cost, stated plainly: this Prefix performs 2 extra <c>Transform.Find</c> calls per physics
    /// frame per character that is holding an item. They are pure overhead - when the Prefix returns
    /// true the original goes on to perform its own 4 Find calls (CharacterItems.cs:887, 893, 903,
    /// 913) regardless. The alternative, letting the vanilla NullReferenceException flood continue
    /// (135 occurrences in one session), costs an exception, a stack capture and a synchronous log
    /// write per frame, which is orders of magnitude more.
    /// </summary>
    internal static class IKItemGuardPatch
    {
        private static AccessTools.FieldRef<CharacterAnimations, Character> _character;
        private static Func<CharacterItems, Item, Vector3> _getItemPosRight;
        private static Func<CharacterItems, Item, Quaternion> _getItemRotRight;

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

            // The item-branch writes the right target too; verify its shape so a reshaped class
            // fails closed.
            StabilityPatchValidation.RequireInstanceField(
                typeof(Character.CharacterRefs), "IKHandTargetRight", typeof(Transform));

            // The four helpers the item branch calls (CharacterItems.cs:885-915). All four are
            // asserted even though only two are invoked here, because the branch model in the
            // docstring - which of the four raises first, and how many writes landed before it -
            // is a statement about all of them. They are internal, hence requirePublic: false.
            MethodInfo getItemPosRight = StabilityPatchValidation.RequireInstanceMethod(
                typeof(CharacterItems), "GetItemPosRight", typeof(Vector3), new[] { typeof(Item) },
                requirePublic: false);
            MethodInfo getItemRotRight = StabilityPatchValidation.RequireInstanceMethod(
                typeof(CharacterItems), "GetItemRotRight", typeof(Quaternion), new[] { typeof(Item) },
                requirePublic: false);
            StabilityPatchValidation.RequireInstanceMethod(
                typeof(CharacterItems), "GetItemPosLeft", typeof(Vector3), new[] { typeof(Item) },
                requirePublic: false);
            StabilityPatchValidation.RequireInstanceMethod(
                typeof(CharacterItems), "GetItemRotLeft", typeof(Quaternion), new[] { typeof(Item) },
                requirePublic: false);

            // The two links the reproduction walks: refs.items is the receiver of the helpers
            // (CharacterAnimations.cs:397-398) and refs.animationItemTransform is what they
            // dereference internally (CharacterItems.cs:888, 896-897).
            StabilityPatchValidation.RequireInstanceField(
                typeof(Character.CharacterRefs), "items", typeof(CharacterItems));
            StabilityPatchValidation.RequireInstanceField(
                typeof(Character.CharacterRefs), "animationItemTransform", typeof(Transform));

            // Item.transform is inherited from Component, which StabilityPatchValidation's
            // declared-only property lookup cannot express, so it is checked directly here - the
            // same hand-rolled shape check BinocularSunGuardPatch applies to Bodypart.transform.
            // DeclaredOnly is deliberately absent: the declaring type is Component, not Item.
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
            // The helpers are internal to Assembly-CSharp, so they cannot be called directly from
            // this assembly; open-instance delegates over the validated MethodInfos are the
            // allocation-free way in, created once here. Precedent in this repo:
            // Patches\Scale\AnimatedMouthMaterialPatch.cs:101-103 binds the same way to a private
            // method (AnimatedMouth.MicrophoneLevelMax, AnimatedMouth.cs:103).
            Func<CharacterItems, Item, Vector3> posRight =
                (Func<CharacterItems, Item, Vector3>)getItemPosRight.CreateDelegate(
                    typeof(Func<CharacterItems, Item, Vector3>));
            Func<CharacterItems, Item, Quaternion> rotRight =
                (Func<CharacterItems, Item, Quaternion>)getItemRotRight.CreateDelegate(
                    typeof(Func<CharacterItems, Item, Quaternion>));
            if (character == null || posRight == null || rotRight == null)
                throw new InvalidOperationException("IKItemGuard accessor creation failed");
            _character = character;
            _getItemPosRight = posRight;
            _getItemRotRight = rotRight;
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
            _getItemPosRight = null;
            _getItemRotRight = null;
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
            // Character is a UnityEngine.Object; `== null` here uses Unity's null/destroyed semantics.
            if (character == null || character.refs == null || character.data == null)
                return true;

            Transform leftTarget = character.refs.IKHandTargetLeft;
            if (leftTarget == null)
                return true; // Original outer guard is false; the body is a no-op.

            Item item = character.data.currentItem;
            if (item == null)
                return true; // Original takes the ReachIK() branch; leave it untouched.

            Transform handRight = item.transform.Find("Hand_R");
            if (handRight == null)
            {
                // Original raises while evaluating the argument of CharacterAnimations.cs:397
                // (CharacterItems.cs:887), before any of the four writes. Skipping the item branch
                // is equivalent to the original minus the exception.
                RateLimitedDiagnostics.Hit("IKItemGuard.right-hand-node-missing");
                return false;
            }

            Transform handLeft = item.transform.Find("Hand_L");
            if (handLeft == null)
            {
                // Original completes both right-hand writes (:397, :398) and only then raises at
                // :399 (CharacterItems.cs:913). Those two writes must be reproduced or the right IK
                // hand target freezes for as long as the item is held.
                return ReproduceRightHandWrites(character, item);
            }

            return true;
        }

        /// <summary>
        /// Reproduces CharacterAnimations.cs:397-398 for the case where Find("Hand_R") exists and
        /// Find("Hand_L") does not, then reports the skip. Always returns false.
        /// </summary>
        private static bool ReproduceRightHandWrites(Character character, Item item)
        {
            Func<CharacterItems, Item, Vector3> posRight = _getItemPosRight;
            Func<CharacterItems, Item, Quaternion> rotRight = _getItemRotRight;
            Transform rightTarget = character.refs.IKHandTargetRight;
            CharacterItems items = character.refs.items;
            Transform animationItem = character.refs.animationItemTransform;

            // Unity null semantics for the three game references, not plain reference comparison:
            // rightTarget is written through the native position/rotation setters; items is the
            // receiver of helpers whose bodies call native members (Transform.Find,
            // TransformPoint/TransformDirection); animationItem is dereferenced natively at
            // CharacterItems.cs:888 and 896-897. A destroyed-but-non-null shell would raise in the
            // original too, so treating it as unavailable matches vanilla rather than diverging.
            // The two delegate checks cover Reset() having run mid-flight; they share this reason
            // because the outcome is identical - the right-hand writes cannot be performed.
            if (posRight == null || rotRight == null || rightTarget == null || items == null ||
                animationItem == null)
            {
                // With animationItemTransform absent the original raises inside GetItemPosRight at
                // :397 with no write having landed, so this is the same no-write case as a missing
                // Hand_R node. A missing target or items reference means :397's receiver chain is
                // itself broken, which the original cannot survive either.
                RateLimitedDiagnostics.Hit("IKItemGuard.right-hand-write-unavailable");
                return false;
            }

            try
            {
                rightTarget.position = posRight(items, item);
                rightTarget.rotation = rotRight(items, item);
            }
            catch (Exception)
            {
                // CharacterItems reaches animationItemTransform through its own character field
                // (CharacterItems.cs:31,144), which is not necessarily the instance checked above,
                // so this is the one throw site that cannot be excluded by inspection. As in the
                // original, the position write may already have landed when the rotation write
                // raised; nothing is rolled back, because the original does not roll back either.
                RateLimitedDiagnostics.Hit("IKItemGuard.right-hand-write-failed");
                return false;
            }

            RateLimitedDiagnostics.Hit("IKItemGuard.left-hand-node-missing");
            return false;
        }
    }
}
