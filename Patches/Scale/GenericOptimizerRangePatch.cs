using System;
using System.Collections.Generic;
using System.Reflection;
using Peak;
using PeakSafeOptimizer.Diagnostics;
using UnityEngine;

namespace PeakSafeOptimizer.Patches.Scale
{
    /// <summary>
    /// Squared-distance equivalent of Peak.GenericOptimizer.AnyCharacterWithinRange(float)
    /// (audited source Peak/GenericOptimizer.cs:80-90), driven every frame per registered
    /// optimizer by ItemOptimizationManager. Dead characters are still skipped first and the
    /// strict `< range` boundary is preserved. Anything that cannot be proven equivalent
    /// (negative range, missing list) falls back to the original method.
    ///
    /// Default is false. The audited GenericOptimizer initializes
    /// rangeToDisableScripts = float.PositiveInfinity, and the infinite-range branch below has to
    /// keep the original Vector3.Distance comparison to preserve `Distance &lt; +Inf`, so on default
    /// scene data this patch performs the same math plus one extra layer of indirection. It is only
    /// worth enabling where the scene actually assigns a finite rangeToDisableScripts.
    /// </summary>
    internal static class GenericOptimizerRangePatch
    {
        internal static void Initialize()
        {
            ResolveAndValidateTarget();
            ScaleValidation.RequireStaticField(typeof(Character), "AllCharacters", typeof(List<Character>));
            ScaleValidation.RequireInstanceField(typeof(Character), "data", typeof(CharacterData));
            ScaleValidation.RequireInstanceProperty(typeof(CharacterData), "dead", typeof(bool));
            ScaleValidation.RequireInstanceProperty(typeof(Character), "Center", typeof(Vector3));
            ScaleValidation.RequireInstanceField(
                typeof(GenericOptimizer), "rangeToDisableScripts", typeof(float));
            ScaleValidation.RequireInstanceProperty(
                typeof(GenericOptimizer), "transform", typeof(Transform));
            ScaleValidation.RequireStaticMethod(
                typeof(Vector3), "Distance", typeof(float),
                new[] { typeof(Vector3), typeof(Vector3) }, true);
        }

        internal static MethodInfo ResolveAndValidateTarget()
        {
            return ScaleValidation.RequireInstanceMethod(
                typeof(GenericOptimizer),
                "AnyCharacterWithinRange",
                typeof(bool),
                new[] { typeof(float) },
                true);
        }

        internal static void Reset()
        {
        }

        internal static bool Prefix(GenericOptimizer __instance, float range, ref bool __result)
        {
            // Negative range: Distance is non-negative so the original always returns false, but
            // squaring would flip the sign information. Defer instead of guessing.
            if (!(range >= 0f))
            {
                RateLimitedDiagnostics.Hit("GenericOptimizerRange.non-positive-range-fallback");
                return true;
            }

            List<Character> all = Character.AllCharacters;
            if (all == null)
            {
                RateLimitedDiagnostics.Hit("GenericOptimizerRange.allCharacters-null");
                return true;
            }

            Transform selfTransform = __instance.transform;
            if (selfTransform == null)
            {
                RateLimitedDiagnostics.Hit("GenericOptimizerRange.transform-null");
                return true;
            }

            Vector3 origin = selfTransform.position;
            bool infiniteRange = float.IsPositiveInfinity(range);
            float rangeSqr = range * range;
            if (!infiniteRange && range > 0f && (!(rangeSqr > 0f) || float.IsInfinity(rangeSqr)))
            {
                // Squaring subnormal/huge finite ranges underflows or overflows and no longer
                // preserves the strict Vector3.Distance boundary.
                return true;
            }

            for (int i = 0; i < all.Count; i++)
            {
                Character candidate = all[i];
                if (candidate == null)
                    continue;
                CharacterData data = candidate.data;
                if (data == null)
                    continue;
                if (data.dead)
                    continue;

                if (infiniteRange)
                {
                    // range == +Inf (the audited default of rangeToDisableScripts): squaring both
                    // sides would compare Inf < Inf and lose the strict test, so keep the original
                    // Vector3.Distance comparison verbatim for this case.
                    if (Vector3.Distance(candidate.Center, origin) < range)
                    {
                        __result = true;
                        return false;
                    }
                    continue;
                }

                Vector3 delta = candidate.Center - origin;
                // range finite and >= 0 here, so sqrMagnitude < range*range matches
                // Vector3.Distance(...) < range for all finite and NaN component values.
                if (delta.sqrMagnitude < rangeSqr)
                {
                    __result = true;
                    return false;
                }
            }

            __result = false;
            return false;
        }
    }
}
