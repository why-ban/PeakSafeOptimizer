using System;
using System.Reflection;
using HarmonyLib;
using PeakSafeOptimizer.Diagnostics;
using UnityEngine;

namespace PeakSafeOptimizer.Patches.Scale
{
    /// <summary>
    /// Full equivalent replacement of IsLookedAt.Update: Vector3.Angle is deferred behind the distance
    /// test (it is pure), each transform is read once, and delta.magnitude equals Vector3.Distance on
    /// the same positions exactly rather than approximately.
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
            // Build-shape gate, not a read guard: this Prefix must not touch MainCamera.instance on the blind path
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
