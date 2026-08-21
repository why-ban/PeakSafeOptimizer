using System;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace PeakSafeOptimizer.Patches.Scale
{
    /// <summary>
    /// Full equivalent replacement of AnimatorValues.Update() (audited source
    /// AnimatorValues.cs:15-37). Literal animator names are hashed once, and idempotent float/bool
    /// writes are skipped, including the damped Sprint write once GetFloat has reached its exact
    /// target. The grounded sprintAnimationDisabled write remains in its original position after
    /// all animator work.
    /// </summary>
    internal static class AnimatorValuesHashPatch
    {
        private static Func<AnimatorValues, UnityEngine.Object> _getAnimator;
        private static AccessTools.FieldRef<AnimatorValues, Character> _character;
        private static Func<string, int> _stringToHash;
        private static Func<UnityEngine.Object, int, float> _getFloat;
        private static Action<UnityEngine.Object, int, float> _setFloat;
        private static Action<UnityEngine.Object, int, float, float, float> _setFloatDamped;
        private static Func<UnityEngine.Object, int, bool> _getBool;
        private static Action<UnityEngine.Object, int, bool> _setBool;

        private static int _inputXHash;
        private static int _inputYHash;
        private static int _isGroundedHash;
        private static int _sprintHash;
        private static int _velocityYHash;

        internal static void Initialize()
        {
            ResolveAndValidateTarget();

            Type owner = typeof(AnimatorValues);
            FieldInfo animatorField = ScaleValidation.RequireInstanceFieldByTypeName(
                owner, "anim", "UnityEngine.Animator");
            FieldInfo characterField = ScaleValidation.RequireInstanceField(
                owner, "character", typeof(Character));
            ScaleValidation.RequireInstanceField(typeof(Character), "input", typeof(CharacterInput));
            ScaleValidation.RequireInstanceField(typeof(Character), "data", typeof(CharacterData));
            ScaleValidation.RequireInstanceField(typeof(Character), "refs", typeof(Character.CharacterRefs));
            ScaleValidation.RequireInstanceField(typeof(CharacterInput), "movementInput", typeof(Vector2));
            ScaleValidation.RequireInstanceField(typeof(CharacterData), "isGrounded", typeof(bool));
            ScaleValidation.RequireInstanceField(typeof(CharacterData), "isSprinting", typeof(bool));
            ScaleValidation.RequireInstanceField(typeof(CharacterData), "avarageVelocity", typeof(Vector3));
            ScaleValidation.RequireInstanceField(
                typeof(Character.CharacterRefs), "movement", typeof(CharacterMovement));
            ScaleValidation.RequireInstanceField(
                typeof(CharacterMovement), "sprintAnimationDisabled", typeof(bool));

            Type animatorType = animatorField.FieldType;
            MethodInfo stringToHash = RequireMethod(
                animatorType, "StringToHash", typeof(int), true, typeof(string));
            MethodInfo getFloat = RequireMethod(
                animatorType, "GetFloat", typeof(float), false, typeof(int));
            MethodInfo setFloat = RequireMethod(
                animatorType, "SetFloat", typeof(void), false, typeof(int), typeof(float));
            MethodInfo setFloatDamped = RequireMethod(
                animatorType,
                "SetFloat",
                typeof(void),
                false,
                typeof(int),
                typeof(float),
                typeof(float),
                typeof(float));
            MethodInfo getBool = RequireMethod(
                animatorType, "GetBool", typeof(bool), false, typeof(int));
            MethodInfo setBool = RequireMethod(
                animatorType, "SetBool", typeof(void), false, typeof(int), typeof(bool));

            Func<AnimatorValues, UnityEngine.Object> getAnimator =
                ScaleEmit.CreateUnityObjectFieldGetter<AnimatorValues>(animatorField);
            AccessTools.FieldRef<AnimatorValues, Character> character =
                AccessTools.FieldRefAccess<AnimatorValues, Character>(characterField);
            Func<string, int> hash =
                (Func<string, int>)stringToHash.CreateDelegate(typeof(Func<string, int>));
            Func<UnityEngine.Object, int, float> getFloatDelegate = EmitGetFloat(animatorType, getFloat);
            Action<UnityEngine.Object, int, float> setFloatDelegate = EmitSetFloat(animatorType, setFloat);
            Action<UnityEngine.Object, int, float, float, float> setFloatDampedDelegate =
                EmitSetFloatDamped(animatorType, setFloatDamped);
            Func<UnityEngine.Object, int, bool> getBoolDelegate = EmitGetBool(animatorType, getBool);
            Action<UnityEngine.Object, int, bool> setBoolDelegate = EmitSetBool(animatorType, setBool);
            if (getAnimator == null || character == null || hash == null || getFloatDelegate == null ||
                setFloatDelegate == null || setFloatDampedDelegate == null || getBoolDelegate == null ||
                setBoolDelegate == null)
            {
                throw new InvalidOperationException("AnimatorValuesHash accessor creation failed");
            }

            _getAnimator = getAnimator;
            _character = character;
            _stringToHash = hash;
            _getFloat = getFloatDelegate;
            _setFloat = setFloatDelegate;
            _setFloatDamped = setFloatDampedDelegate;
            _getBool = getBoolDelegate;
            _setBool = setBoolDelegate;
            _inputXHash = hash("Input X");
            _inputYHash = hash("Input Y");
            _isGroundedHash = hash("Is Grounded");
            _sprintHash = hash("Sprint");
            _velocityYHash = hash("Velocity Y");
        }

        internal static MethodInfo ResolveAndValidateTarget()
        {
            return ScaleValidation.RequireInstanceMethod(
                typeof(AnimatorValues), "Update", typeof(void), Type.EmptyTypes, false);
        }

        internal static void Reset()
        {
            _getAnimator = null;
            _character = null;
            _stringToHash = null;
            _getFloat = null;
            _setFloat = null;
            _setFloatDamped = null;
            _getBool = null;
            _setBool = null;
            _inputXHash = 0;
            _inputYHash = 0;
            _isGroundedHash = 0;
            _sprintHash = 0;
            _velocityYHash = 0;
        }

        internal static bool Prefix(AnimatorValues __instance)
        {
            Func<AnimatorValues, UnityEngine.Object> getAnimator = _getAnimator;
            AccessTools.FieldRef<AnimatorValues, Character> characterRef = _character;
            Func<UnityEngine.Object, int, float> getFloat = _getFloat;
            Action<UnityEngine.Object, int, float> setFloat = _setFloat;
            Action<UnityEngine.Object, int, float, float, float> setFloatDamped = _setFloatDamped;
            Func<UnityEngine.Object, int, bool> getBool = _getBool;
            Action<UnityEngine.Object, int, bool> setBool = _setBool;
            if (getAnimator == null || characterRef == null || _stringToHash == null ||
                getFloat == null || setFloat == null || setFloatDamped == null ||
                getBool == null || setBool == null)
            {
                return true;
            }

            UnityEngine.Object animator = getAnimator(__instance);
            Character character = characterRef(__instance);
            if (animator == null || character == null || character.input == null ||
                character.data == null || character.refs == null || character.refs.movement == null)
            {
                return true;
            }

            CharacterData data = character.data;
            Vector2 movementInput = character.input.movementInput;
            SetFloatIfChanged(animator, _inputXHash, movementInput.x, getFloat, setFloat);
            SetFloatIfChanged(animator, _inputYHash, movementInput.y, getFloat, setFloat);
            SetBoolIfChanged(animator, _isGroundedHash, data.isGrounded, getBool, setBool);

            if (character.refs.movement.sprintAnimationDisabled)
            {
                SetFloatIfChanged(animator, _sprintHash, 0f, getFloat, setFloat);
            }
            else
            {
                float sprintTarget = data.isSprinting ? 1f : 0f;
                if (getFloat(animator, _sprintHash) != sprintTarget)
                {
                    setFloatDamped(animator, _sprintHash, sprintTarget, 0.125f, Time.deltaTime);
                }
            }

            SetFloatIfChanged(animator, _velocityYHash, data.avarageVelocity.y, getFloat, setFloat);
            if (data.isGrounded)
                character.refs.movement.sprintAnimationDisabled = false;

            return false;
        }

        private static void SetFloatIfChanged(
            UnityEngine.Object animator,
            int hash,
            float target,
            Func<UnityEngine.Object, int, float> getFloat,
            Action<UnityEngine.Object, int, float> setFloat)
        {
            if (getFloat(animator, hash) != target)
                setFloat(animator, hash, target);
        }

        private static void SetBoolIfChanged(
            UnityEngine.Object animator,
            int hash,
            bool target,
            Func<UnityEngine.Object, int, bool> getBool,
            Action<UnityEngine.Object, int, bool> setBool)
        {
            if (getBool(animator, hash) != target)
                setBool(animator, hash, target);
        }

        private static MethodInfo RequireMethod(
            Type owner, string name, Type returnType, bool isStatic, params Type[] parameterTypes)
        {
            BindingFlags flags = BindingFlags.Public |
                (isStatic ? BindingFlags.Static : BindingFlags.Instance);
            MethodInfo method = owner.GetMethod(name, flags, null, parameterTypes, null);
            if (method == null || method.DeclaringType != owner || method.IsStatic != isStatic ||
                method.ReturnType != returnType || method.ContainsGenericParameters)
            {
                throw new MissingMethodException(owner.FullName, name);
            }
            return method;
        }

        private static Func<UnityEngine.Object, int, float> EmitGetFloat(
            Type animatorType, MethodInfo methodInfo)
        {
            var method = NewAnimatorMethod("PSO_AnimatorGetFloat", typeof(float), typeof(int));
            ILGenerator il = method.GetILGenerator();
            EmitAnimatorCallPrefix(il, animatorType, 1);
            il.Emit(OpCodes.Callvirt, methodInfo);
            il.Emit(OpCodes.Ret);
            return (Func<UnityEngine.Object, int, float>)method.CreateDelegate(
                typeof(Func<UnityEngine.Object, int, float>));
        }

        private static Action<UnityEngine.Object, int, float> EmitSetFloat(
            Type animatorType, MethodInfo methodInfo)
        {
            var method = NewAnimatorMethod("PSO_AnimatorSetFloat", typeof(void), typeof(int), typeof(float));
            ILGenerator il = method.GetILGenerator();
            EmitAnimatorCallPrefix(il, animatorType, 2);
            il.Emit(OpCodes.Callvirt, methodInfo);
            il.Emit(OpCodes.Ret);
            return (Action<UnityEngine.Object, int, float>)method.CreateDelegate(
                typeof(Action<UnityEngine.Object, int, float>));
        }

        private static Action<UnityEngine.Object, int, float, float, float> EmitSetFloatDamped(
            Type animatorType, MethodInfo methodInfo)
        {
            var method = NewAnimatorMethod(
                "PSO_AnimatorSetFloatDamped",
                typeof(void),
                typeof(int),
                typeof(float),
                typeof(float),
                typeof(float));
            ILGenerator il = method.GetILGenerator();
            EmitAnimatorCallPrefix(il, animatorType, 4);
            il.Emit(OpCodes.Callvirt, methodInfo);
            il.Emit(OpCodes.Ret);
            return (Action<UnityEngine.Object, int, float, float, float>)method.CreateDelegate(
                typeof(Action<UnityEngine.Object, int, float, float, float>));
        }

        private static Func<UnityEngine.Object, int, bool> EmitGetBool(
            Type animatorType, MethodInfo methodInfo)
        {
            var method = NewAnimatorMethod("PSO_AnimatorGetBool", typeof(bool), typeof(int));
            ILGenerator il = method.GetILGenerator();
            EmitAnimatorCallPrefix(il, animatorType, 1);
            il.Emit(OpCodes.Callvirt, methodInfo);
            il.Emit(OpCodes.Ret);
            return (Func<UnityEngine.Object, int, bool>)method.CreateDelegate(
                typeof(Func<UnityEngine.Object, int, bool>));
        }

        private static Action<UnityEngine.Object, int, bool> EmitSetBool(
            Type animatorType, MethodInfo methodInfo)
        {
            var method = NewAnimatorMethod("PSO_AnimatorSetBool", typeof(void), typeof(int), typeof(bool));
            ILGenerator il = method.GetILGenerator();
            EmitAnimatorCallPrefix(il, animatorType, 2);
            il.Emit(OpCodes.Callvirt, methodInfo);
            il.Emit(OpCodes.Ret);
            return (Action<UnityEngine.Object, int, bool>)method.CreateDelegate(
                typeof(Action<UnityEngine.Object, int, bool>));
        }

        private static DynamicMethod NewAnimatorMethod(
            string name, Type returnType, params Type[] parametersAfterAnimator)
        {
            Type[] parameters = new Type[parametersAfterAnimator.Length + 1];
            parameters[0] = typeof(UnityEngine.Object);
            Array.Copy(parametersAfterAnimator, 0, parameters, 1, parametersAfterAnimator.Length);
            return new DynamicMethod(
                name,
                returnType,
                parameters,
                typeof(AnimatorValuesHashPatch).Module,
                true);
        }

        private static void EmitAnimatorCallPrefix(ILGenerator il, Type animatorType, int argumentCount)
        {
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, animatorType);
            for (int i = 1; i <= argumentCount; i++)
                il.Emit(OpCodes.Ldarg, i);
        }
    }
}
