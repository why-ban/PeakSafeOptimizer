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
    /// Line-for-line replacement of PlayerEyeLook.Update() (audited source
    /// PlayerEyeLook.cs:58-115) with two mechanical differences: the per-frame
    /// GetComponent&lt;AnimatedMouth&gt;() at PlayerEyeLook.cs:70 is served from a per-Character
    /// cache (the audited IL confirms exactly one call site), and the eye material write uses the
    /// int property id from Shader.PropertyToID("_EyePosition") instead of the string overload.
    /// Material.SetVector(string, Vector4) is itself just SetColorImpl(Shader.PropertyToID(name),
    /// value) (Material.cs:1849-1857), so hoisting the id out of the loop is exact.
    ///
    /// Everything else is preserved verbatim:
    ///   - characters = Character.AllCharacters and distance = +Infinity every frame
    ///   - full scan every frame (no frequency reduction)
    ///   - nearest-distance branch first, then the speaking override branch, in that order,
    ///     including the fact that a later speaking candidate overrides an earlier one
    ///   - distance / character / lookDir / lookDelta / UpDelta / RightDelta / lookAngle
    ///     written with the same values at the same points
    ///   - InverseTransformDirection call kept (its return value is discarded upstream too)
    ///   - eyeTarget branches, the lastCharacter 0.3f smoothing factor, eyePos lerp,
    ///     eyeRenderers material writes and the lastViewDir threshold update
    ///
    /// Lifetime. The cache is a ConditionalWeakTable&lt;Character, MouthEntry&gt;, and the entry holds
    /// the AnimatedMouth through a WeakReference&lt;AnimatedMouth&gt; rather than a strong field. That
    /// matters because a non-ghost mouth carries a managed back-reference to the very Character used as
    /// the key: AnimatedMouth.character (AnimatedMouth.cs:43) is assigned GetComponent&lt;Character&gt;()
    /// in Start (AnimatedMouth.cs:72), and this patch resolves the mouth with
    /// candidate.GetComponent&lt;AnimatedMouth&gt;() on the same GameObject. A strong field would
    /// therefore have formed MouthEntry -&gt; Mouth -&gt; Mouth.character -&gt; key, the value-to-key
    /// cycle AGENTS.md forbids for CWT state classes. Through a weak reference the value does not
    /// reference its key at all, so the rule is satisfied structurally and the collectability of a
    /// dropped Character does not depend on whether the GC's ephemeron sweep is implemented correctly.
    ///
    /// Why the weak reference is also free of behavioural risk: Unity roots the managed shell of a
    /// MonoBehaviour from the native side for as long as the component is attached to a live
    /// GameObject, so while the mouth exists the weak target cannot disappear. If it ever does - the
    /// component destroyed, or the shell genuinely collected - <see cref="ResolveMouth"/> falls into the
    /// path that was already there for a Unity-destroyed component and re-runs
    /// GetComponent&lt;AnimatedMouth&gt;(), i.e. the degradation is "pay the lookup the vanilla method
    /// pays every frame anyway". The Unity null check on the retrieved target is kept as well, because a
    /// destroyed component still has a live managed shell that the weak reference would hand back.
    ///
    /// The component itself still has to be held (weakly) rather than projected to something smaller:
    /// the hot path needs the live per-frame value of AnimatedMouth.isSpeaking plus Unity's
    /// destroyed-object null semantics, and nothing narrower - a bool, a Renderer, a cached position -
    /// reproduces either.
    /// </summary>
    internal static class EyeLookComponentCachePatch
    {
        /// <summary>
        /// Per-Character cache slot. The mouth is held through a weak reference so that this value
        /// never references its own ConditionalWeakTable key - see the type remarks for the
        /// AnimatedMouth.character back-reference that would otherwise close the cycle.
        ///
        /// The reference is created lazily, on the first *successful* resolve, and only re-targeted with
        /// SetTarget afterwards. So the steady-state hot path allocates nothing: one WeakReference per
        /// Character for the lifetime of that Character, and TryGetTarget only reads a GCHandle.
        ///
        /// Created lazily rather than in a field initializer, and never handed a null target, because
        /// the shipped mscorlib builds WeakReference&lt;T&gt; on GCHandle
        /// (WeakReference`1: <c>handle = GCHandle.Alloc(target, GCHandleType.Weak)</c>) and its
        /// SetTarget writes straight through that handle: GCHandle.Target's setter takes the
        /// <c>CanDereferenceHandle(handle)</c> branch - <c>((nint)handle &amp; 1) == 0</c>, which is true
        /// for IntPtr.Zero - and then does <c>SetRef(handle, value)</c>, an unchecked store through the
        /// raw pointer. Whether Mono's GetTargetHandle icall returns a non-zero handle for a null object
        /// is not visible from managed code, and if it returned zero, a SetTarget on such an instance
        /// would store through a null pointer. Constructing only with a real target removes the question
        /// entirely, at the cost of one extra null check per resolve.
        ///
        /// The field is left with its implicit initialization to null, so MouthEntry keeps the implicit
        /// parameterless constructor that ConditionalWeakTable.GetOrCreateValue needs (it activates
        /// TValue through Activator.CreateInstance&lt;TValue&gt;()).
        ///
        /// Accepted cost of the weak reference: WeakReference&lt;T&gt; has a finalizer (it frees its
        /// GCHandle), so one finalizable object per Character joins the finalizer queue when that
        /// Character is collected. That is a handful of objects over a session against a GetComponent
        /// removed from a per-frame, per-character loop, and it is the price of not having a value-to-key
        /// cycle.
        /// </summary>
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
                // The original would dereference these; defer so behavior stays identical.
                RateLimitedDiagnostics.Hit("EyeLookComponentCache.local-or-roster-unavailable");
                return true;
            }

            // Validate every fallback dependency before the first field write: a candidate whose
            // mouth cannot be resolved must fall back to the untouched method *before* this patch
            // starts mutating PlayerEyeLook state, otherwise a partial write would be left behind.
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
                    // The original would throw a NullReferenceException on component.isSpeaking.
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

            // PlayerEyeLook.cs:60-61
            charactersRef(__instance) = all;
            __instance.distance = float.PositiveInfinity;

            // PlayerEyeLook.cs:62-76
            for (int i = 0; i < all.Count; i++)
            {
                Character candidate = all[i];
                // Pre-validated above: a null candidate already deferred to the original before any
                // state write. This is only a defensive guard so a mid-method roster mutation cannot
                // throw after this patch has started writing.
                if (candidate == null)
                    continue;

                float num = Vector3.Distance(candidate.Center, local.Center);
                if (num < __instance.distance && candidate != local)
                {
                    __instance.distance = num;
                    __instance.character = candidate;
                }

                // Only change vs. the original: cached component lookup (guaranteed non-null above).
                AnimatedMouth mouth = ResolveMouth(mouths, candidate);

                if (num < __instance.listenRange && mouth.isSpeaking && candidate != local)
                {
                    __instance.distance = num;
                    __instance.character = candidate;
                }
            }

            Character selected = __instance.character;

            // PlayerEyeLook.cs:77-85
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

            // PlayerEyeLook.cs:86-100
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

            // PlayerEyeLook.cs:101-110
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

            // PlayerEyeLook.cs:111-114
            Bodypart finalHead = getBodypartRef(local, BodypartType.Head);
            if (finalHead != null && finalHead.transform != null)
            {
                Vector3 forwardNow = finalHead.transform.forward;
                if (Vector3.Distance(lastViewDirRef(__instance), forwardNow) > __instance.xLookThreshold)
                    lastViewDirRef(__instance) = forwardNow;
            }
            return false;
        }

        /// <summary>
        /// The one behavioural difference from the original: the per-frame
        /// GetComponent&lt;AnimatedMouth&gt;() at PlayerEyeLook.cs:70 is served from the cache.
        ///
        /// Returns exactly what GetComponent would: the attached AnimatedMouth, or null when there is
        /// none. Two conditions have to hold before the cached value may be used - the weak target must
        /// still be alive, and it must pass Unity's null check. The second is not redundant: a
        /// Destroy()d component keeps a live managed shell, which the weak reference would hand back
        /// while <c>cached != null</c> is false, so this is the path that re-resolves after a rig is torn
        /// down and rebuilt. A Character with no AnimatedMouth at all is not cached as a negative - the
        /// reference stays null and every frame re-runs GetComponent, exactly as the original does - but
        /// that case never reaches the loop, because the pre-validation pass defers the whole frame to
        /// the untouched method when any candidate resolves to null.
        /// </summary>
        private static AnimatedMouth ResolveMouth(
            ConditionalWeakTable<Character, MouthEntry> mouths,
            Character candidate)
        {
            MouthEntry entry = mouths.GetOrCreateValue(candidate);
            WeakReference<AnimatedMouth> reference = entry.Ref;
            if (reference != null && reference.TryGetTarget(out AnimatedMouth cached) && cached != null)
                return cached;

            // Missing, Unity-destroyed, or (in principle) collected component: re-resolve exactly like
            // the original would.
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
