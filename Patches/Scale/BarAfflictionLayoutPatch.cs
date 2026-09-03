using System;
using System.Reflection;
using PeakSafeOptimizer.Diagnostics;
using UnityEngine;

namespace PeakSafeOptimizer.Patches.Scale
{
    /// <summary>
    /// Redundant-layout-write filter for BarAffliction.UpdateAffliction(StaminaBar). BarAffliction.width
    /// is a property whose getter reads rtf.sizeDelta.x and whose setter writes rtf.sizeDelta, dirtying
    /// the Canvas layout even when the value does not move. StaminaBar.Update calls UpdateAffliction on
    /// every affliction bar every frame, so a converged bar keeps re-dirtying the layout.
    ///
    /// The game has since added two branches of its own to UpdateAffliction:
    ///
    ///   if (size == 0f)                        width = 0f;
    ///   else if (Approximately(width, size))   width = size;
    ///   else                                   width = Lerp(width, size, Min(deltaTime*10f, 0.1f));
    ///
    /// Both new branches still *write* width - they only changed which value is written - so they do not
    /// do what this patch does and the layout dirtying is unchanged. What they do change is the value
    /// this patch has to predict, so the Prefix now reproduces all three branches and skips the call
    /// only when the branch the game would take would store a value bit-identical to the one already in
    /// rtf.sizeDelta.x.
    ///
    /// Per branch:
    ///   - size == 0f: the game stores literal 0f. Skipped when width already holds +0f, compared by
    ///     bit pattern so a stored -0f still reaches the original.
    ///   - Approximately(width, size): the game stores size verbatim. Skipped when width is bit-for-bit
    ///     size. When they are merely approximately equal the call is handed back, so the snap the game
    ///     intends still happens - this patch never suppresses convergence.
    ///   - otherwise: the same Lerp is computed here and compared bit-for-bit against the current
    ///     width, exactly as the previous revision did for the then-unconditional Lerp.
    ///
    /// The width getter is read once and reused for the branch test and the comparison, which is what
    /// the game does too in the Approximately branch (it reads width twice, but the second read cannot
    /// differ - nothing writes rtf.sizeDelta in between).
    ///
    /// One honest cost, on the size == 0f branch only: the game writes width there without reading it,
    /// so this Prefix adds a native sizeDelta *read* in order to decide. It buys the removal of a
    /// native sizeDelta *write* whenever the bar is already collapsed, which is the steady state for
    /// every affliction the character does not currently have - i.e. most of the array, every frame.
    /// A read does not dirty the Canvas layout and a write does, which is why the trade is taken.
    ///
    /// rtf is only used as an aliveness test, so it is read through a UnityEngine.Object-typed accessor
    /// and its declared type is asserted by full name. That keeps the check identical whether
    /// RectTransform is reached through a referenced assembly or a type-forwarding facade.
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
            // The branch selector the game added; asserted so a further change to it fails closed
            // instead of letting this Prefix predict the wrong value.
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
            // Bit comparison rather than ==, in all three branches. == would call -0f equal to the +0f
            // the game is about to store and let this Prefix skip a write that does change the field,
            // which is the one way a redundant-write filter can be wrong.
            int widthBits = BitConverter.SingleToInt32Bits(width);

            if (size == 0f)
            {
                // Game stores the literal 0f. 0 is the bit pattern of +0f.
                return widthBits != 0;
            }

            if (Mathf.Approximately(width, size))
            {
                // Game snaps width to size. Only skippable when it is already exactly there. When they
                // are merely approximately equal the call is handed back, so the snap still happens.
                return widthBits != BitConverter.SingleToInt32Bits(size);
            }

            float next = Mathf.Lerp(width, size, Mathf.Min(Time.deltaTime * 10f, 0.1f));
            return widthBits != BitConverter.SingleToInt32Bits(next);
        }
    }
}
