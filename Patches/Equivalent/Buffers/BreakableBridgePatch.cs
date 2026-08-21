using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using PeakSafeOptimizer.Patches.Equivalent.MathAndScan;

namespace PeakSafeOptimizer.Patches.Equivalent.Buffers
{
    internal static class BreakableBridgePatch
    {
        private sealed class Pattern
        {
            internal FieldInfo DictionaryField;
            internal FieldInfo SnapshotField;
            internal MethodInfo GetKeys;
            internal MethodInfo ToList;
            internal MethodInfo RefreshSnapshot;
        }

        private static AccessTools.FieldRef<BreakableBridge, Dictionary<Character, float>> _dictionary;
        private static AccessTools.FieldRef<BreakableBridge, List<Character>> _snapshot;
        private static Pattern _pattern;

        internal static void Initialize()
        {
            Pattern pattern = CreatePattern();
            RunMatcherSelfTest(pattern);

            AccessTools.FieldRef<BreakableBridge, Dictionary<Character, float>> dictionary =
                AccessTools.FieldRefAccess<BreakableBridge, Dictionary<Character, float>>(
                    pattern.DictionaryField);
            AccessTools.FieldRef<BreakableBridge, List<Character>> snapshot =
                AccessTools.FieldRefAccess<BreakableBridge, List<Character>>(
                    pattern.SnapshotField);
            if (dictionary == null || snapshot == null)
                throw new InvalidOperationException("BreakableBridge FieldRef creation failed");

            MethodInfo target = ResolveAndValidateTarget();
            List<CodeInstruction> original = IlPatternTools.GetOriginalInstructions(target);
            RequireSingleMatch(original, pattern);

            _dictionary = dictionary;
            _snapshot = snapshot;
            _pattern = pattern;
        }

        internal static MethodInfo ResolveAndValidateTarget()
        {
            return MathAndScanValidation.RequireInstanceMethod(
                typeof(BreakableBridge), "FixedUpdate", typeof(void), Type.EmptyTypes, false);
        }

        internal static void Reset()
        {
            _dictionary = null;
            _snapshot = null;
            _pattern = null;
        }

        internal static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions)
        {
            Pattern pattern = _pattern ?? throw new InvalidOperationException(
                "BreakableBridge transpiler ran before successful initialization");
            var rewritten = new List<CodeInstruction>(instructions);
            int match = RequireSingleMatch(rewritten, pattern);
            IlPatternTools.ValidateRemovalSafety(rewritten, match, match + 6);

            var loadInstance = new CodeInstruction(OpCodes.Ldarg_0);
            IlPatternTools.CopyMetadata(rewritten[match], loadInstance);
            var replacement = new List<CodeInstruction>
            {
                loadInstance,
                new CodeInstruction(OpCodes.Call, pattern.RefreshSnapshot)
            };
            rewritten.RemoveRange(match, 6);
            rewritten.InsertRange(match, replacement);

            if (FindMatches(rewritten, pattern).Count != 0)
                throw new InvalidOperationException(
                    "BreakableBridge IL rewrite did not consume its unique pattern");
            return rewritten;
        }

        private static void RefreshSnapshot(BreakableBridge instance)
        {
            Dictionary<Character, float> dictionary = _dictionary(instance);
            List<Character> snapshot = _snapshot(instance);
            if (dictionary == null)
                throw new InvalidOperationException("BreakableBridge peopleOnBridgeDict is unexpectedly null");

            if (snapshot == null)
            {
                // The original assigns a fresh List here (BreakableBridge.cs:135), so a null
                // snapshot is repaired by writing a new list back instead of throwing every frame.
                snapshot = new List<Character>();
                _snapshot(instance) = snapshot;
            }
            else
            {
                snapshot.Clear();
            }

            foreach (Character character in dictionary.Keys)
                snapshot.Add(character);
        }

        private static Pattern CreatePattern()
        {
            Type dictionaryType = typeof(Dictionary<Character, float>);
            Type snapshotType = typeof(List<Character>);
            FieldInfo dictionaryField = MathAndScanValidation.RequireInstanceField(
                typeof(BreakableBridge), "peopleOnBridgeDict", dictionaryType);
            FieldInfo snapshotField = MathAndScanValidation.RequireInstanceField(
                typeof(BreakableBridge), "cachedPeopleOnBridgeList", snapshotType);
            if (!dictionaryField.IsInitOnly)
                throw new InvalidOperationException(
                    "BreakableBridge.peopleOnBridgeDict is unexpectedly mutable");

            PropertyInfo keysProperty = dictionaryType.GetProperty(
                "Keys", BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
            MethodInfo getKeys = keysProperty?.GetGetMethod(false);
            if (keysProperty == null || getKeys == null || getKeys.IsStatic ||
                keysProperty.PropertyType != typeof(Dictionary<Character, float>.KeyCollection))
            {
                throw new InvalidOperationException("Dictionary<Character,float>.Keys structure mismatch");
            }

            MethodInfo toListDefinition = null;
            MethodInfo[] enumerableMethods = typeof(System.Linq.Enumerable).GetMethods(
                BindingFlags.Static | BindingFlags.Public);
            for (int i = 0; i < enumerableMethods.Length; i++)
            {
                MethodInfo candidate = enumerableMethods[i];
                if (candidate.Name != "ToList" || !candidate.IsGenericMethodDefinition ||
                    candidate.GetGenericArguments().Length != 1 || candidate.GetParameters().Length != 1)
                    continue;
                if (toListDefinition != null)
                    throw new InvalidOperationException("Enumerable.ToList signature was not unique");
                toListDefinition = candidate;
            }
            if (toListDefinition == null)
                throw new MissingMethodException(typeof(System.Linq.Enumerable).FullName, "ToList");
            MethodInfo toList = toListDefinition.MakeGenericMethod(typeof(Character));

            MethodInfo refresh = typeof(BreakableBridgePatch).GetMethod(
                nameof(RefreshSnapshot), BindingFlags.Static | BindingFlags.NonPublic);
            if (refresh == null || refresh.ReturnType != typeof(void))
                throw new MissingMethodException(
                    typeof(BreakableBridgePatch).FullName, nameof(RefreshSnapshot));

            return new Pattern
            {
                DictionaryField = dictionaryField,
                SnapshotField = snapshotField,
                GetKeys = getKeys,
                ToList = toList,
                RefreshSnapshot = refresh
            };
        }

        private static int RequireSingleMatch(
            IReadOnlyList<CodeInstruction> instructions,
            Pattern pattern)
        {
            List<int> matches = FindMatches(instructions, pattern);
            if (matches.Count != 1)
                throw new InvalidOperationException(
                    $"BreakableBridge expected exactly one audited snapshot block, found {matches.Count}");
            return matches[0];
        }

        private static List<int> FindMatches(
            IReadOnlyList<CodeInstruction> instructions,
            Pattern pattern)
        {
            var matches = new List<int>();
            for (int i = 0; i + 5 < instructions.Count; i++)
            {
                if (IlPatternTools.IsLoadArgumentZero(instructions[i]) &&
                    IlPatternTools.IsLoadArgumentZero(instructions[i + 1]) &&
                    IlPatternTools.IsField(instructions[i + 2], OpCodes.Ldfld, pattern.DictionaryField) &&
                    IlPatternTools.IsCall(instructions[i + 3], OpCodes.Callvirt, pattern.GetKeys) &&
                    IlPatternTools.IsCall(instructions[i + 4], OpCodes.Call, pattern.ToList) &&
                    IlPatternTools.IsField(instructions[i + 5], OpCodes.Stfld, pattern.SnapshotField))
                {
                    matches.Add(i);
                }
            }
            return matches;
        }

        private static void RunMatcherSelfTest(Pattern pattern)
        {
            var valid = new List<CodeInstruction>
            {
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Ldfld, pattern.DictionaryField),
                new CodeInstruction(OpCodes.Callvirt, pattern.GetKeys),
                new CodeInstruction(OpCodes.Call, pattern.ToList),
                new CodeInstruction(OpCodes.Stfld, pattern.SnapshotField)
            };
            if (FindMatches(valid, pattern).Count != 1)
                throw new InvalidOperationException(
                    "BreakableBridge matcher positive self-test failed");

            var tampered = new List<CodeInstruction>();
            for (int i = 0; i < valid.Count; i++)
                tampered.Add(new CodeInstruction(valid[i]));
            tampered[4].opcode = OpCodes.Nop;
            tampered[4].operand = null;
            if (FindMatches(tampered, pattern).Count != 0)
                throw new InvalidOperationException(
                    "BreakableBridge matcher negative self-test failed");
        }
    }
}
