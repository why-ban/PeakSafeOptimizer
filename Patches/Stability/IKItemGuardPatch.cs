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
    /// CharacterRagdoll.FixedUpdate.
    ///
    /// Root cause (confirmed in-game, 135 occurrences in one multiplayer session):
    /// ConfigureIK writes IKHandTargetRight/Left position and rotation from
    /// CharacterItems.GetItemPosRight/RotRight/PosLeft/RotLeft. Those helpers dereference
    /// item.transform.Find("Hand_R") / Find("Hand_L") (CharacterItems.cs:887, 894, 904, 913). When
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
    ///   - Only when currentItem is present but Find("Hand_R") or Find("Hand_L") is null does the
    ///     original throw; this patch skips that frame's item branch and returns false.
    ///
    /// ReachIK() is never called from this patch, so ReachHandPos and any other state it manages are
    /// left exactly as they were.
    /// </summary>
    internal static class IKItemGuardPatch
    {
        private static AccessTools.FieldRef<CharacterAnimations, Character> _character;

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

            AccessTools.FieldRef<CharacterAnimations, Character> character =
                AccessTools.FieldRefAccess<CharacterAnimations, Character>(characterField);
            if (character == null)
                throw new InvalidOperationException("IKItemGuard accessor creation failed");
            _character = character;
        }

        internal static MethodInfo ResolveAndValidateTarget()
        {
            return StabilityPatchValidation.RequireInstanceMethod(
                typeof(CharacterAnimations), "ConfigureIK", typeof(void), Type.EmptyTypes,
                requirePublic: true);
        }

        internal static void Reset() => _character = null;

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
            Transform handLeft = item.transform.Find("Hand_L");
            if (handRight == null || handLeft == null)
            {
                RateLimitedDiagnostics.Hit("IKItemGuard.hand-node-missing");
                return false; // Skip this frame's item hand-IK write; every other IK path is safe.
            }

            return true;
        }
    }
}
