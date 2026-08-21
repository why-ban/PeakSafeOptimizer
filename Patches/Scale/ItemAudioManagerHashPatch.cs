using System;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using PeakSafeOptimizer.Diagnostics;
using UnityEngine;

namespace PeakSafeOptimizer.Patches.Scale
{
    /// <summary>
    /// Full equivalent replacement of ItemAudioManager.Update() (audited source
    /// ItemAudioManager.cs:26-101). Three strictly-equivalent reductions:
    ///   1. The name-based animator.SetBool(name, ...) / GetBool(name) calls are replaced with the
    ///      cached int-hash overloads (Animator.StringToHash). Unity documents that the name and
    ///      hash overloads are interchangeable. The five literal names are hashed once during
    ///      Initialize; the dynamic prevUse/useAnimation names are hashed through a
    ///      ConditionalWeakTable keyed by the string so the key is never strongly held.
    ///   2. SetBool is skipped when GetBool already reports the target value (SetBool is idempotent,
    ///      so this is a no-op elimination).
    ///   3. throwCharge.volume/pitch writes are skipped once the value has converged to its Lerp
    ///      target (Lerp(target, target, t) == target, so the write is a no-op).
    ///
    /// Animator and AudioSource live in UnityEngine.AnimationModule / AudioModule, which this
    /// project does not reference, so their members are reached through accessors emitted once
    /// during Initialize.
    /// </summary>
    internal static class ItemAudioManagerHashPatch
    {
        private sealed class HashBox
        {
            internal int Hash;
        }

        private static AccessTools.FieldRef<ItemAudioManager, Character> _character;
        private static AccessTools.FieldRef<ItemAudioManager, string> _prevUse;
        private static AccessTools.FieldRef<ItemAudioManager, Item> _prevItem;

        private static Func<Character.CharacterRefs, UnityEngine.Object> _getAnimator;
        private static Func<ItemAudioManager, UnityEngine.Object> _getThrowCharge;
        private static Func<string, int> _stringToHash;
        private static Action<UnityEngine.Object, int, bool> _setBool;
        private static Func<UnityEngine.Object, int, bool> _getBool;
        private static Func<UnityEngine.Object, float> _getVolume;
        private static Action<UnityEngine.Object, float> _setVolume;
        private static Func<UnityEngine.Object, float> _getPitch;
        private static Action<UnityEngine.Object, float> _setPitch;

        private static int _eatHash;
        private static int _healHash;
        private static int _drinkHash;
        private static int _antidoteHash;
        private static int _consumedItemHash;

        private static ConditionalWeakTable<string, HashBox> _hashCache;

        internal static void Initialize()
        {
            ResolveAndValidateTarget();

            FieldInfo characterField = ScaleValidation.RequireInstanceField(
                typeof(ItemAudioManager), "character", typeof(Character));
            FieldInfo prevUseField = ScaleValidation.RequireInstanceField(
                typeof(ItemAudioManager), "prevUse", typeof(string));
            FieldInfo prevItemField = ScaleValidation.RequireInstanceField(
                typeof(ItemAudioManager), "prevItem", typeof(Item));
            ScaleValidation.RequireInstanceField(typeof(ItemAudioManager), "finishTimer", typeof(float));
            ScaleValidation.RequireInstanceField(
                typeof(ItemAudioManager), "switchGeneric", typeof(SFX_Instance[]));
            FieldInfo throwChargeField = ScaleValidation.RequireInstanceFieldByTypeName(
                typeof(ItemAudioManager), "throwCharge", "UnityEngine.AudioSource");

            ScaleValidation.RequireInstanceField(typeof(Character), "refs", typeof(Character.CharacterRefs));
            ScaleValidation.RequireInstanceField(typeof(Character), "data", typeof(CharacterData));
            FieldInfo animatorField = ScaleValidation.RequireInstanceFieldByTypeName(
                typeof(Character.CharacterRefs), "animator", "UnityEngine.Animator");
            ScaleValidation.RequireInstanceField(
                typeof(Character.CharacterRefs), "items", typeof(CharacterItems));
            ScaleValidation.RequireInstanceField(typeof(CharacterItems), "throwChargeLevel", typeof(float));
            ScaleValidation.RequireInstanceProperty(typeof(CharacterData), "currentItem", typeof(Item));
            ScaleValidation.RequireInstanceProperty(typeof(Item), "isUsingPrimary", typeof(bool));
            ScaleValidation.RequireInstanceProperty(typeof(Item), "castProgress", typeof(float));
            ScaleValidation.RequireInstanceField(typeof(ItemUseFeedback), "useAnimation", typeof(string));
            ScaleValidation.RequireInstanceField(typeof(ItemUseFeedback), "equip", typeof(SFX_Instance[]));
            ScaleValidation.RequireInstanceMethod(
                typeof(SFX_Instance), "Play", typeof(void), new[] { typeof(Vector3) }, true);

            // Animator members.
            Type animatorType = animatorField.FieldType;
            MethodInfo stringToHash = animatorType.GetMethod(
                "StringToHash", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
            if (stringToHash == null || stringToHash.ReturnType != typeof(int) || !stringToHash.IsStatic)
                throw new MissingMethodException("UnityEngine.Animator", "StringToHash");
            MethodInfo setBool = animatorType.GetMethod(
                "SetBool", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(int), typeof(bool) }, null);
            if (setBool == null || setBool.ReturnType != typeof(void) || setBool.IsStatic)
                throw new MissingMethodException("UnityEngine.Animator", "SetBool(int, bool)");
            MethodInfo getBool = animatorType.GetMethod(
                "GetBool", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(int) }, null);
            if (getBool == null || getBool.ReturnType != typeof(bool) || getBool.IsStatic)
                throw new MissingMethodException("UnityEngine.Animator", "GetBool(int)");

            // AudioSource members.
            Type audioSourceType = throwChargeField.FieldType;
            PropertyInfo volumeProperty = audioSourceType.GetProperty(
                "volume", BindingFlags.Instance | BindingFlags.Public);
            PropertyInfo pitchProperty = audioSourceType.GetProperty(
                "pitch", BindingFlags.Instance | BindingFlags.Public);
            if (volumeProperty == null || volumeProperty.PropertyType != typeof(float) ||
                volumeProperty.GetGetMethod(false) == null || volumeProperty.GetSetMethod(false) == null)
            {
                throw new MissingMethodException("UnityEngine.AudioSource", "volume");
            }
            if (pitchProperty == null || pitchProperty.PropertyType != typeof(float) ||
                pitchProperty.GetGetMethod(false) == null || pitchProperty.GetSetMethod(false) == null)
            {
                throw new MissingMethodException("UnityEngine.AudioSource", "pitch");
            }

            AccessTools.FieldRef<ItemAudioManager, Character> character =
                AccessTools.FieldRefAccess<ItemAudioManager, Character>(characterField);
            AccessTools.FieldRef<ItemAudioManager, string> prevUse =
                AccessTools.FieldRefAccess<ItemAudioManager, string>(prevUseField);
            AccessTools.FieldRef<ItemAudioManager, Item> prevItem =
                AccessTools.FieldRefAccess<ItemAudioManager, Item>(prevItemField);

            Func<Character.CharacterRefs, UnityEngine.Object> getAnimator =
                ScaleEmit.CreateUnityObjectFieldGetter<Character.CharacterRefs>(animatorField);
            Func<ItemAudioManager, UnityEngine.Object> getThrowCharge =
                ScaleEmit.CreateUnityObjectFieldGetter<ItemAudioManager>(throwChargeField);

            Func<string, int> stringToHashDelegate =
                (Func<string, int>)stringToHash.CreateDelegate(typeof(Func<string, int>));
            Action<UnityEngine.Object, int, bool> setBoolDelegate = EmitSetBool(animatorType, setBool);
            Func<UnityEngine.Object, int, bool> getBoolDelegate = EmitGetBool(animatorType, getBool);
            Func<UnityEngine.Object, float> getVolume =
                EmitPropertyGetter(audioSourceType, volumeProperty.GetGetMethod(false), "volume");
            Action<UnityEngine.Object, float> setVolume =
                EmitPropertySetter(audioSourceType, volumeProperty.GetSetMethod(false), "volume");
            Func<UnityEngine.Object, float> getPitch =
                EmitPropertyGetter(audioSourceType, pitchProperty.GetGetMethod(false), "pitch");
            Action<UnityEngine.Object, float> setPitch =
                EmitPropertySetter(audioSourceType, pitchProperty.GetSetMethod(false), "pitch");

            if (character == null || prevUse == null || prevItem == null || getAnimator == null ||
                getThrowCharge == null || stringToHashDelegate == null || setBoolDelegate == null ||
                getBoolDelegate == null || getVolume == null || setVolume == null || getPitch == null ||
                setPitch == null)
            {
                throw new InvalidOperationException("ItemAudioManagerHash accessor creation failed");
            }

            _character = character;
            _prevUse = prevUse;
            _prevItem = prevItem;
            _getAnimator = getAnimator;
            _getThrowCharge = getThrowCharge;
            _stringToHash = stringToHashDelegate;
            _setBool = setBoolDelegate;
            _getBool = getBoolDelegate;
            _getVolume = getVolume;
            _setVolume = setVolume;
            _getPitch = getPitch;
            _setPitch = setPitch;
            _eatHash = stringToHashDelegate("Eat");
            _healHash = stringToHashDelegate("Heal");
            _drinkHash = stringToHashDelegate("Drink");
            _antidoteHash = stringToHashDelegate("Antidote");
            _consumedItemHash = stringToHashDelegate("Consumed Item");
            _hashCache = new ConditionalWeakTable<string, HashBox>();
        }

        internal static MethodInfo ResolveAndValidateTarget()
        {
            return ScaleValidation.RequireInstanceMethod(
                typeof(ItemAudioManager), "Update", typeof(void), Type.EmptyTypes, false);
        }

        internal static void Reset()
        {
            _character = null;
            _prevUse = null;
            _prevItem = null;
            _getAnimator = null;
            _getThrowCharge = null;
            _stringToHash = null;
            _setBool = null;
            _getBool = null;
            _getVolume = null;
            _setVolume = null;
            _getPitch = null;
            _setPitch = null;
            _hashCache = null;
        }

        internal static bool Prefix(ItemAudioManager __instance)
        {
            AccessTools.FieldRef<ItemAudioManager, Character> characterRef = _character;
            AccessTools.FieldRef<ItemAudioManager, string> prevUseRef = _prevUse;
            AccessTools.FieldRef<ItemAudioManager, Item> prevItemRef = _prevItem;
            Func<Character.CharacterRefs, UnityEngine.Object> getAnimator = _getAnimator;
            Func<ItemAudioManager, UnityEngine.Object> getThrowCharge = _getThrowCharge;
            Func<string, int> stringToHash = _stringToHash;
            Action<UnityEngine.Object, int, bool> setBool = _setBool;
            Func<UnityEngine.Object, int, bool> getBool = _getBool;
            Func<UnityEngine.Object, float> getVolume = _getVolume;
            Action<UnityEngine.Object, float> setVolume = _setVolume;
            Func<UnityEngine.Object, float> getPitch = _getPitch;
            Action<UnityEngine.Object, float> setPitch = _setPitch;
            ConditionalWeakTable<string, HashBox> hashCache = _hashCache;

            if (characterRef == null || prevUseRef == null || prevItemRef == null || getAnimator == null ||
                getThrowCharge == null || stringToHash == null || setBool == null || getBool == null ||
                getVolume == null || setVolume == null || getPitch == null || setPitch == null ||
                hashCache == null)
            {
                RateLimitedDiagnostics.Hit("ItemAudioManagerHash.accessors-unavailable");
                return true;
            }

            Character character = characterRef(__instance);
            if (character == null)
            {
                RateLimitedDiagnostics.Hit("ItemAudioManagerHash.character-null");
                return true;
            }

            Character.CharacterRefs refs = character.refs;
            if (refs == null)
            {
                RateLimitedDiagnostics.Hit("ItemAudioManagerHash.refs-null");
                return true;
            }

            UnityEngine.Object animator = getAnimator(refs);
            if (animator == null)
            {
                RateLimitedDiagnostics.Hit("ItemAudioManagerHash.animator-null");
                return true;
            }

            UnityEngine.Object throwCharge = getThrowCharge(__instance);
            if (throwCharge == null)
            {
                RateLimitedDiagnostics.Hit("ItemAudioManagerHash.throwCharge-null");
                return true;
            }

            int eatHash = _eatHash;
            int healHash = _healHash;
            int drinkHash = _drinkHash;
            int antidoteHash = _antidoteHash;
            int consumedItemHash = _consumedItemHash;

            // ItemAudioManager.cs:28-31
            if (getBool(animator, eatHash)) setBool(animator, eatHash, false);
            if (getBool(animator, healHash)) setBool(animator, healHash, false);
            if (getBool(animator, drinkHash)) setBool(animator, drinkHash, false);
            if (getBool(animator, antidoteHash)) setBool(animator, antidoteHash, false);

            // ItemAudioManager.cs:32-33
            float dt = Time.deltaTime;
            float volume = getVolume(throwCharge);
            float newVolume = Mathf.Lerp(volume, 0f, dt * 5f);
            if (newVolume != volume) setVolume(throwCharge, newVolume);
            float pitch = getPitch(throwCharge);
            float newPitch = Mathf.Lerp(pitch, 1f, dt * 5f);
            if (newPitch != pitch) setPitch(throwCharge, newPitch);

            string prevUse = prevUseRef(__instance);
            bool hasPrevUse = !string.IsNullOrEmpty(prevUse);
            int prevUseHash = 0;
            if (hasPrevUse)
                prevUseHash = GetStringHash(hashCache, stringToHash, prevUse);

            // ItemAudioManager.cs:34-37 (IsNullOrEmpty here is string.IsNullOrEmpty).
            if (hasPrevUse)
            {
                if (getBool(animator, prevUseHash)) setBool(animator, prevUseHash, false);
            }

            Item currentItem = character.data.currentItem;

            // ItemAudioManager.cs:38-41
            if (!currentItem && hasPrevUse)
            {
                if (getBool(animator, prevUseHash)) setBool(animator, prevUseHash, false);
            }

            // ItemAudioManager.cs:42-49
            if (getBool(animator, consumedItemHash))
            {
                __instance.finishTimer -= dt;
            }
            else
            {
                __instance.finishTimer = 0.25f;
            }

            // ItemAudioManager.cs:50-53
            if (__instance.finishTimer <= 0f)
            {
                if (getBool(animator, consumedItemHash)) setBool(animator, consumedItemHash, false);
            }

            // ItemAudioManager.cs:54
            if (currentItem)
            {
                // ItemAudioManager.cs:56-60
                float throwChargeLevel = character.refs.items.throwChargeLevel;
                if (throwChargeLevel > 0f)
                {
                    float v = getVolume(throwCharge);
                    float nv = Mathf.Lerp(v, 0.3f, dt * 10f);
                    if (nv != v) setVolume(throwCharge, nv);

                    float p = getPitch(throwCharge);
                    float np = Mathf.Lerp(p, 2f + throwChargeLevel * 3f, dt * 10f);
                    if (np != p) setPitch(throwCharge, np);
                }

                Item prevItem = prevItemRef(__instance);

                // ItemAudioManager.cs:61-67
                if (prevItem != currentItem)
                {
                    SFX_Instance[] switchGeneric = __instance.switchGeneric;
                    for (int i = 0; i < switchGeneric.Length; i++)
                    {
                        switchGeneric[i].Play(__instance.transform.position);
                    }
                }

                // ItemAudioManager.cs:68-91
                if (currentItem.TryGetComponent<ItemUseFeedback>(out ItemUseFeedback component))
                {
                    // ItemAudioManager.cs:70-77
                    if (prevItem != currentItem)
                    {
                        SFX_Instance[] equip = component.equip;
                        for (int j = 0; j < equip.Length; j++)
                        {
                            equip[j].Play(__instance.transform.position);
                        }
                    }

                    // ItemAudioManager.cs:78-89
                    string useAnimation = component.useAnimation;
                    if (!string.IsNullOrEmpty(useAnimation))
                    {
                        bool usePrimary = currentItem.isUsingPrimary && currentItem.castProgress < 1f;
                        int useHash = GetStringHash(hashCache, stringToHash, useAnimation);
                        if (getBool(animator, useHash) != usePrimary)
                            setBool(animator, useHash, usePrimary);
                    }

                    // ItemAudioManager.cs:90
                    prevUseRef(__instance) = useAnimation;
                }
            }

            // ItemAudioManager.cs:93-99
            if (prevItemRef(__instance) && !currentItem)
            {
                SFX_Instance[] switchGeneric = __instance.switchGeneric;
                for (int k = 0; k < switchGeneric.Length; k++)
                {
                    switchGeneric[k].Play(__instance.transform.position);
                }
            }

            // ItemAudioManager.cs:100
            prevItemRef(__instance) = currentItem;

            return false;
        }

        private static int GetStringHash(
            ConditionalWeakTable<string, HashBox> cache, Func<string, int> stringToHash, string name)
        {
            HashBox box = cache.GetValue(name, _ => new HashBox());
            int hash = box.Hash;
            if (hash == 0)
            {
                hash = stringToHash(name);
                box.Hash = hash;
            }
            return hash;
        }

        private static Action<UnityEngine.Object, int, bool> EmitSetBool(Type animatorType, MethodInfo setBool)
        {
            var method = new DynamicMethod(
                "PSO_SetBool",
                typeof(void),
                new[] { typeof(UnityEngine.Object), typeof(int), typeof(bool) },
                typeof(ItemAudioManagerHashPatch).Module,
                true);
            ILGenerator il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, animatorType);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldarg_2);
            il.Emit(OpCodes.Callvirt, setBool);
            il.Emit(OpCodes.Ret);
            return (Action<UnityEngine.Object, int, bool>)method.CreateDelegate(
                typeof(Action<UnityEngine.Object, int, bool>));
        }

        private static Func<UnityEngine.Object, int, bool> EmitGetBool(Type animatorType, MethodInfo getBool)
        {
            var method = new DynamicMethod(
                "PSO_GetBool",
                typeof(bool),
                new[] { typeof(UnityEngine.Object), typeof(int) },
                typeof(ItemAudioManagerHashPatch).Module,
                true);
            ILGenerator il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, animatorType);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Callvirt, getBool);
            il.Emit(OpCodes.Ret);
            return (Func<UnityEngine.Object, int, bool>)method.CreateDelegate(
                typeof(Func<UnityEngine.Object, int, bool>));
        }

        private static Func<UnityEngine.Object, float> EmitPropertyGetter(
            Type ownerType, MethodInfo getter, string name)
        {
            var method = new DynamicMethod(
                "PSO_Get_" + name,
                typeof(float),
                new[] { typeof(UnityEngine.Object) },
                typeof(ItemAudioManagerHashPatch).Module,
                true);
            ILGenerator il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, ownerType);
            il.Emit(OpCodes.Callvirt, getter);
            il.Emit(OpCodes.Ret);
            return (Func<UnityEngine.Object, float>)method.CreateDelegate(typeof(Func<UnityEngine.Object, float>));
        }

        private static Action<UnityEngine.Object, float> EmitPropertySetter(
            Type ownerType, MethodInfo setter, string name)
        {
            var method = new DynamicMethod(
                "PSO_Set_" + name,
                typeof(void),
                new[] { typeof(UnityEngine.Object), typeof(float) },
                typeof(ItemAudioManagerHashPatch).Module,
                true);
            ILGenerator il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, ownerType);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Callvirt, setter);
            il.Emit(OpCodes.Ret);
            return (Action<UnityEngine.Object, float>)method.CreateDelegate(typeof(Action<UnityEngine.Object, float>));
        }
    }
}
