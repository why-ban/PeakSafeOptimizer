using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using PeakSafeOptimizer.Diagnostics;
using Zorro.Core.Serizalization;

namespace PeakSafeOptimizer.Patches.Stability
{
    /// <summary>
    /// Null guard for SerializableRunBasedValues.SerializeRunBasedValues, whose dictionaries only Allocate()
    /// fills. The Prefix returns void, so the original still runs and writes empty dictionaries instead of
    /// throwing, keeping the four lists after them in the payload. The repair cannot persist because the
    /// struct reaches the serializer copied out of AchievementManager.runBasedValueData, a property.
    /// </summary>
    internal static class RunBasedValuesPatch
    {
        private static AccessTools.StructFieldRef<SerializableRunBasedValues,
            Dictionary<RUNBASEDVALUETYPE, int>> _ints;
        private static AccessTools.StructFieldRef<SerializableRunBasedValues,
            Dictionary<RUNBASEDVALUETYPE, float>> _floats;

        internal static void Initialize()
        {
            Type owner = typeof(SerializableRunBasedValues);
            if (!owner.IsValueType || owner.IsEnum)
                throw new InvalidOperationException(
                    "SerializableRunBasedValues is no longer a non-enum value type");

            StabilityPatchValidation.RequireInstanceProperty(
                typeof(AchievementManager),
                "runBasedValueData",
                owner);

            FieldInfo intsField = StabilityPatchValidation.RequireInstanceField(
                owner,
                "runBasedInts",
                typeof(Dictionary<RUNBASEDVALUETYPE, int>));
            FieldInfo floatsField = StabilityPatchValidation.RequireInstanceField(
                owner,
                "runBasedFloats",
                typeof(Dictionary<RUNBASEDVALUETYPE, float>));
            if (intsField.IsInitOnly || floatsField.IsInitOnly)
                throw new InvalidOperationException(
                    "RunBasedValues dictionary fields are unexpectedly readonly");

            AccessTools.StructFieldRef<SerializableRunBasedValues,
                Dictionary<RUNBASEDVALUETYPE, int>> ints =
                AccessTools.StructFieldRefAccess<SerializableRunBasedValues,
                    Dictionary<RUNBASEDVALUETYPE, int>>(intsField);
            AccessTools.StructFieldRef<SerializableRunBasedValues,
                Dictionary<RUNBASEDVALUETYPE, float>> floats =
                AccessTools.StructFieldRefAccess<SerializableRunBasedValues,
                    Dictionary<RUNBASEDVALUETYPE, float>>(floatsField);
            if (ints == null || floats == null)
                throw new InvalidOperationException(
                    "RunBasedValues StructFieldRef accessor creation failed");

            _ints = ints;
            _floats = floats;
        }

        internal static MethodInfo ResolveAndValidateTarget()
        {
            return StabilityPatchValidation.RequireInstanceMethod(
                typeof(SerializableRunBasedValues),
                "SerializeRunBasedValues",
                typeof(void),
                new[] { typeof(BinarySerializer) });
        }

        internal static void Reset()
        {
            _ints = null;
            _floats = null;
        }

        internal static void Prefix(ref SerializableRunBasedValues __instance)
        {
            if (_ints(ref __instance) == null)
            {
                _ints(ref __instance) = new Dictionary<RUNBASEDVALUETYPE, int>();
                RateLimitedDiagnostics.Hit("RunBasedValues.runBasedInts-null");
            }

            if (_floats(ref __instance) == null)
            {
                _floats(ref __instance) = new Dictionary<RUNBASEDVALUETYPE, float>();
                RateLimitedDiagnostics.Hit("RunBasedValues.runBasedFloats-null");
            }
        }
    }
}
