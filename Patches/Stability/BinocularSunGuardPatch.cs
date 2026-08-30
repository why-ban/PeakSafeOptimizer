using System;
using System.Collections.Generic;
using System.Reflection;
using PeakSafeOptimizer.Diagnostics;
using UnityEngine;
using Zorro.Core;

namespace PeakSafeOptimizer.Patches.Stability
{
    /// <summary>
    /// Null guard for Action_ShowBinocularOverlay.TestLookAtSun() (audited source
    /// Action_ShowBinocularOverlay.cs:40-55), reached every frame from Update while anyone holds
    /// raised binoculars (:15-21, gated on <c>binocularsActive &amp;&amp; !isProp</c>).
    ///
    /// The method exists only to award the astronomy badge for looking at the sun on Mesa. Its guard
    /// clause null-checks exactly one of the things it then dereferences:
    ///
    ///   if (Character.localCharacter == null
    ///       || Singleton&lt;MapHandler&gt;.Instance.GetCurrentBiome() != Biome.BiomeType.Mesa
    ///       || DayNightManager.instance.sun.intensity &lt; 5f) return;
    ///
    /// <c>Singleton&lt;T&gt;.Instance</c> returns null while shutting down and before Awake
    /// (Zorro\Core\Singleton.cs:14-33; MapHandler does not implement IFindIfNull, so there is no
    /// find-on-demand fallback), <c>DayNightManager.instance</c> is a plain static assigned in Awake
    /// with no guard (DayNightManager.cs:34,162), and <c>sun</c> is a serialized field that can be
    /// unassigned. Any of those throws. Confirmed in-game: 1544 NullReferenceException in a single
    /// session - roughly one per frame for as long as the binoculars were up, each one paying for an
    /// exception, a stack capture and a synchronous log write on the main thread. The airport is the
    /// obvious producer: no MapHandler exists there, and binoculars can be raised.
    ///
    /// Past the guard clause the method dereferences three more things without a check:
    /// <c>MainCamera.instance</c> (:47; MainCamera.cs:5,18 - a plain static assigned in Awake),
    /// <c>Character.localCharacter.Center</c> (:49) and
    /// <c>Singleton&lt;AchievementManager&gt;.Instance</c> (:52). <c>Character.Center</c> is not a
    /// null risk but a KeyNotFoundException risk: it routes through GetBodypart, which indexes
    /// CharacterRagdoll.partDict directly (Character.cs:266,1299-1302) and throws before the ragdoll
    /// has registered its parts - the same hazard that Patches\Scale\StatusEmitterScanPatch.cs and
    /// Patches\Scale\ParticleCountCullerWritePatch.cs treat as real. All three are checked here.
    ///
    /// Ordering matters for diagnostic honesty. The <c>||</c> chain in the guard clause short-circuits,
    /// so a non-Mesa biome or a dim sun makes the original return without ever touching
    /// DayNightManager, MainCamera, Center or AchievementManager. A Prefix that checked those
    /// unconditionally would count a "repair reason" on frames where the original is unharmed - for
    /// example every frame with binoculars up in the airport would blame MainCamera as well as
    /// MapHandler. So this Prefix reproduces the guard clause first (biome, then sun intensity) and
    /// only evaluates the later checks when the original would actually reach them. The
    /// <c>RateLimitedDiagnostics</c> counters are quoted as measured evidence of vanilla defects, so a
    /// false positive costs more than the branch it saves.
    ///
    /// Cost of reproducing the guard clause: <c>GetCurrentBiome()</c> now runs twice per frame per
    /// raised binocular, once here and once in the original. It is
    /// <c>segments[currentSegment].biome</c> (MapHandler.cs:1032-1035): an array index plus, on a
    /// segment with <c>hasVariant</c>, a <c>List&lt;BiomeType&gt;.Contains</c> over the handful of
    /// detected biomes (MapHandler.cs:44-54,1037-1040,119). No allocation, no loop over anything
    /// scene-sized, so the duplicate call is cheaper than the branch misprediction it replaces and far
    /// cheaper than the exception it prevents.
    ///
    /// Known limitation of that cost argument, one case where the duplicate call is not free. On a
    /// segment with <c>hasVariant</c> the <c>biome</c> getter calls
    /// <c>GetVariantSegmentFromBiome(variantBiomeIndex)</c> once inside the <c>BiomeIsPresent</c> test
    /// and again to return the value (MapHandler.cs:48,50), and that method logs
    /// <c>Debug.LogError("Requested biome index is out of range of variant segments...")</c> whenever
    /// the index falls outside <c>variantSegments</c> (MapHandler.cs:1042-1050, error at :1046). So on
    /// a segment whose <c>variantBiomeIndex</c> (MapHandler.cs:40) is misconfigured, a single
    /// <c>GetCurrentBiome()</c> already writes one error line, and two when the fallback
    /// <c>segments[0].biome</c> also passes <c>BiomeIsPresent</c>. This Prefix doubles whichever of
    /// those it is, so up to four error log writes per frame per raised binocular. Accepted rather
    /// than worked around: the condition is a scene-data defect that produces the same spam from every
    /// other <c>biome</c>, <c>segmentParent</c>, <c>segmentCampfire</c> and <c>dayNightProfile</c> read
    /// in the frame (MapHandler.cs:56-90), and the alternative - not reproducing the guard clause -
    /// reintroduces the diagnostic over-counting argued for above.
    ///
    /// This Prefix is read-only. It skips the frame only when the original would dereference
    /// something that is unavailable, and otherwise returns true so the original runs unchanged. The
    /// only consequence of a skipped frame is that the badge check does not happen on that frame.
    /// A missing local character is not counted at all: the original's own first operand handles that
    /// case, so there is no vanilla defect to account for. <c>GetCurrentBiome()</c> is additionally
    /// wrapped in a catch-all, because reproducing it here means this Prefix now executes vanilla code
    /// that can raise on its own (an unset <c>segments</c> array, an out-of-range
    /// <c>currentSegment</c>); an exception escaping a Prefix would propagate through Harmony into
    /// Update and cost more than the frame it skips.
    ///
    /// Residual imprecision, deliberately not removed, both of a kind that can only over-count:
    ///
    ///   * <c>Center</c> and <c>Singleton&lt;AchievementManager&gt;.Instance</c> sit behind two
    ///     further tests in the original - the 10-degree look-at-sun angle (:47) and the line check
    ///     (:49-50). The line check cannot be reproduced in a Prefix without firing the raycast
    ///     twice, and reproducing the angle alone would duplicate a second piece of vanilla logic
    ///     while still leaving the raycast unreproduced. So on Mesa with a bright sun those two
    ///     counters can fire while the player is not looking at the sun.
    ///   * The checks use Unity null semantics, which are also false for a destroyed-but-non-null
    ///     object. Some of those cases would not actually throw in the original - a field read on a
    ///     destroyed managed wrapper succeeds - so they are counted slightly early. The alternative,
    ///     a strict reference comparison, would let a destroyed manager through to a native access
    ///     inside the original and re-introduce the exception. Losing one frame of a badge check is
    ///     the cheaper error here, unlike in EmoteWheelGuardPatch where skipping a frame suppresses
    ///     a vanilla state write.
    ///
    /// Both windows are narrow and, unlike the airport case, only occur where the scene graph is
    /// already broken.
    /// </summary>
    internal static class BinocularSunGuardPatch
    {
        internal static void Initialize()
        {
            ResolveAndValidateTarget();

            // Every member the guard clause reads, plus the three the original dereferences unguarded
            // after it.
            StabilityPatchValidation.RequireInstanceMethod(
                typeof(MapHandler), "GetCurrentBiome", typeof(Biome.BiomeType), Type.EmptyTypes);
            if (!Enum.IsDefined(typeof(Biome.BiomeType), Biome.BiomeType.Mesa))
                throw new InvalidOperationException("Biome.BiomeType.Mesa is no longer defined");
            // Hand-rolled because StabilityPatchValidation has no static-field helper. Checks match
            // Patches\Scale\ScaleValidation.RequireStaticField (:105-113): DeclaredOnly in the binding
            // flags plus an explicit DeclaringType test, so a same-named static inherited from a
            // future base class cannot satisfy the check. Public-only rather than that helper's
            // Public|NonPublic, and deliberately so: the Prefix reads these statics through directly
            // compiled access, which a demoted field would no longer permit. Declared on
            // DayNightManager itself (DayNightManager.cs:34).
            FieldInfo instanceField = typeof(DayNightManager).GetField(
                "instance",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.DeclaredOnly);
            if (instanceField == null || !instanceField.IsStatic ||
                instanceField.DeclaringType != typeof(DayNightManager) ||
                instanceField.FieldType != typeof(DayNightManager))
            {
                throw new InvalidOperationException("DayNightManager.instance failed structural self-check");
            }
            StabilityPatchValidation.RequireInstanceField(typeof(DayNightManager), "sun", typeof(Light));

            // Declared on MainCamera itself (MainCamera.cs:5).
            FieldInfo cameraInstance = typeof(MainCamera).GetField(
                "instance",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.DeclaredOnly);
            if (cameraInstance == null || !cameraInstance.IsStatic ||
                cameraInstance.DeclaringType != typeof(MainCamera) ||
                cameraInstance.FieldType != typeof(MainCamera))
            {
                throw new InvalidOperationException("MainCamera.instance failed structural self-check");
            }
            StabilityPatchValidation.RequireInstanceMethod(
                typeof(AchievementManager),
                "ThrowAchievement",
                typeof(void),
                new[] { typeof(ACHIEVEMENTTYPE) },
                requirePublic: false);

            // The Center chain. Character.Center is asserted because this is the value the torso
            // lookup below stands in for; the rest is the non-throwing route to it, the same one
            // Patches\ScaleExperimental\ExplosionScalePatch.cs and RemoteRagdollLod.cs take.
            // Declared on Character itself (Character.cs:117); same DeclaredOnly + DeclaringType
            // shape as the two static fields above.
            FieldInfo localCharacterField = typeof(Character).GetField(
                "localCharacter",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.DeclaredOnly);
            if (localCharacterField == null || !localCharacterField.IsStatic ||
                localCharacterField.DeclaringType != typeof(Character) ||
                localCharacterField.FieldType != typeof(Character))
            {
                throw new InvalidOperationException("Character.localCharacter failed structural self-check");
            }
            StabilityPatchValidation.RequireInstanceProperty(typeof(Character), "Center", typeof(Vector3));
            StabilityPatchValidation.RequireInstanceField(
                typeof(Character), "refs", typeof(Character.CharacterRefs));
            StabilityPatchValidation.RequireInstanceField(
                typeof(Character.CharacterRefs), "ragdoll", typeof(CharacterRagdoll));
            StabilityPatchValidation.RequireInstanceField(
                typeof(CharacterRagdoll), "partDict", typeof(Dictionary<BodypartType, Bodypart>));
            // Bodypart.transform is inherited from Component, which StabilityPatchValidation's
            // declared-only property lookup cannot express, so it is checked directly here in the
            // same style as the two static fields above.
            PropertyInfo bodypartTransform = typeof(Bodypart).GetProperty(
                "transform", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            MethodInfo bodypartTransformGetter = bodypartTransform?.GetGetMethod(true);
            if (bodypartTransform == null || bodypartTransform.PropertyType != typeof(Transform) ||
                bodypartTransform.GetIndexParameters().Length != 0 ||
                bodypartTransformGetter == null || bodypartTransformGetter.IsStatic)
            {
                throw new InvalidOperationException("Bodypart.transform failed structural self-check");
            }
            if (!Enum.IsDefined(typeof(BodypartType), BodypartType.Torso))
                throw new InvalidOperationException("BodypartType.Torso is no longer defined");
        }

        internal static MethodInfo ResolveAndValidateTarget()
        {
            return StabilityPatchValidation.RequireInstanceMethod(
                typeof(Action_ShowBinocularOverlay),
                "TestLookAtSun",
                typeof(void),
                Type.EmptyTypes,
                requirePublic: false);
        }

        internal static void Reset()
        {
        }

        internal static bool Prefix()
        {
            // Unity null semantics throughout: false for a null reference and for a destroyed object.

            // The original's own first guard: a missing local character makes it return, so there is
            // nothing to repair and nothing to count. Pass through.
            Character local = Character.localCharacter;
            if (!(bool)local)
                return true;

            // Original :42, first unguarded dereference: Singleton<MapHandler>.Instance.GetCurrentBiome().
            MapHandler mapHandler = Singleton<MapHandler>.Instance;
            if (!(bool)(UnityEngine.Object)mapHandler)
            {
                RateLimitedDiagnostics.Hit("BinocularSunGuard.mapHandler-unavailable");
                return false;
            }

            Biome.BiomeType biome;
            try
            {
                biome = mapHandler.GetCurrentBiome();
            }
            catch (Exception)
            {
                // segments unset or currentSegment out of range: the original's own call would raise
                // the same exception one instruction later, so skipping is strictly the cheaper
                // outcome. Caught rather than propagated so nothing unexpected escapes the Prefix
                // into Harmony's wrapper.
                RateLimitedDiagnostics.Hit("BinocularSunGuard.biome-query-failed");
                return false;
            }
            if (biome != Biome.BiomeType.Mesa)
            {
                // Original short-circuits at the second operand and returns; DayNightManager,
                // MainCamera, Center and AchievementManager are never touched.
                return true;
            }

            // Original :42, third operand: DayNightManager.instance.sun.intensity.
            DayNightManager dayNight = DayNightManager.instance;
            if (!(bool)dayNight)
            {
                RateLimitedDiagnostics.Hit("BinocularSunGuard.dayNightManager-unavailable");
                return false;
            }
            Light sun = dayNight.sun;
            if (!(bool)sun)
            {
                RateLimitedDiagnostics.Hit("BinocularSunGuard.sun-unavailable");
                return false;
            }
            if (sun.intensity < 5f)
            {
                // Original returns here; nothing past the guard clause runs.
                return true;
            }

            // Original :47, unguarded: MainCamera.instance.transform.
            if (!(bool)MainCamera.instance)
            {
                RateLimitedDiagnostics.Hit("BinocularSunGuard.mainCamera-unavailable");
                return false;
            }

            // Original :49, unguarded: Character.localCharacter.Center, which indexes partDict and
            // raises KeyNotFoundException before the ragdoll registers its parts. Resolved here
            // through TryGetValue so the check itself cannot throw.
            if (!TorsoIsAvailable(local))
            {
                RateLimitedDiagnostics.Hit("BinocularSunGuard.center-unavailable");
                return false;
            }

            // Original :52, unguarded: Singleton<AchievementManager>.Instance.ThrowAchievement.
            if (!(bool)(UnityEngine.Object)Singleton<AchievementManager>.Instance)
            {
                RateLimitedDiagnostics.Hit("BinocularSunGuard.achievementManager-unavailable");
                return false;
            }

            return true;
        }

        /// <summary>
        /// Whether Character.Center (Character.cs:266) could be read without throwing, reached the
        /// non-throwing way: GetBodypart indexes CharacterRagdoll.partDict with <c>[]</c>
        /// (Character.cs:1299-1302), TryGetValue reads the same entry and reports a miss instead.
        /// </summary>
        private static bool TorsoIsAvailable(Character local)
        {
            Character.CharacterRefs refs = local.refs;
            CharacterRagdoll ragdoll = refs != null ? refs.ragdoll : null;
            if (!(bool)ragdoll)
                return false;
            Dictionary<BodypartType, Bodypart> parts = ragdoll.partDict;
            if (parts == null || !parts.TryGetValue(BodypartType.Torso, out Bodypart torso) || !(bool)torso)
                return false;
            return (bool)torso.transform;
        }
    }
}
