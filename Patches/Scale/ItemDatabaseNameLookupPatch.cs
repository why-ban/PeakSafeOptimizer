using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using PeakSafeOptimizer.Diagnostics;

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
        private static Func<object> _getInstance;
        private static Func<object, Dictionary<ushort, Item>> _getLookup;
        private static Dictionary<string, Item> _nameCache;
        private static int _builtCount = -1;

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

            Func<object> getInstance = ResolveSingletonInstance(itemDatabaseType);
            Func<object, Dictionary<ushort, Item>> getLookup = EmitLookupGetter(itemDatabaseType, itemLookupField);
            if (getInstance == null || getLookup == null)
                throw new InvalidOperationException("ItemDatabaseNameLookup accessor creation failed");

            _getInstance = getInstance;
            _getLookup = getLookup;
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
            _nameCache = null;
            _builtCount = -1;
        }

        private static Type ResolveItemDatabaseType()
        {
            Type type = typeof(Item).Assembly.GetType("ItemDatabase", throwOnError: false);
            if (type == null)
                throw new TypeLoadException("ItemDatabase type not found");
            return type;
        }

        private static Func<object> ResolveSingletonInstance(Type itemDatabaseType)
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
                return (Func<object>)getter.CreateDelegate(typeof(Func<object>));
            }
            throw new InvalidOperationException("ItemDatabase singleton Instance getter not found");
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

        internal static bool Prefix(string itemNameOnFile, ref Item item, ref bool __result)
        {
            Func<object> getInstance = _getInstance;
            Func<object, Dictionary<ushort, Item>> getLookup = _getLookup;
            Dictionary<string, Item> cache = _nameCache;
            int builtCount = _builtCount;

            if (getInstance == null || getLookup == null)
            {
                RateLimitedDiagnostics.Hit("ItemDatabaseNameLookup.accessors-unavailable");
                return true;
            }

            object db = getInstance();
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

            int count = lookup.Count;
            if (cache == null || count != builtCount)
            {
                Dictionary<string, Item> built = new Dictionary<string, Item>(count);
                foreach (KeyValuePair<ushort, Item> pair in lookup)
                {
                    Item value = pair.Value;
                    if (value == null)
                    {
                        RateLimitedDiagnostics.Hit("ItemDatabaseNameLookup.null-entry");
                        return true;
                    }
                    built[value.gameObject.name] = value;
                }
                cache = built;
                _nameCache = built;
                _builtCount = count;
            }

            Item found;
            if (cache.TryGetValue(itemNameOnFile, out found))
            {
                item = found;
                __result = true;
            }
            else
            {
                item = null;
                __result = false;
            }
            return false;
        }
    }
}
