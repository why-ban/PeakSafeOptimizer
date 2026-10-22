using System;
using System.Reflection;
using HarmonyLib;
using PeakSafeOptimizer.Diagnostics;
using PeakSafeOptimizer.Patches.Scale;
using UnityEngine;

namespace PeakSafeOptimizer.Patches.ScaleExperimental
{
    /// <summary>
    /// Replaces CharacterAnimations.Update for a character ShouldDowngrade selects with a reduced body: the
    /// animation helper transform, SetAnimSpeed, the two timers, the emote-cancel condition, the InWater
    /// parameter and the myersDistance/inWater resets; the dropped Animator writes only feed the pose. Both
    /// timers keep advancing, which is why the Prefix must honour __runOriginal; HandleIK lives in LateUpdate.
    /// </summary>
    internal static class RemoteRagdollLodAnimatorPatch
    {
        private static AccessTools.FieldRef<CharacterAnimations, Character> _character;
        private static AccessTools.FieldRef<CharacterAnimations, bool> _emoting;
        private static AccessTools.FieldRef<CharacterAnimations, float> _sinceEmoteStart;
        private static Action<CharacterAnimations> _setAnimSpeed;
        private static int _emoteHash;
        private static int _inWaterHash;
        private static bool _acquired;

        internal static void Initialize()
        {
            ResolveAndValidateTarget();

            FieldInfo characterField = ScaleValidation.RequireInstanceField(
                typeof(CharacterAnimations), "character", typeof(Character));
            FieldInfo emotingField = ScaleValidation.RequireInstanceField(
                typeof(CharacterAnimations), "emoting", typeof(bool));
            FieldInfo sinceEmoteStartField = ScaleValidation.RequireInstanceField(
                typeof(CharacterAnimations), "sinceEmoteStart", typeof(float));
            ScaleValidation.RequireInstanceField(
                typeof(Character.CharacterRefs), "animator", typeof(Animator));
            ScaleValidation.RequireStaticMethod(
                typeof(Animator), "StringToHash", typeof(int), new[] { typeof(string) }, requirePublic: true);
            ScaleValidation.RequireInstanceMethod(
                typeof(Animator), "SetBool", typeof(void), new[] { typeof(int), typeof(bool) },
                requirePublic: true);
            ScaleValidation.RequireInstanceMethod(
                typeof(Animator), "SetFloat", typeof(void), new[] { typeof(int), typeof(float) },
                requirePublic: true);
            ScaleValidation.RequireInstanceField(typeof(Character), "data", typeof(CharacterData));
            ScaleValidation.RequireInstanceField(typeof(Character), "input", typeof(CharacterInput));
            ScaleValidation.RequireInstanceField(typeof(Character), "refs", typeof(Character.CharacterRefs));
            ScaleValidation.RequireInstanceField(
                typeof(Character.CharacterRefs), "animationPositionTransform", typeof(UnityEngine.Transform));
            ScaleValidation.RequireInstanceField(
                typeof(Character.CharacterRefs), "hip", typeof(Bodypart));
            ScaleValidation.RequireInstanceField(typeof(CharacterData), "myersDistance", typeof(float));
            ScaleValidation.RequireInstanceField(typeof(CharacterData), "inWater", typeof(float));
            ScaleValidation.RequireInstanceField(typeof(CharacterAnimations), "throwTime", typeof(float));
            ScaleValidation.RequireInstanceField(typeof(CharacterData), "sinceGrounded", typeof(float));
            ScaleValidation.RequireInstanceField(typeof(CharacterInput), "movementInput", typeof(UnityEngine.Vector2));
            ScaleValidation.RequireInstanceField(typeof(CharacterInput), "jumpWasPressed", typeof(bool));
            ScaleValidation.RequireInstanceMethod(
                typeof(CharacterAnimations), "LateUpdate", typeof(void), Type.EmptyTypes, false);
            ScaleValidation.RequireInstanceMethod(
                typeof(CharacterAnimations), "HandleIK", typeof(void), Type.EmptyTypes, false);
            MethodInfo setAnimSpeedMethod = ScaleValidation.RequireInstanceMethod(
                typeof(CharacterAnimations), "SetAnimSpeed", typeof(void), Type.EmptyTypes, false);

            AccessTools.FieldRef<CharacterAnimations, Character> character =
                AccessTools.FieldRefAccess<CharacterAnimations, Character>(characterField);
            AccessTools.FieldRef<CharacterAnimations, bool> emoting =
                AccessTools.FieldRefAccess<CharacterAnimations, bool>(emotingField);
            AccessTools.FieldRef<CharacterAnimations, float> sinceEmoteStart =
                AccessTools.FieldRefAccess<CharacterAnimations, float>(sinceEmoteStartField);
            Action<CharacterAnimations> setAnimSpeed =
                ScaleExperimentalEmit.CreateDelegate<Action<CharacterAnimations>>(setAnimSpeedMethod);
            if (character == null || emoting == null || sinceEmoteStart == null || setAnimSpeed == null)
                throw new InvalidOperationException("RemoteRagdollLodAnimator FieldRef creation failed");

            int emoteHash = Animator.StringToHash("Emote");
            int inWaterHash = Animator.StringToHash("InWater");

            RemoteRagdollLod.Acquire();
            _acquired = true;
            _character = character;
            _emoting = emoting;
            _sinceEmoteStart = sinceEmoteStart;
            _setAnimSpeed = setAnimSpeed;
            _emoteHash = emoteHash;
            _inWaterHash = inWaterHash;
        }

        internal static MethodInfo ResolveAndValidateTarget()
        {
            return ScaleValidation.RequireInstanceMethod(
                typeof(CharacterAnimations), "Update", typeof(void), Type.EmptyTypes, requirePublic: false);
        }

        internal static void Reset()
        {
            _character = null;
            _emoting = null;
            _sinceEmoteStart = null;
            _setAnimSpeed = null;
            _emoteHash = 0;
            _inWaterHash = 0;
            if (_acquired)
            {
                _acquired = false;
                RemoteRagdollLod.Release();
            }
        }

        internal static bool Prefix(CharacterAnimations __instance, bool __runOriginal)
        {
            if (!__runOriginal)
                return false;

            AccessTools.FieldRef<CharacterAnimations, Character> characterRef = _character;
            if (characterRef == null)
            {
                RateLimitedDiagnostics.Hit("RemoteRagdollLodAnimator.accessor-unavailable");
                return true;
            }

            Character owner = characterRef(__instance);
            if (owner == null)
            {
                RateLimitedDiagnostics.Hit("RemoteRagdollLodAnimator.character-null");
                return true;
            }
            if (!RemoteRagdollLod.ShouldDowngrade(owner))
                return true;

            CharacterData data = owner.data;
            Character.CharacterRefs refs = owner.refs;
            Action<CharacterAnimations> setAnimSpeed = _setAnimSpeed;
            AccessTools.FieldRef<CharacterAnimations, bool> emotingRef = _emoting;
            AccessTools.FieldRef<CharacterAnimations, float> sinceEmoteStartRef = _sinceEmoteStart;
            if (data == null || refs == null || refs.animationPositionTransform == null ||
                setAnimSpeed == null || emotingRef == null || sinceEmoteStartRef == null)
                return true;

            Bodypart hip = refs.hip;
            Animator animator = refs.animator;
            if (hip == null || hip.transform == null || animator == null || owner.input == null)
                return true;

            refs.animationPositionTransform.position = hip.transform.position;
            setAnimSpeed(__instance);
            float dt = Time.deltaTime;
            __instance.throwTime -= dt;
            if (__instance.throwTime <= 0f)
                __instance.throwTime = 0f;
            sinceEmoteStartRef(__instance) += dt;

            if (emotingRef(__instance) &&
                (sinceEmoteStartRef(__instance) > 2f ||
                 (sinceEmoteStartRef(__instance) > 0.7f &&
                  (owner.input.movementInput.magnitude > 0.1f || owner.input.jumpWasPressed ||
                   data.sinceGrounded > 0.2f))))
            {
                animator.SetBool(_emoteHash, false);
                emotingRef(__instance) = false;
            }

            animator.SetFloat(_inWaterHash, data.inWater);
            data.myersDistance = 1000f;
            data.inWater = 1f;
            return false;
        }
    }
}
