using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using PeakSafeOptimizer.Diagnostics;
using UnityEngine;

namespace PeakSafeOptimizer.Patches.Scale
{
    /// <summary>
    /// Name lookup replacement for ItemDatabase.TryGetItem(string, out Item) (audited source
    /// ItemDatabase.cs:82-93). The original scans itemLookup linearly on every call, comparing
    /// item2.Value.gameObject.name == itemNameOnFile per entry and, because it never breaks on a
    /// match, a later duplicate name overwrites an earlier one.
    ///
    /// This patch builds a Dictionary&lt;string, Item&gt; once by iterating itemLookup in the same
    /// order and overwriting on every match, which reproduces the last-match-wins semantics exactly.
    /// The cache is rebuilt whenever itemLookup.Count changes (scene reload). If the singleton is
    /// null, the lookup list is null, or any value is null, the prefix defers to the original.
    ///
    /// ItemDatabase is reached entirely through reflection: its base chain ends in
    /// Sirenix.SerializedScriptableObject (unreferenced), so the type, the static
    /// SingletonAsset&lt;ItemDatabase&gt;.Instance getter, and the itemLookup field are all resolved once
    /// during Initialize and bound to delegates so the hot path stays allocation free.
    /// </summary>
    internal static class ItemDatabaseNameLookupPatch
    {
        private static Func<UnityEngine.Object> _getInstance;
        private static Func<object, Dictionary<ushort, Item>> _getLookup;
        private static Func<Dictionary<ushort, Item>, int> _getLookupVersion;
        private static Dictionary<string, Item> _nameCache;
        private static Dictionary<ushort, Item> _cachedLookup;
        private static int _builtCount = -1;
        private static int _builtVersion = int.MinValue;

        internal static void Initialize()
        {
            ResolveAndValidateTarget();

            Type itemDatabaseType = ResolveItemDatabaseType();
            FieldInfo itemLookupField = itemDatabaseType.GetField(
                "itemLookup",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (itemLookupField == null || itemLookupField.IsStatic ||
                itemLookupField.FieldType != typeof(Dictionary<ushort, Item>))
            {
                throw new MissingFieldException("ItemDatabase", "itemLookup");
            }

            Func<UnityEngine.Object> getInstance = ResolveSingletonInstance(itemDatabaseType);
            Func<object, Dictionary<ushort, Item>> getLookup = EmitLookupGetter(itemDatabaseType, itemLookupField);
            Func<Dictionary<ushort, Item>, int> getLookupVersion = CreateDictionaryVersionGetter();
            if (getInstance == null || getLookup == null || getLookupVersion == null)
                throw new InvalidOperationException("ItemDatabaseNameLookup accessor creation failed");

            _getInstance = getInstance;
            _getLookup = getLookup;
            _getLookupVersion = getLookupVersion;
        }

        internal static MethodInfo ResolveAndValidateTarget()
        {
            Type type = ResolveItemDatabaseType();
            MethodInfo method = type.GetMethod(
                "TryGetItem",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
                null,
                new[] { typeof(string), typeof(Item).MakeByRefType() },
                null);
            if (method == null || !method.IsStatic || !method.IsPublic || method.ReturnType != typeof(bool))
                throw new MissingMethodException("ItemDatabase", "TryGetItem(string, out Item)");
            return method;
        }

        internal static void Reset()
        {
            _getInstance = null;
            _getLookup = null;
            _getLookupVersion = null;
            _nameCache = null;
            _cachedLookup = null;
            _builtCount = -1;
            _builtVersion = int.MinValue;
        }

        private static Type ResolveItemDatabaseType()
        {
            Type type = typeof(Item).Assembly.GetType("ItemDatabase", throwOnError: false);
            if (type == null)
                throw new TypeLoadException("ItemDatabase type not found");
            return type;
        }

        private static Func<UnityEngine.Object> ResolveSingletonInstance(Type itemDatabaseType)
        {
            for (Type type = itemDatabaseType.BaseType; type != null; type = type.BaseType)
            {
                PropertyInfo instance = type.GetProperty(
                    "Instance",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (instance == null)
                    continue;
                MethodInfo getter = instance.GetGetMethod(true);
                if (getter == null || !getter.IsStatic || getter.ReturnType != itemDatabaseType)
                    continue;
                return EmitSingletonGetter(getter);
            }
            throw new InvalidOperationException("ItemDatabase singleton Instance getter not found");
        }

        private static Func<UnityEngine.Object> EmitSingletonGetter(MethodInfo getter)
        {
            var method = new DynamicMethod(
                "PSO_GetItemDatabaseInstance",
                typeof(UnityEngine.Object),
                Type.EmptyTypes,
                typeof(ItemDatabaseNameLookupPatch).Module,
                true);
            ILGenerator il = method.GetILGenerator();
            il.Emit(OpCodes.Call, getter);
            il.Emit(OpCodes.Ret);
            return (Func<UnityEngine.Object>)method.CreateDelegate(typeof(Func<UnityEngine.Object>));
        }

        private static Func<object, Dictionary<ushort, Item>> EmitLookupGetter(
            Type itemDatabaseType, FieldInfo itemLookupField)
        {
            var method = new DynamicMethod(
                "PSO_GetItemLookup",
                typeof(Dictionary<ushort, Item>),
                new[] { typeof(object) },
                typeof(ItemDatabaseNameLookupPatch).Module,
                true);
            ILGenerator il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, itemDatabaseType);
            il.Emit(OpCodes.Ldfld, itemLookupField);
            il.Emit(OpCodes.Ret);
            return (Func<object, Dictionary<ushort, Item>>)method.CreateDelegate(
                typeof(Func<object, Dictionary<ushort, Item>>));
        }

        private static Func<Dictionary<ushort, Item>, int> CreateDictionaryVersionGetter()
        {
            Type dictionaryType = typeof(Dictionary<ushort, Item>);
            FieldInfo version = dictionaryType.GetField(
                "_version", BindingFlags.Instance | BindingFlags.NonPublic) ??
                dictionaryType.GetField("version", BindingFlags.Instance | BindingFlags.NonPublic);
            if (version == null || version.IsStatic || version.FieldType != typeof(int))
                throw new MissingFieldException(dictionaryType.FullName, "_version/version");

            var method = new DynamicMethod(
                "PSO_GetItemLookupVersion",
                typeof(int),
                new[] { dictionaryType },
                typeof(ItemDatabaseNameLookupPatch).Module,
                true);
            ILGenerator il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, version);
            il.Emit(OpCodes.Ret);
            return (Func<Dictionary<ushort, Item>, int>)method.CreateDelegate(
                typeof(Func<Dictionary<ushort, Item>, int>));
        }

        internal static bool Prefix(string itemNameOnFile, ref Item item, ref bool __result)
        {
            Func<UnityEngine.Object> getInstance = _getInstance;
            Func<object, Dictionary<ushort, Item>> getLookup = _getLookup;
            Func<Dictionary<ushort, Item>, int> getLookupVersion = _getLookupVersion;
            Dictionary<string, Item> cache = _nameCache;
            Dictionary<ushort, Item> cachedLookup = _cachedLookup;
            int builtCount = _builtCount;
            int builtVersion = _builtVersion;

            if (getInstance == null || getLookup == null || getLookupVersion == null)
            {
                RateLimitedDiagnostics.Hit("ItemDatabaseNameLookup.accessors-unavailable");
                return true;
            }

            UnityEngine.Object db = getInstance();
            if (db == null)
            {
                RateLimitedDiagnostics.Hit("ItemDatabaseNameLookup.instance-null");
                return true;
            }

            Dictionary<ushort, Item> lookup = getLookup(db);
            if (lookup == null)
            {
                RateLimitedDiagnostics.Hit("ItemDatabaseNameLookup.lookup-null");
                return true;
            }

            if (itemNameOnFile == null)
            {
                item = null;
                __result = false;
                return false;
            }

            int count = lookup.Count;
            int version = getLookupVersion(lookup);
            if (cache == null || lookup != cachedLookup || count != builtCount || version != builtVersion)
            {
                if (!TryBuildCache(lookup, count, out cache))
                    return true;
                _nameCache = cache;
                _cachedLookup = lookup;
                _builtCount = count;
                _builtVersion = version;
            }

            Item found;
            if (cache.TryGetValue(itemNameOnFile, out found))
            {
                // A Unity object can be destroyed or renamed without changing Dictionary._version.
                // Defer to vanilla on a stale hit so it can perform its normal linear lookup.
                if (found == null || found.gameObject == null || found.gameObject.name != itemNameOnFile)
                    return true;
                item = found;
                __result = true;
            }
            else
            {
                // A cache miss is not proof that vanilla would miss: an item can be renamed without
                // changing Dictionary._version. Let the original preserve that lookup semantics.
                return true;
            }
            return false;
        }

        private static bool TryBuildCache(
            Dictionary<ushort, Item> lookup,
            int count,
            out Dictionary<string, Item> cache)
        {
            var built = new Dictionary<string, Item>(count);
            foreach (KeyValuePair<ushort, Item> pair in lookup)
            {
                Item value = pair.Value;
                if (value == null)
                {
                    RateLimitedDiagnostics.Hit("ItemDatabaseNameLookup.null-entry");
                    cache = null;
                    return false;
                }
                if (value.gameObject == null)
                {
                    RateLimitedDiagnostics.Hit("ItemDatabaseNameLookup.entry-object-null");
                    cache = null;
                    return false;
                }
                built[value.gameObject.name] = value;
            }
            cache = built;
            return true;
        }
    }
}
