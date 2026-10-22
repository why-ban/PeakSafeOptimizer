using System;
using System.Reflection;
using PeakSafeOptimizer.Diagnostics;
using UnityEngine;

namespace PeakSafeOptimizer.Patches.Scale
{
    /// <summary>
    /// BarAffliction.width's setter writes rtf.sizeDelta, which dirties the Canvas layout even when the
    /// value does not move. This predicts the branch the original would take and skips the call only
    /// when that branch would store a value bit-identical to the one already there.
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
            ScaleValidation.RequireStaticMethod(
                typeof(Mathf), "Approximately", typeof(bool), new[] { typeof(float), typeof(float) }, true);
            ScaleValidation.RequireStaticMethod(
                typeof(Mathf), "Lerp", typeof(float),
                new[] { typeof(float), typeof(float), typeof(float) }, true);
            ScaleValidation.RequireStaticMethod(
                typeof(Mathf), "Min", typeof(float), new[] { typeof(float), typeof(float) }, true);

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
            float size = __instance.size;
            // Bit comparison, not ==, because == calls -0f equal to the +0f the game stores
            int widthBits = BitConverter.SingleToInt32Bits(width);

            if (size == 0f)
            {
                return widthBits != 0;
            }

            if (Mathf.Approximately(width, size))
            {
                return widthBits != BitConverter.SingleToInt32Bits(size);
            }

            float next = Mathf.Lerp(width, size, Mathf.Min(Time.deltaTime * 10f, 0.1f));
            return widthBits != BitConverter.SingleToInt32Bits(next);
        }
    }
}
