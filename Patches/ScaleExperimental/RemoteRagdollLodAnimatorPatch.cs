using System;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using PeakSafeOptimizer.Diagnostics;
using PeakSafeOptimizer.Patches.Scale;
using UnityEngine;

namespace PeakSafeOptimizer.Patches.ScaleExperimental
{
    /// <summary>
    /// Opt-in: skips CharacterAnimations.Update() (audited source CharacterAnimations.cs:160-286)
    /// for a distant remote character selected by <see cref="RemoteRagdollLod.ShouldDowngrade"/>.
    /// The method writes roughly thirty Animator parameters per character per frame plus a
    /// Matrix4x4 look computation, and every one of those writes only feeds the animation pose.
    ///
    /// Why no physics can break: this method applies no force and moves no Rigidbody. Its only
    /// non-Animator writes are helper-transform and bookkeeping values:
    ///   refs.animationPositionTransform.position (a helper transform; its only other consumer is
    ///     Action_Torch's particle simulation space)
    ///   throwTime countdown, data.myersDistance reset to 1000f, data.inWater reset to 1f
    /// None of these is read by a force path. data.myersDistance and data.inWater are only consumed
    /// as Animator parameters (CharacterAnimations.cs:249 and :284), so leaving them stale for a
    /// distant character has no gameplay effect on this client.
    ///
    /// Two effects are accepted by design, both cosmetic and both on a character that is at least
    /// the configured enter distance away: its Animator parameters freeze at their last values, and
    /// data.isInWater (which reads back the InWater Animator parameter) can lag, which can make its
    /// distant footstep/water SFX variant stale. HandleIK is additionally called every frame from
    /// CharacterAnimations.LateUpdate (CharacterAnimations.cs:335-338), which is not patched, so IK
    /// weights keep being maintained even while Update is skipped.
    /// </summary>
    internal static class RemoteRagdollLodAnimatorPatch
    {
        private static AccessTools.FieldRef<CharacterAnimations, Character> _character;
        private static AccessTools.FieldRef<CharacterAnimations, bool> _emoting;
        private static AccessTools.FieldRef<CharacterAnimations, float> _sinceEmoteStart;
        private static Func<Character.CharacterRefs, UnityEngine.Object> _getAnimator;
        private static Action<UnityEngine.Object, int, bool> _setBool;
        private static Action<UnityEngine.Object, int, float> _setFloat;
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
            FieldInfo animatorField = ScaleValidation.RequireInstanceFieldByTypeName(
                typeof(Character.CharacterRefs), "animator", "UnityEngine.Animator");
            ScaleValidation.RequireInstanceField(typeof(Character), "data", typeof(CharacterData));
            ScaleValidation.RequireInstanceField(typeof(Character), "input", typeof(CharacterInput));
            ScaleValidation.RequireInstanceField(typeof(Character), "refs", typeof(Character.CharacterRefs));
            ScaleValidation.RequireInstanceField(
                typeof(Character.CharacterRefs), "animationPositionTransform", typeof(UnityEngine.Transform));
            ScaleValidation.RequireInstanceField(typeof(CharacterData), "myersDistance", typeof(float));
            ScaleValidation.RequireInstanceField(typeof(CharacterData), "inWater", typeof(float));
            // Also read by the replacement body: the throw countdown it advances and the grounded
            // timer its emote-cancel condition tests. Both are direct compile-time field references,
            // so with requireCurrentMvid these can only fail together with the whole patch - asserted
            // anyway so the validation list matches the set of members the replacement touches.
            ScaleValidation.RequireInstanceField(typeof(CharacterAnimations), "throwTime", typeof(float));
            ScaleValidation.RequireInstanceField(typeof(CharacterData), "sinceGrounded", typeof(float));
            ScaleValidation.RequireInstanceField(typeof(CharacterInput), "movementInput", typeof(UnityEngine.Vector2));
            ScaleValidation.RequireInstanceField(typeof(CharacterInput), "jumpWasPressed", typeof(bool));
            // Proof that HandleIK is still maintained outside Update, which the safety argument
            // above relies on.
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
            Func<Character.CharacterRefs, UnityEngine.Object> getAnimator =
                ScaleEmit.CreateUnityObjectFieldGetter<Character.CharacterRefs>(animatorField);
            Type animatorType = animatorField.FieldType;
            MethodInfo stringToHash = animatorType.GetMethod(
                "StringToHash", BindingFlags.Public | BindingFlags.Static, null,
                new[] { typeof(string) }, null);
            MethodInfo setBool = animatorType.GetMethod(
                "SetBool", BindingFlags.Public | BindingFlags.Instance, null,
                new[] { typeof(int), typeof(bool) }, null);
            MethodInfo setFloat = animatorType.GetMethod(
                "SetFloat", BindingFlags.Public | BindingFlags.Instance, null,
                new[] { typeof(int), typeof(float) }, null);
            if (stringToHash == null || setBool == null || setFloat == null)
                throw new MissingMethodException("UnityEngine.Animator", "hash/set accessors");
            Func<string, int> hash = (Func<string, int>)stringToHash.CreateDelegate(typeof(Func<string, int>));
            Action<UnityEngine.Object, int, bool> setBoolDelegate =
                EmitSetBool(animatorType, setBool);
            Action<UnityEngine.Object, int, float> setFloatDelegate =
                EmitSetFloat(animatorType, setFloat);
            Action<CharacterAnimations> setAnimSpeed =
                ScaleExperimentalEmit.CreateDelegate<Action<CharacterAnimations>>(setAnimSpeedMethod);
            if (character == null || emoting == null || sinceEmoteStart == null || getAnimator == null ||
                setBoolDelegate == null || setFloatDelegate == null || setAnimSpeed == null)
                throw new InvalidOperationException("RemoteRagdollLodAnimator FieldRef creation failed");

            // Acquired last so a failed Initialize never leaves a reference on the shared helper.
            RemoteRagdollLod.Acquire();
            _acquired = true;
            _character = character;
            _emoting = emoting;
            _sinceEmoteStart = sinceEmoteStart;
            _getAnimator = getAnimator;
            _setBool = setBoolDelegate;
            _setFloat = setFloatDelegate;
            _setAnimSpeed = setAnimSpeed;
            _emoteHash = hash("Emote");
            _inWaterHash = hash("InWater");
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
            _getAnimator = null;
            _setBool = null;
            _setFloat = null;
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
            // Another Prefix on CharacterAnimations.Update may already have decided to skip it.
            // HarmonyX calls every Prefix and only combines their verdicts afterwards, so reading
            // __runOriginal is what keeps this lane exclusive with any other patch on the same target.
            // No such patch exists in this plugin today, but the replacement below writes Animator
            // parameters and advances throwTime/sinceEmoteStart, so running it after someone else has
            // already suppressed the original would double-advance those timers.
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
            Func<Character.CharacterRefs, UnityEngine.Object> getAnimator = _getAnimator;
            Action<UnityEngine.Object, int, bool> setBool = _setBool;
            Action<UnityEngine.Object, int, float> setFloat = _setFloat;
            Action<CharacterAnimations> setAnimSpeed = _setAnimSpeed;
            AccessTools.FieldRef<CharacterAnimations, bool> emotingRef = _emoting;
            AccessTools.FieldRef<CharacterAnimations, float> sinceEmoteStartRef = _sinceEmoteStart;
            if (data == null || refs == null || refs.animationPositionTransform == null || getAnimator == null ||
                setBool == null || setFloat == null || setAnimSpeed == null ||
                emotingRef == null || sinceEmoteStartRef == null)
                return true;

            Bodypart hip = refs.hip;
            UnityEngine.Object animator = getAnimator(refs);
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
                setBool(animator, _emoteHash, false);
                emotingRef(__instance) = false;
            }

            setFloat(animator, _inWaterHash, data.inWater);
            data.myersDistance = 1000f;
            data.inWater = 1f;
            return false;
        }

        private static Action<UnityEngine.Object, int, bool> EmitSetBool(Type animatorType, MethodInfo setter)
        {
            var method = new DynamicMethod(
                "PSO_RemoteAnimatorSetBool",
                typeof(void),
                new[] { typeof(UnityEngine.Object), typeof(int), typeof(bool) },
                typeof(RemoteRagdollLodAnimatorPatch).Module,
                true);
            ILGenerator il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, animatorType);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldarg_2);
            il.Emit(OpCodes.Callvirt, setter);
            il.Emit(OpCodes.Ret);
            return (Action<UnityEngine.Object, int, bool>)method.CreateDelegate(
                typeof(Action<UnityEngine.Object, int, bool>));
        }

        private static Action<UnityEngine.Object, int, float> EmitSetFloat(Type animatorType, MethodInfo setter)
        {
            var method = new DynamicMethod(
                "PSO_RemoteAnimatorSetFloat",
                typeof(void),
                new[] { typeof(UnityEngine.Object), typeof(int), typeof(float) },
                typeof(RemoteRagdollLodAnimatorPatch).Module,
                true);
            ILGenerator il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, animatorType);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldarg_2);
            il.Emit(OpCodes.Callvirt, setter);
            il.Emit(OpCodes.Ret);
            return (Action<UnityEngine.Object, int, float>)method.CreateDelegate(
                typeof(Action<UnityEngine.Object, int, float>));
        }
    }
}
