using System;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace PeakSafeOptimizer.Patches.Scale
{
    // The label is a pure function of the prefab, the instance's cooked amount and the current language, so
    // an unchanged triple cannot produce a different string and vanilla's own comparison would find it equal
    // A recorded string that no longer matches the label means something else wrote it, so the call is handed
    // back. The state holds no reference to its key: a string cannot reach an InventoryItemUI
    internal static class InventoryNameTextRefreshPatch
    {
        private sealed class State
        {
            internal Item Prefab;
            internal int Cooked;
            internal int Language;
            internal string LastWritten;
        }

        private const BindingFlags DeclaredInstance = BindingFlags.Instance |
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        private static AccessTools.FieldRef<InventoryItemUI, Item> _getItemPrefab;
        private static Func<InventoryItemUI, string> _getNameText;
        private static ConditionalWeakTable<InventoryItemUI, State> _states;

        internal static void Initialize()
        {
            ResolveAndValidateTarget();

            Type owner = typeof(InventoryItemUI);
            FieldInfo prefabField = ScaleValidation.RequireInstanceField(owner, "_itemPrefab", typeof(Item));
            ScaleValidation.RequireInstanceField(owner, "_itemData", typeof(ItemInstanceData));
            FieldInfo nameTextField = ScaleValidation.RequireInstanceFieldByTypeName(
                owner, "nameText", "TMPro.TextMeshProUGUI");
            ScaleValidation.RequireInstanceMethod(owner, "SetItem", typeof(void), new[] { typeof(ItemSlot) }, true);
            ScaleValidation.RequireInstanceMethod(owner, "SetSelected", typeof(void), Type.EmptyTypes, true);
            ScaleValidation.RequireInstanceMethod(owner, "Clear", typeof(void), Type.EmptyTypes, true);
            ScaleValidation.RequireInstanceMethod(owner, "OnDisable", typeof(void), Type.EmptyTypes, false);

            // The Postfix records what vanilla wrote through this setter, so both accessors are load bearing
            PropertyInfo textProperty = ScaleValidation.RequireInstancePropertyByTypeName(
                nameTextField.FieldType, "text", typeof(string).FullName, requireSetter: true);

            ScaleValidation.RequireInstanceMethod(
                typeof(Item), "GetItemName", typeof(string), new[] { typeof(ItemInstanceData) }, true);
            ScaleValidation.RequireInstanceField(typeof(IntItemData), "Value", typeof(int));
            MethodInfo tryGetDataEntry = RequireTryGetDataEntry();
            // Closed over IntItemData so a constraint change fails here instead of inside the Prefix
            tryGetDataEntry.MakeGenericMethod(typeof(IntItemData));

            if (!Enum.IsDefined(typeof(DataEntryKey), DataEntryKey.CookedAmount))
                throw new InvalidOperationException("DataEntryKey.CookedAmount is no longer defined");

            FieldInfo languageField = ScaleValidation.RequireStaticField(
                typeof(LocalizedText), "CURRENT_LANGUAGE", typeof(LocalizedText.Language));
            // A literal would be inlined against the reference assembly, so a language change could not
            // be observed at all
            if (languageField.IsInitOnly || languageField.IsLiteral ||
                Enum.GetUnderlyingType(typeof(LocalizedText.Language)) != typeof(int))
            {
                throw new InvalidOperationException("LocalizedText.CURRENT_LANGUAGE structure mismatch");
            }

            AccessTools.FieldRef<InventoryItemUI, Item> getPrefab =
                AccessTools.FieldRefAccess<InventoryItemUI, Item>(prefabField);
            Func<InventoryItemUI, string> getNameText = EmitNameTextGetter(nameTextField, textProperty);
            if (getPrefab == null || getNameText == null)
                throw new InvalidOperationException("InventoryNameTextRefresh accessor creation failed");

            _getItemPrefab = getPrefab;
            _getNameText = getNameText;
            _states = new ConditionalWeakTable<InventoryItemUI, State>();
        }

        internal static MethodInfo ResolveAndValidateTarget()
        {
            return ScaleValidation.RequireInstanceMethod(
                typeof(InventoryItemUI), "UpdateNameText", typeof(void), Type.EmptyTypes, true);
        }

        internal static void Reset()
        {
            _getItemPrefab = null;
            _getNameText = null;
            _states = null;
        }

        internal static bool Prefix(InventoryItemUI __instance, out bool __state)
        {
            __state = false;
            AccessTools.FieldRef<InventoryItemUI, Item> getPrefab = _getItemPrefab;
            Func<InventoryItemUI, string> getNameText = _getNameText;
            ConditionalWeakTable<InventoryItemUI, State> states = _states;
            if (getPrefab == null || getNameText == null || states == null)
                return true;

            Item prefab = getPrefab(__instance);
            // A dead or absent prefab selects vanilla's cheap "" / "Backpack" branches
            if (prefab == null)
                return true;

            ItemInstanceData data = __instance._itemData;
            // GetItemName(null) reads the prefab's own data and can register an entry there
            if (data == null)
                return true;

            int cooked = 0;
            if (data.TryGetDataEntry<IntItemData>(DataEntryKey.CookedAmount, out IntItemData entry))
            {
                // Vanilla dereferences the entry unconditionally, so a null one must reach it
                if (entry == null)
                    return true;
                cooked = entry.Value;
            }
            int language = (int)LocalizedText.CURRENT_LANGUAGE;

            string current = getNameText(__instance);
            // A missing label is vanilla's to dereference
            if (current == null)
                return true;

            states.TryGetValue(__instance, out State state);
            if (state != null && ReferenceEquals(state.Prefab, prefab) && state.Cooked == cooked &&
                state.Language == language && ReferenceEquals(current, state.LastWritten))
            {
                return false;
            }

            if (state == null)
            {
                state = new State();
                states.Add(__instance, state);
            }
            state.Prefab = prefab;
            state.Cooked = cooked;
            state.Language = language;
            __state = true;
            return true;
        }

        internal static void Postfix(InventoryItemUI __instance, bool __state)
        {
            // Only a call whose key the Prefix actually validated may claim the record, so a Prefix that
            // bailed out before computing the key cannot pair a fresh string with a stale triple
            if (!__state)
                return;
            Func<InventoryItemUI, string> getNameText = _getNameText;
            ConditionalWeakTable<InventoryItemUI, State> states = _states;
            if (getNameText == null || states == null)
                return;
            if (!states.TryGetValue(__instance, out State state))
                return;
            state.LastWritten = getNameText(__instance);
        }

        private static MethodInfo RequireTryGetDataEntry()
        {
            MethodInfo found = null;
            MethodInfo[] candidates = typeof(ItemInstanceData).GetMethods(DeclaredInstance);
            for (int i = 0; i < candidates.Length; i++)
            {
                MethodInfo candidate = candidates[i];
                if (!string.Equals(candidate.Name, "TryGetDataEntry", StringComparison.Ordinal) ||
                    !candidate.IsGenericMethodDefinition || candidate.ReturnType != typeof(bool))
                {
                    continue;
                }
                Type[] genericArguments = candidate.GetGenericArguments();
                if (genericArguments.Length != 1)
                    continue;
                ParameterInfo[] parameters = candidate.GetParameters();
                if (parameters.Length != 2 || parameters[0].ParameterType != typeof(DataEntryKey))
                    continue;
                Type valueParameter = parameters[1].ParameterType;
                if (!valueParameter.IsByRef ||
                    valueParameter.GetElementType() != genericArguments[0])
                {
                    continue;
                }
                Type[] constraints = genericArguments[0].GetGenericParameterConstraints();
                if (constraints.Length != 1 || constraints[0] != typeof(DataEntryValue))
                    continue;
                if (found != null)
                    throw new InvalidOperationException("Ambiguous ItemInstanceData.TryGetDataEntry structure");
                found = candidate;
            }
            if (found == null)
                throw new MissingMethodException(typeof(ItemInstanceData).FullName, "TryGetDataEntry");
            return found;
        }

        private static Func<InventoryItemUI, string> EmitNameTextGetter(
            FieldInfo field, PropertyInfo property)
        {
            MethodInfo getter = property?.GetGetMethod(true);
            if (field == null || property == null || getter == null || getter.IsStatic ||
                field.IsStatic || field.DeclaringType != typeof(InventoryItemUI) ||
                !field.FieldType.IsClass || !property.DeclaringType.IsAssignableFrom(field.FieldType))
            {
                throw new InvalidOperationException("Unexpected InventoryItemUI.nameText structure");
            }

            var method = new DynamicMethod(
                "PSO_InventoryNameText",
                typeof(string),
                new[] { typeof(InventoryItemUI) },
                typeof(InventoryNameTextRefreshPatch).Module,
                true);
            ILGenerator il = method.GetILGenerator();
            Label alive = il.DefineLabel();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, field);
            il.Emit(OpCodes.Dup);
            il.Emit(OpCodes.Brtrue_S, alive);
            il.Emit(OpCodes.Ret);
            il.MarkLabel(alive);
            il.Emit(OpCodes.Callvirt, getter);
            il.Emit(OpCodes.Ret);
            return (Func<InventoryItemUI, string>)method.CreateDelegate(
                typeof(Func<InventoryItemUI, string>));
        }
    }
}
