using System;
using System.Reflection;
using HarmonyLib;
using PeakSafeOptimizer.Diagnostics;
using UnityEngine;

namespace PeakSafeOptimizer.Patches.Stability
{
    /// <summary>
    /// Item-branch guard for CharacterAnimations.ConfigureIK() (audited source
    /// CharacterAnimations.cs:390-408), invoked every physics frame from
    /// CharacterRagdoll.FixedUpdate.
    ///
    /// Root cause (confirmed in-game, 135 occurrences in one multiplayer session):
    /// ConfigureIK writes IKHandTargetLeft/Right position and rotation from
    /// CharacterItems.GetItemPosLeft/RotLeft/PosRight/RotRight. Those helpers dereference
    /// item.transform.Find("Hand_L") / Find("Hand_R") (CharacterItems.cs:879-907). When
    /// the current item is non-null but its prefab has no "Hand_L"/"Hand_R" child (an item that is
    /// not a two-handed holdable, or a prefab variant without those nodes), Find returns null and
    /// .localPosition throws NullReferenceException every physics frame during the equip transition.
    ///
    /// Why this guards ConfigureIK's item branch and not the GetItem* helpers: returning a synthetic
    /// default from four helpers would still write IKHandTarget positions/rotations, potentially
    /// snapping the hands for a frame. Skipping just the item-based hand-IK write for a frame is
    /// cleaner and preserves every other part of ConfigureIK. ConfigureIK is only a handful of lines,
    /// so the guarded case is precisely bounded:
    ///
    ///   - If IKHandTargetLeft is null, the original outer guard is false and the body is a no-op;
    ///     this patch returns true to keep that behavior.
    ///   - If currentItem is null/destroyed, the original takes the else-if ReachIK() branch; this
    ///     patch returns true and does not touch it.
    ///   - Only when currentItem is present and one of Find("Hand_L") / Find("Hand_R") is null does
    ///     the original throw. The two cases are NOT symmetric and this patch must not treat them
    ///     as one; see below.
    ///
    /// The asymmetry, and why it matters. The audited body evaluates the LEFT hand first and the
    /// RIGHT hand last, and that order is load-bearing:
    ///
    ///     Transform iKHandTargetLeft = character.refs.IKHandTargetLeft;
    ///     Vector3 itemPosLeft        = character.refs.items.GetItemPosLeft(currentItem);
    ///     Quaternion rotation        = character.refs.IKHandTargetLeft.rotation
    ///                                = character.refs.items.GetItemRotLeft(currentItem);
    ///     iKHandTargetLeft.SetPositionAndRotation(itemPosLeft, rotation);
    ///     character.refs.IKHandTargetRight.SetPositionAndRotation(
    ///         character.refs.items.GetItemPosRight(currentItem),
    ///         character.refs.items.GetItemRotRight(currentItem));
    ///
    /// (Confirmed by IL: GetItemPosLeft at IL_0063, GetItemRotLeft at IL_0098 with the dup/stloc
    /// that also feeds the rotation setter, Transform::set_rotation at IL_009f,
    /// SetPositionAndRotation at IL_00a5, then GetItemPosRight/GetItemRotRight and the right-hand
    /// SetPositionAndRotation at IL_00da/IL_00ff/IL_0104.) C# evaluates `a.P = f()` as receiver,
    /// then argument, then setter, so:
    ///
    ///   - Find("Hand_L") == null: the original raises inside GetItemPosLeft at IL_0063 while
    ///     evaluating the item branch's first statement. Not one write has landed. Skipping the
    ///     whole item branch is exactly equivalent, minus the exception, so this patch counts
    ///     IKItemGuard.left-hand-node-missing and returns false.
    ///   - Find("Hand_L") != null &amp;&amp; Find("Hand_R") == null: the original has already completed
    ///     BOTH left-hand writes - the rotation setter at IL_009f (from GetItemRotLeft) and
    ///     SetPositionAndRotation at IL_00a5 (from GetItemPosLeft) - and only then raises inside
    ///     GetItemPosRight at IL_00da. Returning false without those two writes would freeze the left
    ///     IK hand target for every physics frame the item is held, i.e. the left hand stops following
    ///     the item. That is the same class of regression as the 1.1.0 emote-wheel one: skipping the
    ///     writes that happen *before* the throw. So this patch reproduces the two left-hand writes
    ///     through reflection delegates over the two internal helpers and only then returns false,
    ///     counting IKItemGuard.right-hand-node-missing.
    ///
    /// History of this asymmetry, because it has been wrong once. The per-side role assignment was
    /// originally written against the 1.20 body, which evaluated the RIGHT hand first and the left
    /// hand last, so it reproduced the right-hand pair and treated a missing Hand_L as the zero-write
    /// case. The current build (and the audited c88cc526 baseline) reversed that order. The
    /// bookkeeping was not flipped with it, so this patch reproduced the wrong pair for two releases:
    /// a missing Hand_L froze the left IK target instead of the right, and a missing Hand_R performed
    /// two right-hand writes the original never reaches. Both directions are fixed here.
    ///
    /// Prerequisites of the reproduction are checked individually rather than caught, because the
    /// original raises on exactly the same references: GetItemPosLeft/GetItemRotLeft dereference
    /// <c>character.refs.animationItemTransform</c> (CharacterItems.cs:881, 897-901), so when that is
    /// unavailable the original also raises at IL_0063 with no write having landed - which is again the
    /// no-write case, reported as IKItemGuard.left-hand-write-unavailable and skipped. Only the two
    /// helper invocations themselves are additionally wrapped, since CharacterItems resolves
    /// <c>animationItemTransform</c> through its own <c>character</c> field rather than the one this
    /// Prefix inspected; a failure there is reported as IKItemGuard.left-hand-write-failed and,
    /// matching the original, the rotation write may already have landed before the position write
    /// raised.
    ///
    /// ReachIK() is never called from this patch, so ReachHandPos and any other state it manages are
    /// left exactly as they were.
    ///
    /// Cost, stated plainly: this Prefix performs 2 extra <c>Transform.Find</c> calls per physics
    /// frame per character that is holding an item. They are pure overhead - when the Prefix returns
    /// true the original goes on to perform its own 4 Find calls regardless. The alternative, letting
    /// the vanilla NullReferenceException flood continue (135 occurrences in one session), costs an
    /// exception, a stack capture and a synchronous log write per frame, which is orders of magnitude
    /// more.
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

            // The item-branch writes the right target too; verify its shape so a reshaped class
            // fails closed.
            StabilityPatchValidation.RequireInstanceField(
                typeof(Character.CharacterRefs), "IKHandTargetRight", typeof(Transform));

            // The four helpers the item branch calls. All four are asserted even though only two are
            // invoked here, because the branch model in the docstring - which of the four raises
            // first, and how many writes landed before it - is a statement about all of them. They
            // are internal, hence requirePublic: false.
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

            // The two links the reproduction walks: refs.items is the receiver of the helpers and
            // refs.animationItemTransform is what they dereference internally (CharacterItems.cs:881,
            // 897-901).
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
            // Patches\Scale\AnimatedMouthMaterialPatch.cs binds the same way to a private method
            // (AnimatedMouth.MicrophoneLevelMax).
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
            // Character is a UnityEngine.Object; `== null` here uses Unity's null/destroyed semantics.
            if (character == null || character.refs == null || character.data == null)
                return true;

            Transform leftTarget = character.refs.IKHandTargetLeft;
            if (leftTarget == null)
                return true; // Original outer guard is false; the body is a no-op.

            Item item = character.data.currentItem;
            if (item == null)
                return true; // Original takes the ReachIK() branch; leave it untouched.

            // The left hand is evaluated first in the audited body, so a missing Hand_L is the
            // zero-write case and a missing Hand_R is the case with left-hand writes already landed.
            Transform handLeft = item.transform.Find("Hand_L");
            if (handLeft == null)
            {
                // Original raises inside GetItemPosLeft while evaluating the item branch's first
                // statement, before any of the four writes. Skipping the item branch is equivalent
                // to the original minus the exception.
                RateLimitedDiagnostics.Hit("IKItemGuard.left-hand-node-missing");
                return false;
            }

            Transform handRight = item.transform.Find("Hand_R");
            if (handRight == null)
            {
                // Original completes both left-hand writes - the rotation setter fed by
                // GetItemRotLeft, then SetPositionAndRotation fed by GetItemPosLeft - and only then
                // raises inside GetItemPosRight. Those two writes must be reproduced or the left IK
                // hand target freezes for as long as the item is held.
                return ReproduceLeftHandWrites(character, item);
            }

            return true;
        }

        /// <summary>
        /// Reproduces the two left-hand writes for the case where Find("Hand_L") exists and
        /// Find("Hand_R") does not, then reports the skip. Always returns false.
        /// </summary>
        private static bool ReproduceLeftHandWrites(Character character, Item item)
        {
            Func<CharacterItems, Item, Vector3> posLeft = _getItemPosLeft;
            Func<CharacterItems, Item, Quaternion> rotLeft = _getItemRotLeft;
            Transform leftTarget = character.refs.IKHandTargetLeft;
            CharacterItems items = character.refs.items;
            Transform animationItem = character.refs.animationItemTransform;

            // Unity null semantics for the three game references, not plain reference comparison:
            // leftTarget is written through the native rotation/position setters; items is the
            // receiver of helpers whose bodies call native members (Transform.Find,
            // TransformPoint/TransformDirection); animationItem is dereferenced natively inside both
            // helpers. A destroyed-but-non-null shell would raise in the original too, so treating it
            // as unavailable matches vanilla rather than diverging. The two delegate checks cover
            // Reset() having run mid-flight; they share this reason because the outcome is identical -
            // the left-hand writes cannot be performed.
            if (posLeft == null || rotLeft == null || leftTarget == null || items == null ||
                animationItem == null)
            {
                // With animationItemTransform absent the original raises inside GetItemPosLeft, which
                // is the first statement of the item branch, so this is the same no-write case as a
                // missing Hand_L node. A missing target or items reference means the statement's
                // receiver chain is itself broken, which the original cannot survive either.
                RateLimitedDiagnostics.Hit("IKItemGuard.left-hand-write-unavailable");
                return false;
            }

            try
            {
                // Original evaluation order, reproduced: position is computed first (via
                // GetItemPosLeft), then the rotation (via GetItemRotLeft, whose result also feeds the
                // rotation setter), then the rotation setter, then SetPositionAndRotation.
                Vector3 position = posLeft(items, item);
                Quaternion rotation = rotLeft(items, item);
                leftTarget.rotation = rotation;
                leftTarget.SetPositionAndRotation(position, rotation);
            }
            catch (Exception)
            {
                // CharacterItems reaches animationItemTransform through its own character field,
                // which is not necessarily the instance checked above, so this is the one throw site
                // that cannot be excluded by inspection. As in the original, the rotation write may
                // already have landed when the position write raised; nothing is rolled back, because
                // the original does not roll back either.
                RateLimitedDiagnostics.Hit("IKItemGuard.left-hand-write-failed");
                return false;
            }

            RateLimitedDiagnostics.Hit("IKItemGuard.right-hand-node-missing");
            return false;
        }
    }
}
