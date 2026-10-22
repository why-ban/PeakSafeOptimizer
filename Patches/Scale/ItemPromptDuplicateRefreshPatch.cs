using System;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace PeakSafeOptimizer.Patches.Scale
{
    // UpdateItems issues UpdateItemPrompts unconditionally later in the same LateUpdate, so the call this
    // method would make is a duplicate whenever UpdateItems gets that far. Both memo writes are reproduced
    // verbatim, including the second overwriting the first, so the fields match vanilla, and the skip is
    // gated on UpdateItems' own first guard so it never drops the only refresh of a frame
    internal static class ItemPromptDuplicateRefreshPatch
    {
        private static Action<GUIManager, bool> _setCanUsePrimaryPrevious;
        private static Action<GUIManager, bool> _setCanUseSecondaryPrevious;

        internal static void Initialize()
        {
            ResolveAndValidateTarget();

            Type owner = typeof(GUIManager);
            FieldInfo primary = ScaleValidation.RequireInstanceField(
                owner, "canUsePrimaryPrevious", typeof(bool));
            FieldInfo secondary = ScaleValidation.RequireInstanceField(
                owner, "canUseSecondaryPrevious", typeof(bool));
            ScaleValidation.RequireInstanceMethod(
                owner, "UpdateItemPrompts", typeof(void), Type.EmptyTypes, true);
            ScaleValidation.RequireInstanceMethod(
                owner, "UpdateItems", typeof(void), Type.EmptyTypes, true);
            ScaleValidation.RequireInstanceMethod(
                owner, "LateUpdate", typeof(void), Type.EmptyTypes, false);
            ScaleValidation.RequireStaticField(typeof(Character), "localCharacter", typeof(Character));
            ScaleValidation.RequireStaticProperty(
                typeof(Character), "observedCharacter", typeof(Character));
            ScaleValidation.RequireInstanceField(typeof(Character), "data", typeof(CharacterData));
            ScaleValidation.RequireInstanceProperty(typeof(CharacterData), "currentItem", typeof(Item));
            ScaleValidation.RequireInstanceMethod(
                typeof(Item), "CanUsePrimary", typeof(bool), Type.EmptyTypes, true);
            ScaleValidation.RequireInstanceMethod(
                typeof(Item), "CanUseSecondary", typeof(bool), Type.EmptyTypes, true);

            Action<GUIManager, bool> setPrimary = EmitBoolFieldSetter(primary);
            Action<GUIManager, bool> setSecondary = EmitBoolFieldSetter(secondary);
            if (setPrimary == null || setSecondary == null)
                throw new InvalidOperationException("ItemPromptDuplicateRefresh accessor creation failed");

            _setCanUsePrimaryPrevious = setPrimary;
            _setCanUseSecondaryPrevious = setSecondary;
        }

        internal static MethodInfo ResolveAndValidateTarget()
        {
            return ScaleValidation.RequireInstanceMethod(
                typeof(GUIManager), "TestUpdateItemPrompts", typeof(void), Type.EmptyTypes, false);
        }

        internal static void Reset()
        {
            _setCanUsePrimaryPrevious = null;
            _setCanUseSecondaryPrevious = null;
        }

        internal static bool Prefix(GUIManager __instance)
        {
            Action<GUIManager, bool> setPrimary = _setCanUsePrimaryPrevious;
            Action<GUIManager, bool> setSecondary = _setCanUseSecondaryPrevious;
            if (setPrimary == null || setSecondary == null)
                return true;

            // Mirrors UpdateItems' first guard: below it, UpdateItems returns without refreshing
            if (Character.observedCharacter == null)
                return true;

            Character local = Character.localCharacter;
            if (local == null)
            {
                setPrimary(__instance, false);
                setSecondary(__instance, false);
                return false;
            }

            CharacterData data = local.data;
            if (data == null)
                return true;

            Item current = data.currentItem;
            if (current == null)
            {
                setPrimary(__instance, false);
                setSecondary(__instance, false);
                return false;
            }

            bool primary = current.CanUsePrimary();
            bool secondary = current.CanUseSecondary();
            // Vanilla assigns canUsePrimaryPrevious twice; the second write wins, so it is repeated here
            setPrimary(__instance, primary);
            setPrimary(__instance, secondary);
            return false;
        }

        private static Action<GUIManager, bool> EmitBoolFieldSetter(FieldInfo field)
        {
            if (field == null || field.IsStatic || field.DeclaringType != typeof(GUIManager) ||
                field.FieldType != typeof(bool))
            {
                throw new InvalidOperationException($"Unexpected bool field: {field?.Name}");
            }

            var method = new DynamicMethod(
                "PSO_Set_" + field.Name,
                typeof(void),
                new[] { typeof(GUIManager), typeof(bool) },
                typeof(ItemPromptDuplicateRefreshPatch).Module,
                true);
            ILGenerator il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Stfld, field);
            il.Emit(OpCodes.Ret);
            return (Action<GUIManager, bool>)method.CreateDelegate(typeof(Action<GUIManager, bool>));
        }
    }
}
