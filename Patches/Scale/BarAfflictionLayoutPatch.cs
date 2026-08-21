using System;
using System.Reflection;
using PeakSafeOptimizer.Diagnostics;
using UnityEngine;

namespace PeakSafeOptimizer.Patches.Scale
{
    /// <summary>
    /// Redundant-layout-write filter for BarAffliction.UpdateAffliction() (audited source
    /// BarAffliction.cs:58-61). The width property is a property (BarAffliction.cs:17-27) whose
    /// getter reads rtf.sizeDelta.x and whose setter writes rtf.sizeDelta, dirtying the Canvas
    /// layout, even after the bar has converged to its target size. The Prefix computes the exact
    /// vanilla Lerp result and skips only when that result is bit-for-bit equal to the current width,
    /// making the setter write a true no-op.
    ///
    /// rtf is read through a UnityEngine.Object accessor because RectTransform lives in
    /// UnityEngine.UIModule, which this project does not reference.
    /// </summary>
    internal static class BarAfflictionLayoutPatch
    {
        private static Func<BarAffliction, UnityEngine.Object> _getRtf;

        internal static void Initialize()
        {
            ResolveAndValidateTarget();

            FieldInfo rtfField = ScaleValidation.RequireInstanceFieldByTypeName(
                typeof(BarAffliction), "rtf", "UnityEngine.RectTransform");
            ScaleValidation.RequireInstanceProperty(typeof(BarAffliction), "width", typeof(float));
            ScaleValidation.RequireInstanceField(typeof(BarAffliction), "size", typeof(float));

            Func<BarAffliction, UnityEngine.Object> getRtf =
                ScaleEmit.CreateUnityObjectFieldGetter<BarAffliction>(rtfField);
            if (getRtf == null)
                throw new InvalidOperationException("BarAfflictionLayout accessor creation failed");

            _getRtf = getRtf;
        }

        internal static MethodInfo ResolveAndValidateTarget()
        {
            return ScaleValidation.RequireInstanceMethod(
                typeof(BarAffliction),
                "UpdateAffliction",
                typeof(void),
                new[] { typeof(StaminaBar) },
                true);
        }

        internal static void Reset()
        {
            _getRtf = null;
        }

        internal static bool Prefix(BarAffliction __instance)
        {
            Func<BarAffliction, UnityEngine.Object> getRtf = _getRtf;
            if (getRtf == null)
            {
                RateLimitedDiagnostics.Hit("BarAfflictionLayout.accessors-unavailable");
                return true;
            }
            if (getRtf(__instance) == null)
            {
                RateLimitedDiagnostics.Hit("BarAfflictionLayout.rtf-null");
                return true;
            }

            float width = __instance.width;
            float next = Mathf.Lerp(width, __instance.size, Mathf.Min(Time.deltaTime * 10f, 0.1f));
            if (next == width)
                return false;

            return true;
        }
    }
}
