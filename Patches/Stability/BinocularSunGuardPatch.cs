using System;
using System.Reflection;
using PeakSafeOptimizer.Diagnostics;
using UnityEngine;
using Zorro.Core;

namespace PeakSafeOptimizer.Patches.Stability
{
    /// <summary>
    /// Null guard for Action_ShowBinocularOverlay.TestLookAtSun() (audited source
    /// Action_ShowBinocularOverlay.cs:39-54), reached every frame from Update while anyone holds
    /// raised binoculars (:15-21, gated on <c>binocularsActive &amp;&amp; !isProp</c>).
    ///
    /// The method exists only to award the astronomy badge for looking at the sun on Mesa. Its guard
    /// clause null-checks exactly one of the three things it then dereferences:
    ///
    ///   if (Character.localCharacter == null
    ///       || Singleton&lt;MapHandler&gt;.Instance.GetCurrentBiome() != Biome.BiomeType.Mesa
    ///       || DayNightManager.instance.sun.intensity &lt; 5f) return;
    ///
    /// <c>Singleton&lt;T&gt;.Instance</c> returns null while shutting down or before Awake
    /// (Zorro\Core\Singleton.cs:15-32), <c>DayNightManager.instance</c> is a plain static assigned in
    /// Awake with no guard (DayNightManager.cs:34), and <c>sun</c> is a serialized field that can be
    /// unassigned. Any of the three throws. Confirmed in-game: 1544 NullReferenceException in a single
    /// session - roughly one per frame for as long as the binoculars were up, each one paying for an
    /// exception, a stack capture and a synchronous log write on the main thread.
    ///
    /// This Prefix is read-only. It skips the frame only when one of those three is unavailable, and
    /// otherwise returns true so the original runs unchanged. The only consequence of a skipped frame
    /// is that the badge check does not happen on that frame.
    /// </summary>
    internal static class BinocularSunGuardPatch
    {
        internal static void Initialize()
        {
            ResolveAndValidateTarget();

            // Every member the guard reads, plus the two the original dereferences unguarded.
            StabilityPatchValidation.RequireInstanceMethod(
                typeof(MapHandler), "GetCurrentBiome", typeof(Biome.BiomeType), Type.EmptyTypes);
            FieldInfo instanceField = typeof(DayNightManager).GetField(
                "instance", BindingFlags.Static | BindingFlags.Public);
            if (instanceField == null || !instanceField.IsStatic ||
                instanceField.FieldType != typeof(DayNightManager))
            {
                throw new InvalidOperationException("DayNightManager.instance failed structural self-check");
            }
            StabilityPatchValidation.RequireInstanceField(typeof(DayNightManager), "sun", typeof(Light));
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
            // Unity null semantics: false for a null reference and for a destroyed object.
            if (!(bool)(UnityEngine.Object)Singleton<MapHandler>.Instance)
            {
                RateLimitedDiagnostics.Hit("BinocularSunGuard.mapHandler-unavailable");
                return false;
            }

            DayNightManager dayNight = DayNightManager.instance;
            if (!(bool)dayNight)
            {
                RateLimitedDiagnostics.Hit("BinocularSunGuard.dayNightManager-unavailable");
                return false;
            }
            if (!(bool)dayNight.sun)
            {
                RateLimitedDiagnostics.Hit("BinocularSunGuard.sun-unavailable");
                return false;
            }

            return true;
        }
    }
}
