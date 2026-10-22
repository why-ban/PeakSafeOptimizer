using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using PeakSafeOptimizer.Diagnostics;
using PeakSafeOptimizer.Patches.Scale;
using UnityEngine;

namespace PeakSafeOptimizer.Patches.ScaleExperimental
{
    /// <summary>
    /// Equivalent replacement of PlayerEyeLook.Update: the per-candidate GetComponent<AnimatedMouth> and the
    /// repeated GetBodypart(Head) lookups are resolved once and cached, the mouth through a
    /// ConditionalWeakTable holding a WeakReference so a destroyed or replaced one is re-resolved. Selection,
    /// the speaking override and every written field are identical; a missing accessor or null defers.
    /// </summary>
    internal static class EyeLookComponentCachePatch
    {
        private sealed class MouthEntry
        {
            internal WeakReference<AnimatedMouth> Ref;
        }

        private static ConditionalWeakTable<Character, MouthEntry> _mouths;
        private static AccessTools.FieldRef<PlayerEyeLook, List<Character>> _characters;
        private static AccessTools.FieldRef<PlayerEyeLook, Character> _localCharacter;
        private static AccessTools.FieldRef<PlayerEyeLook, Character> _lastCharacter;
        private static AccessTools.FieldRef<PlayerEyeLook, Vector3> _lookDir;
        private static AccessTools.FieldRef<PlayerEyeLook, Vector3> _lookDelta;
        private static AccessTools.FieldRef<PlayerEyeLook, float> _upDelta;
        private static AccessTools.FieldRef<PlayerEyeLook, float> _rightDelta;
        private static AccessTools.FieldRef<PlayerEyeLook, Vector2> _eyePos;
        private static AccessTools.FieldRef<PlayerEyeLook, Vector2> _eyeTarget;
        private static AccessTools.FieldRef<PlayerEyeLook, Vector3> _lastViewDir;
        private static Func<Character, BodypartType, Bodypart> _getBodypart;
        private static int _eyePositionId;

        internal static void Initialize()
        {
            ResolveAndValidateTarget();

            Type eyeLook = typeof(PlayerEyeLook);
            FieldInfo charactersField = ScaleValidation.RequireInstanceField(
                eyeLook, "characters", typeof(List<Character>));
            FieldInfo localCharacterField = ScaleValidation.RequireInstanceField(
                eyeLook, "localCharacter", typeof(Character));
            FieldInfo lastCharacterField = ScaleValidation.RequireInstanceField(
                eyeLook, "lastCharacter", typeof(Character));
            FieldInfo lookDirField = ScaleValidation.RequireInstanceField(eyeLook, "lookDir", typeof(Vector3));
            FieldInfo lookDeltaField = ScaleValidation.RequireInstanceField(eyeLook, "lookDelta", typeof(Vector3));
            FieldInfo upDeltaField = ScaleValidation.RequireInstanceField(eyeLook, "UpDelta", typeof(float));
            FieldInfo rightDeltaField = ScaleValidation.RequireInstanceField(eyeLook, "RightDelta", typeof(float));
            FieldInfo eyePosField = ScaleValidation.RequireInstanceField(eyeLook, "eyePos", typeof(Vector2));
            FieldInfo eyeTargetField = ScaleValidation.RequireInstanceField(eyeLook, "eyeTarget", typeof(Vector2));
            FieldInfo lastViewDirField = ScaleValidation.RequireInstanceField(eyeLook, "lastViewDir", typeof(Vector3));

            ScaleValidation.RequireInstanceField(eyeLook, "character", typeof(Character));
            ScaleValidation.RequireInstanceField(eyeLook, "distance", typeof(float));
            ScaleValidation.RequireInstanceField(eyeLook, "lookRange", typeof(float));
            ScaleValidation.RequireInstanceField(eyeLook, "listenRange", typeof(float));
            ScaleValidation.RequireInstanceField(eyeLook, "lookAngle", typeof(float));
            ScaleValidation.RequireInstanceField(eyeLook, "lookAngleMax", typeof(float));
            ScaleValidation.RequireInstanceField(eyeLook, "lookSmoothing", typeof(float));
            ScaleValidation.RequireInstanceField(eyeLook, "xLookThreshold", typeof(float));
            ScaleValidation.RequireInstanceField(eyeLook, "XMax", typeof(float));
            ScaleValidation.RequireInstanceField(eyeLook, "YMax", typeof(float));
            ScaleValidation.RequireInstanceField(eyeLook, "lookingAtCharacter", typeof(bool));
            ScaleValidation.RequireInstanceField(eyeLook, "eyeRenderers", typeof(Renderer[]));

            ScaleValidation.RequireStaticField(typeof(Character), "AllCharacters", typeof(List<Character>));
            ScaleValidation.RequireInstanceProperty(typeof(Character), "Center", typeof(Vector3));
            ScaleValidation.RequireInstanceProperty(typeof(Character), "Head", typeof(Vector3));
            ScaleValidation.RequireInstanceField(typeof(Character), "data", typeof(CharacterData));
            ScaleValidation.RequireInstanceField(typeof(CharacterData), "lookDirection", typeof(Vector3));
            MethodInfo getBodypartMethod = ScaleValidation.RequireInstanceMethod(
                typeof(Character), "GetBodypart", typeof(Bodypart), new[] { typeof(BodypartType) }, false);
            ScaleValidation.RequireInstanceField(typeof(AnimatedMouth), "isSpeaking", typeof(bool));
            if (!Enum.IsDefined(typeof(BodypartType), BodypartType.Head) ||
                !Enum.IsDefined(typeof(BodypartType), BodypartType.Hip))
            {
                throw new InvalidOperationException("BodypartType.Head/Hip is no longer defined");
            }

            AccessTools.FieldRef<PlayerEyeLook, List<Character>> characters =
                AccessTools.FieldRefAccess<PlayerEyeLook, List<Character>>(charactersField);
            AccessTools.FieldRef<PlayerEyeLook, Character> localCharacter =
                AccessTools.FieldRefAccess<PlayerEyeLook, Character>(localCharacterField);
            AccessTools.FieldRef<PlayerEyeLook, Character> lastCharacter =
                AccessTools.FieldRefAccess<PlayerEyeLook, Character>(lastCharacterField);
            AccessTools.FieldRef<PlayerEyeLook, Vector3> lookDir =
                AccessTools.FieldRefAccess<PlayerEyeLook, Vector3>(lookDirField);
            AccessTools.FieldRef<PlayerEyeLook, Vector3> lookDelta =
                AccessTools.FieldRefAccess<PlayerEyeLook, Vector3>(lookDeltaField);
            AccessTools.FieldRef<PlayerEyeLook, float> upDelta =
                AccessTools.FieldRefAccess<PlayerEyeLook, float>(upDeltaField);
            AccessTools.FieldRef<PlayerEyeLook, float> rightDelta =
                AccessTools.FieldRefAccess<PlayerEyeLook, float>(rightDeltaField);
            AccessTools.FieldRef<PlayerEyeLook, Vector2> eyePos =
                AccessTools.FieldRefAccess<PlayerEyeLook, Vector2>(eyePosField);
            AccessTools.FieldRef<PlayerEyeLook, Vector2> eyeTarget =
                AccessTools.FieldRefAccess<PlayerEyeLook, Vector2>(eyeTargetField);
            AccessTools.FieldRef<PlayerEyeLook, Vector3> lastViewDir =
                AccessTools.FieldRefAccess<PlayerEyeLook, Vector3>(lastViewDirField);
            Func<Character, BodypartType, Bodypart> getBodypart =
                ScaleExperimentalEmit.CreateDelegate<Func<Character, BodypartType, Bodypart>>(getBodypartMethod);
            if (characters == null || localCharacter == null || lastCharacter == null || lookDir == null ||
                lookDelta == null || upDelta == null || rightDelta == null || eyePos == null ||
                eyeTarget == null || lastViewDir == null || getBodypart == null)
            {
                throw new InvalidOperationException("EyeLookComponentCache accessor creation failed");
            }

            _mouths = new ConditionalWeakTable<Character, MouthEntry>();
            _characters = characters;
            _localCharacter = localCharacter;
            _lastCharacter = lastCharacter;
            _lookDir = lookDir;
            _lookDelta = lookDelta;
            _upDelta = upDelta;
            _rightDelta = rightDelta;
            _eyePos = eyePos;
            _eyeTarget = eyeTarget;
            _lastViewDir = lastViewDir;
            _getBodypart = getBodypart;
            _eyePositionId = Shader.PropertyToID("_EyePosition");
        }

        internal static MethodInfo ResolveAndValidateTarget()
        {
            return ScaleValidation.RequireInstanceMethod(
                typeof(PlayerEyeLook), "Update", typeof(void), Type.EmptyTypes, false);
        }

        internal static void Reset()
        {
            _mouths = null;
            _characters = null;
            _localCharacter = null;
            _lastCharacter = null;
            _lookDir = null;
            _lookDelta = null;
            _upDelta = null;
            _rightDelta = null;
            _eyePos = null;
            _eyeTarget = null;
            _lastViewDir = null;
            _getBodypart = null;
            _eyePositionId = 0;
        }

        internal static bool Prefix(PlayerEyeLook __instance)
        {
            ConditionalWeakTable<Character, MouthEntry> mouths = _mouths;
            AccessTools.FieldRef<PlayerEyeLook, List<Character>> charactersRef = _characters;
            AccessTools.FieldRef<PlayerEyeLook, Character> localCharacterRef = _localCharacter;
            AccessTools.FieldRef<PlayerEyeLook, Character> lastCharacterRef = _lastCharacter;
            AccessTools.FieldRef<PlayerEyeLook, Vector3> lookDirRef = _lookDir;
            AccessTools.FieldRef<PlayerEyeLook, Vector3> lookDeltaRef = _lookDelta;
            AccessTools.FieldRef<PlayerEyeLook, float> upDeltaRef = _upDelta;
            AccessTools.FieldRef<PlayerEyeLook, float> rightDeltaRef = _rightDelta;
            AccessTools.FieldRef<PlayerEyeLook, Vector2> eyePosRef = _eyePos;
            AccessTools.FieldRef<PlayerEyeLook, Vector2> eyeTargetRef = _eyeTarget;
            AccessTools.FieldRef<PlayerEyeLook, Vector3> lastViewDirRef = _lastViewDir;
            Func<Character, BodypartType, Bodypart> getBodypartRef = _getBodypart;
            if (mouths == null || charactersRef == null || localCharacterRef == null ||
                lastCharacterRef == null || lookDirRef == null || lookDeltaRef == null ||
                upDeltaRef == null || rightDeltaRef == null || eyePosRef == null ||
                eyeTargetRef == null || lastViewDirRef == null || getBodypartRef == null)
            {
                RateLimitedDiagnostics.Hit("EyeLookComponentCache.accessors-unavailable");
                return true;
            }

            Character local = localCharacterRef(__instance);
            List<Character> all = Character.AllCharacters;
            if (local == null || all == null)
            {
                RateLimitedDiagnostics.Hit("EyeLookComponentCache.local-or-roster-unavailable");
                return true;
            }

            for (int i = 0; i < all.Count; i++)
            {
                Character candidate = all[i];
                if (candidate == null)
                {
                    RateLimitedDiagnostics.Hit("EyeLookComponentCache.candidate-null");
                    return true;
                }
                if (ResolveMouth(mouths, candidate) == null)
                {
                    RateLimitedDiagnostics.Hit("EyeLookComponentCache.mouth-unavailable");
                    return true;
                }
            }

            Renderer[] renderers = __instance.eyeRenderers;
            if (renderers == null)
            {
                RateLimitedDiagnostics.Hit("EyeLookComponentCache.eyeRenderers-null");
                return true;
            }
            for (int i = 0; i < renderers.Length; i++)
            {
                if (renderers[i] == null)
                {
                    RateLimitedDiagnostics.Hit("EyeLookComponentCache.eyeRenderer-null");
                    return true;
                }
            }

            charactersRef(__instance) = all;
            __instance.distance = float.PositiveInfinity;

            for (int i = 0; i < all.Count; i++)
            {
                Character candidate = all[i];
                if (candidate == null)
                    continue;

                float num = Vector3.Distance(candidate.Center, local.Center);
                if (num < __instance.distance && candidate != local)
                {
                    __instance.distance = num;
                    __instance.character = candidate;
                }

                AnimatedMouth mouth = ResolveMouth(mouths, candidate);

                if (num < __instance.listenRange && mouth.isSpeaking && candidate != local)
                {
                    __instance.distance = num;
                    __instance.character = candidate;
                }
            }

            Character selected = __instance.character;

            if (selected != null)
            {
                Bodypart localHead = getBodypartRef(local, BodypartType.Head);
                if (localHead == null || localHead.transform == null)
                {
                    RateLimitedDiagnostics.Hit("EyeLookComponentCache.local-head-unavailable");
                    return true;
                }
                Vector3 dir = (selected.Head - local.Head).normalized;
                lookDirRef(__instance) = dir;
                Vector3 delta = localHead.transform.forward - dir;
                lookDeltaRef(__instance) = delta;
                __instance.transform.InverseTransformDirection(delta);
                upDeltaRef(__instance) = Vector3.Dot(localHead.transform.up, lookDeltaRef(__instance));
                rightDeltaRef(__instance) = Vector3.Dot(localHead.transform.right, lookDeltaRef(__instance));
                __instance.lookAngle = Vector3.Angle(local.data.lookDirection, lookDirRef(__instance));
            }

            if (selected != null && __instance.distance < __instance.lookRange &&
                __instance.lookAngle < __instance.lookAngleMax)
            {
                eyeTargetRef(__instance) = new Vector2(
                    rightDeltaRef(__instance) * (0f - __instance.XMax),
                    upDeltaRef(__instance) * __instance.YMax);
                __instance.lookingAtCharacter = true;
            }
            else
            {
                __instance.lookingAtCharacter = false;
                Bodypart hip = getBodypartRef(local, BodypartType.Hip);
                Bodypart head = getBodypartRef(local, BodypartType.Head);
                if (hip == null || hip.transform == null || head == null || head.transform == null)
                {
                    RateLimitedDiagnostics.Hit("EyeLookComponentCache.local-bodyparts-unavailable");
                    return true;
                }
                Vector3 forward = hip.transform.forward;
                forward.y = 0f;
                Vector3 rhs = local.data.lookDirection - forward;
                float num2 = Vector3.Dot(head.transform.right, rhs);
                float num3 = Vector3.Dot(head.transform.up, rhs);
                eyeTargetRef(__instance) = new Vector2(num2 * __instance.XMax, num3 * (0f - __instance.YMax));
            }

            float num4 = 1f;
            if (selected != lastCharacterRef(__instance))
                num4 = 0.3f;
            eyePosRef(__instance) = Vector2.Lerp(
                eyePosRef(__instance),
                eyeTargetRef(__instance),
                Time.deltaTime * __instance.lookSmoothing * num4);

            Vector2 eyePos = eyePosRef(__instance);
            int eyePositionId = _eyePositionId;
            for (int j = 0; j < renderers.Length; j++)
            {
                Renderer renderer = renderers[j];
                renderer.material.SetVector(eyePositionId, eyePos);
            }

            Bodypart finalHead = getBodypartRef(local, BodypartType.Head);
            if (finalHead != null && finalHead.transform != null)
            {
                Vector3 forwardNow = finalHead.transform.forward;
                if (Vector3.Distance(lastViewDirRef(__instance), forwardNow) > __instance.xLookThreshold)
                    lastViewDirRef(__instance) = forwardNow;
            }
            return false;
        }

        private static AnimatedMouth ResolveMouth(
            ConditionalWeakTable<Character, MouthEntry> mouths,
            Character candidate)
        {
            MouthEntry entry = mouths.GetOrCreateValue(candidate);
            WeakReference<AnimatedMouth> reference = entry.Ref;
            if (reference != null && reference.TryGetTarget(out AnimatedMouth cached) && cached != null)
                return cached;

            AnimatedMouth resolved = candidate.GetComponent<AnimatedMouth>();
            if (resolved == null)
                return null;
            if (reference == null)
                entry.Ref = new WeakReference<AnimatedMouth>(resolved);
            else
                reference.SetTarget(resolved);
            return resolved;
        }
    }
}
