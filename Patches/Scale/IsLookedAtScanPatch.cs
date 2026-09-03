using System;
using System.Reflection;
using HarmonyLib;
using PeakSafeOptimizer.Diagnostics;
using UnityEngine;

namespace PeakSafeOptimizer.Patches.Scale
{
    /// <summary>
    /// Full equivalent replacement of IsLookedAt.Update().
    ///
    /// The current game body is:
    ///
    ///   bool visible = false;
    ///   if (mouth.character.data.isBlind || mouth.character.refs.afflictions.isStruggling) {
    ///       GUIManager.instance.playerNames.UpdateName(index, playerNamePos.position, visible,
    ///                                                  mouth.amplitudeIndex);
    ///       return;
    ///   }
    ///   float num  = Vector3.Distance(MainCamera.instance.transform.position, transform.position);
    ///   float num2 = Vector3.Angle(MainCamera.instance.transform.forward,
    ///                              transform.position - MainCamera.instance.transform.position);
    ///   if (num < visibleDistance && num2 < visibleAngle +
    ///       (visibleDistance - num) / visibleDistance * angleDistRatio) visible = true;
    ///   GUIManager.instance.playerNames.UpdateName(index, playerNamePos.position, visible,
    ///                                              mouth.amplitudeIndex);
    ///
    /// The blind/struggling test used to sit *after* the distance and angle work and merely forced
    /// visible back to false; the game now hoists it to the top and returns early, which is the same
    /// optimization this lane would have made. This Prefix therefore reproduces the new control flow,
    /// including the fact that the early path still calls UpdateName and still touches neither
    /// MainCamera.instance nor this component's own transform.
    ///
    /// What is still optimized on top of the current body:
    ///   - Vector3.Angle is only evaluated when num &lt; visibleDistance. The game evaluates it
    ///     unconditionally and then discards it through the &amp;&amp; short circuit. Angle is pure
    ///     (a sqrt, a dot and an Acos), so deferring it cannot change the outcome.
    ///   - MainCamera.instance.transform is resolved once instead of three times, and this component's
    ///     transform.position once instead of twice. Each of those is a native call.
    ///   - the delta is computed once and used for both the distance and the angle. Vanilla builds it
    ///     separately for the angle. `delta.magnitude` squares the same three component differences
    ///     Vector3.Distance does, with the sign flipped before squaring, so the float result is
    ///     identical rather than merely close.
    ///
    /// Any null that would otherwise change the outcome defers to the original via return true.
    /// </summary>
    internal static class IsLookedAtScanPatch
    {
        private static AccessTools.FieldRef<IsLookedAt, int> _index;

        internal static void Initialize()
        {
            ResolveAndValidateTarget();

            Type owner = typeof(IsLookedAt);
            FieldInfo indexField = ScaleValidation.RequireInstanceField(owner, "index", typeof(int));
            ScaleValidation.RequireInstanceField(owner, "mouth", typeof(AnimatedMouth));
            ScaleValidation.RequireInstanceField(owner, "visibleDistance", typeof(float));
            ScaleValidation.RequireInstanceField(owner, "visibleAngle", typeof(float));
            ScaleValidation.RequireInstanceField(owner, "angleDistRatio", typeof(float));
            ScaleValidation.RequireInstanceField(owner, "playerNamePos", typeof(Transform));

            ScaleValidation.RequireStaticField(typeof(MainCamera), "instance", typeof(MainCamera));
            // The math this Prefix substitutes: the game calls Vector3.Distance on the two positions,
            // the Prefix takes the magnitude of the delta it already builds for the angle. Both members
            // are asserted so either half changing shape fails closed.
            ScaleValidation.RequireStaticMethod(
                typeof(Vector3), "Distance", typeof(float), new[] { typeof(Vector3), typeof(Vector3) }, true);
            ScaleValidation.RequireStaticMethod(
                typeof(Vector3), "Angle", typeof(float), new[] { typeof(Vector3), typeof(Vector3) }, true);
            ScaleValidation.RequireInstanceProperty(typeof(Vector3), "magnitude", typeof(float));
            ScaleValidation.RequireInstanceField(typeof(AnimatedMouth), "character", typeof(Character));
            ScaleValidation.RequireInstanceField(typeof(AnimatedMouth), "amplitudeIndex", typeof(int));
            ScaleValidation.RequireInstanceField(typeof(Character), "data", typeof(CharacterData));
            ScaleValidation.RequireInstanceField(typeof(Character), "refs", typeof(Character.CharacterRefs));
            ScaleValidation.RequireInstanceField(typeof(CharacterData), "isBlind", typeof(bool));
            ScaleValidation.RequireInstanceField(
                typeof(Character.CharacterRefs), "afflictions", typeof(CharacterAfflictions));
            ScaleValidation.RequireInstanceProperty(
                typeof(CharacterAfflictions), "isStruggling", typeof(bool));
            ScaleValidation.RequireStaticField(typeof(GUIManager), "instance", typeof(GUIManager));
            ScaleValidation.RequireInstanceField(typeof(GUIManager), "playerNames", typeof(UIPlayerNames));
            ScaleValidation.RequireInstanceMethod(
                typeof(UIPlayerNames),
                "UpdateName",
                typeof(void),
                new[] { typeof(int), typeof(Vector3), typeof(bool), typeof(int) },
                true);
            // UIPlayerNames.Init used to return the slot index; it now takes an Action-of-int and
            // invokes it from a coroutine once the character name has resolved. This patch neither
            // calls Init nor depends on when index is assigned - it forwards whatever the field holds,
            // exactly as the original does - so the assertion is not here to protect a read.
            //
            // It is here as a build-shape gate. This Prefix is a full replacement of Update, and it
            // reproduces the *hoisted* blind/struggling gate: on that path it must not touch
            // MainCamera.instance, because the current Update does not. The previous Update shape
            // computed the distance and angle first and only then forced visible to false, so on that
            // shape a null MainCamera.instance threw. Both shapes produce identical UpdateName
            // arguments, which means nothing at runtime can tell this Prefix which one it is patching -
            // and Init's signature is the cheapest checkable member that changed in the same build as
            // the hoist. If it ever reverts, this patch is skipped instead of quietly swallowing an
            // exception the game would have raised.
            ScaleValidation.RequireInstanceMethod(
                typeof(UIPlayerNames),
                "Init",
                typeof(void),
                new[] { typeof(CharacterInteractible), typeof(Action<int>) },
                true);

            AccessTools.FieldRef<IsLookedAt, int> index =
                AccessTools.FieldRefAccess<IsLookedAt, int>(indexField);
            if (index == null)
                throw new InvalidOperationException("IsLookedAtScan accessor creation failed");

            _index = index;
        }

        internal static MethodInfo ResolveAndValidateTarget()
        {
            return ScaleValidation.RequireInstanceMethod(
                typeof(IsLookedAt), "Update", typeof(void), Type.EmptyTypes, false);
        }

        internal static void Reset()
        {
            _index = null;
        }

        internal static bool Prefix(IsLookedAt __instance)
        {
            AccessTools.FieldRef<IsLookedAt, int> index = _index;
            if (index == null)
            {
                RateLimitedDiagnostics.Hit("IsLookedAtScan.accessors-unavailable");
                return true;
            }

            AnimatedMouth mouth = __instance.mouth;
            if (mouth == null)
            {
                RateLimitedDiagnostics.Hit("IsLookedAtScan.mouth-null");
                return true;
            }

            Character character = mouth.character;
            if (character == null)
            {
                RateLimitedDiagnostics.Hit("IsLookedAtScan.character-null");
                return true;
            }

            Character.CharacterRefs refs = character.refs;
            if (refs == null)
            {
                RateLimitedDiagnostics.Hit("IsLookedAtScan.refs-null");
                return true;
            }

            Transform namePos = __instance.playerNamePos;
            if (namePos == null)
            {
                RateLimitedDiagnostics.Hit("IsLookedAtScan.playerNamePos-null");
                return true;
            }

            GUIManager gui = GUIManager.instance;
            if (gui == null)
            {
                RateLimitedDiagnostics.Hit("IsLookedAtScan.gui-null");
                return true;
            }

            UIPlayerNames names = gui.playerNames;
            if (names == null)
            {
                RateLimitedDiagnostics.Hit("IsLookedAtScan.playerNames-null");
                return true;
            }

            // Hoisted gate, matching the current body: on this path the original touches neither
            // MainCamera.instance nor this component's transform, so neither is resolved here either.
            if (character.data.isBlind || refs.afflictions.isStruggling)
            {
                names.UpdateName(index(__instance), namePos.position, false, mouth.amplitudeIndex);
                return false;
            }

            MainCamera camera = MainCamera.instance;
            if (camera == null)
            {
                RateLimitedDiagnostics.Hit("IsLookedAtScan.camera-null");
                return true;
            }

            Transform cameraTransform = camera.transform;
            Vector3 delta = __instance.transform.position - cameraTransform.position;

            bool visible = false;
            float num = delta.magnitude;
            if (num < __instance.visibleDistance)
            {
                float num2 = Vector3.Angle(cameraTransform.forward, delta);
                if (num2 < __instance.visibleAngle +
                    (__instance.visibleDistance - num) / __instance.visibleDistance * __instance.angleDistRatio)
                {
                    visible = true;
                }
            }

            names.UpdateName(index(__instance), namePos.position, visible, mouth.amplitudeIndex);
            return false;
        }
    }
}
