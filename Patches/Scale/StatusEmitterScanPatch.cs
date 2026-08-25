using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using PeakSafeOptimizer.Diagnostics;
using UnityEngine;

namespace PeakSafeOptimizer.Patches.Scale
{
    /// <summary>
    /// Full equivalent replacement of StatusEmitter.Update() (audited source StatusEmitter.cs:56-121),
    /// the spore-gas damage volume of the swamp/jungle biome. Every emitter authored into the level
    /// runs this every frame with no distance gate of its own, so the cost scales with emitter count
    /// rather than with how many are near the player.
    ///
    /// What the original pays per emitter per frame:
    ///   * InRange() resolves Character.localCharacter.Center - a dictionary lookup into
    ///     CharacterRagdoll.partDict (Character.cs:266 -> :1299-1302) - and takes a square root via
    ///     Vector3.Distance (StatusEmitter.cs:53).
    ///   * The in-range branch resolves Center a second time and takes a second square root (:99).
    ///   * TryMakePriorityOverlappedEmitter hashes a string key into a static Dictionary and, on the
    ///     contested path, resolves Center twice more for two more square roots (:133-156).
    ///
    /// This replacement keeps every branch, every field write and every call in the audited order and
    /// changes only how often the same numbers are obtained:
    ///   * Character.localCharacter.Center is resolved once per call and reused. Within one call the
    ///     torso transform cannot move between two reads, so this is the same value the original reads
    ///     two to four times.
    ///   * The distance to that centre is likewise computed once. It is still a real square root of
    ///     the same float sum - Vector3.Distance and Mathf.Sqrt are the same intrinsic over the same
    ///     expression - so the range boolean and the fade inputs are bit-identical. A squared compare
    ///     would have been cheaper but not exact, and the disagreement band is a live damage tick.
    ///   * The overlap map is looked up once with TryGetValue instead of a ContainsKey plus three
    ///     string-keyed indexer lookups.
    ///
    /// The damage itself is untouched: amount, tickTime, the two fade curves, minAmount and the
    /// AddStatus/SubtractStatus calls are reproduced verbatim, including the `debug` field write that
    /// vanilla performs as a side effect of the clamp (:109).
    ///
    /// This patch does not touch the particle systems. Nothing in StatusEmitter references one - the
    /// clouds are authored visuals and the damage is this volume - so reducing one cannot affect the
    /// other.
    /// </summary>
    internal static class StatusEmitterScanPatch
    {
        private static AccessTools.FieldRef<StatusEmitter, float> _timeSinceLastTick;
        private static AccessTools.FieldRef<StatusEmitter, float> _tickTime;
        private static AccessTools.FieldRef<StatusEmitter, bool> _inZone;
        private static Func<Dictionary<string, StatusEmitter>> _overlapMap;

        internal static void Initialize()
        {
            ResolveAndValidateTarget();

            FieldInfo timeSinceLastTick = ScaleValidation.RequireInstanceField(
                typeof(StatusEmitter), "timeSinceLastTick", typeof(float));
            FieldInfo tickTime = ScaleValidation.RequireInstanceField(
                typeof(StatusEmitter), "tickTime", typeof(float));
            FieldInfo inZone = ScaleValidation.RequireInstanceField(
                typeof(StatusEmitter), "inZone", typeof(bool));
            FieldInfo overlapMap = ScaleValidation.RequireStaticField(
                typeof(StatusEmitter),
                "overlapPreventedStatusEmitters",
                typeof(Dictionary<string, StatusEmitter>));

            // Public fields the replacement reads or writes.
            ScaleValidation.RequireInstanceField(
                typeof(StatusEmitter), "statusType", typeof(CharacterAfflictions.STATUSTYPE));
            ScaleValidation.RequireInstanceField(typeof(StatusEmitter), "amount", typeof(float));
            ScaleValidation.RequireInstanceField(typeof(StatusEmitter), "radius", typeof(float));
            ScaleValidation.RequireInstanceField(typeof(StatusEmitter), "outerFade", typeof(float));
            ScaleValidation.RequireInstanceField(typeof(StatusEmitter), "innerFade", typeof(float));
            ScaleValidation.RequireInstanceField(typeof(StatusEmitter), "minAmount", typeof(float));
            ScaleValidation.RequireInstanceField(typeof(StatusEmitter), "inZoneWarning", typeof(bool));
            ScaleValidation.RequireInstanceField(
                typeof(StatusEmitter), "extraWarningTime", typeof(float));
            ScaleValidation.RequireInstanceField(typeof(StatusEmitter), "debug", typeof(float));
            ScaleValidation.RequireInstanceField(typeof(StatusEmitter), "preventOverlap", typeof(bool));
            ScaleValidation.RequireInstanceField(
                typeof(StatusEmitter), "overlapIdentifier", typeof(string));
            ScaleValidation.RequireInstanceField(
                typeof(StatusEmitter), "isOverlapPriority", typeof(bool));
            ScaleValidation.RequireInstanceField(
                typeof(StatusEmitter), "emitterDisabledByWind", typeof(bool));

            // InRange is virtual and the replacement must not bypass an override. The audited build
            // has exactly one subclass (WindAffectedStatusEmitter) and it does not override InRange,
            // which is what makes inlining the range test here equivalent; if that ever changes, this
            // check fails and the patch stays uninstalled.
            MethodInfo inRange = ScaleValidation.RequireInstanceMethod(
                typeof(StatusEmitter), "InRange", typeof(bool), Type.EmptyTypes, requirePublic: false);
            if (!inRange.IsVirtual)
                throw new InvalidOperationException("StatusEmitter.InRange is no longer virtual");
            RequireNoInRangeOverride(inRange);

            // Members of the damage path and of the warning overlay.
            ScaleValidation.RequireStaticField(typeof(Character), "localCharacter", typeof(Character));
            ScaleValidation.RequireInstanceProperty(typeof(Character), "Center", typeof(Vector3));
            ScaleValidation.RequireInstanceField(typeof(Character), "refs", typeof(Character.CharacterRefs));
            ScaleValidation.RequireInstanceField(
                typeof(Character.CharacterRefs), "afflictions", typeof(CharacterAfflictions));
            ScaleValidation.RequireInstanceMethod(
                typeof(CharacterAfflictions),
                "AddStatus",
                typeof(bool),
                new[]
                {
                    typeof(CharacterAfflictions.STATUSTYPE), typeof(float), typeof(bool),
                    typeof(bool), typeof(bool), typeof(bool)
                },
                requirePublic: true);
            ScaleValidation.RequireInstanceMethod(
                typeof(CharacterAfflictions),
                "SubtractStatus",
                typeof(void),
                new[]
                {
                    typeof(CharacterAfflictions.STATUSTYPE), typeof(float), typeof(bool), typeof(bool)
                },
                requirePublic: true);

            AccessTools.FieldRef<StatusEmitter, float> tickRef =
                AccessTools.FieldRefAccess<StatusEmitter, float>(timeSinceLastTick);
            AccessTools.FieldRef<StatusEmitter, float> tickTimeRef =
                AccessTools.FieldRefAccess<StatusEmitter, float>(tickTime);
            AccessTools.FieldRef<StatusEmitter, bool> inZoneRef =
                AccessTools.FieldRefAccess<StatusEmitter, bool>(inZone);
            Func<Dictionary<string, StatusEmitter>> map =
                (Func<Dictionary<string, StatusEmitter>>)Delegate.CreateDelegate(
                    typeof(Func<Dictionary<string, StatusEmitter>>),
                    null,
                    typeof(StatusEmitterScanPatch).GetMethod(
                        nameof(ReadOverlapMap), BindingFlags.Static | BindingFlags.NonPublic));
            if (tickRef == null || tickTimeRef == null || inZoneRef == null || map == null)
                throw new InvalidOperationException("StatusEmitterScan accessor creation failed");
            // Proven above; read through the validated FieldInfo so the delegate cannot drift from it.
            _overlapFieldInfo = overlapMap;

            _timeSinceLastTick = tickRef;
            _tickTime = tickTimeRef;
            _inZone = inZoneRef;
            _overlapMap = map;
        }

        private static FieldInfo _overlapFieldInfo;

        private static Dictionary<string, StatusEmitter> ReadOverlapMap() =>
            (Dictionary<string, StatusEmitter>)_overlapFieldInfo.GetValue(null);

        internal static MethodInfo ResolveAndValidateTarget()
        {
            return ScaleValidation.RequireInstanceMethod(
                typeof(StatusEmitter), "Update", typeof(void), Type.EmptyTypes, requirePublic: true);
        }

        internal static void Reset()
        {
            _timeSinceLastTick = null;
            _tickTime = null;
            _inZone = null;
            _overlapMap = null;
            _overlapFieldInfo = null;
        }

        internal static bool Prefix(StatusEmitter __instance)
        {
            AccessTools.FieldRef<StatusEmitter, float> tickRef = _timeSinceLastTick;
            AccessTools.FieldRef<StatusEmitter, float> tickTimeRef = _tickTime;
            AccessTools.FieldRef<StatusEmitter, bool> inZoneRef = _inZone;
            Func<Dictionary<string, StatusEmitter>> map = _overlapMap;
            if (tickRef == null || tickTimeRef == null || inZoneRef == null || map == null ||
                __instance == null)
            {
                return true;
            }

            Character local = Character.localCharacter;
            // The original's InRange returns false for a null local character, and every later use of
            // it is inside the in-range branch, so this reproduces the whole method for that case.
            if (local == null)
                return HandleOutOfRange(__instance, inZoneRef, tickRef);

            Character.CharacterRefs refs = local.refs;
            if (refs == null || refs.afflictions == null)
            {
                // The original would throw inside the damage branch; leave that with it.
                RateLimitedDiagnostics.Hit("StatusEmitterScan.localCharacter-incomplete");
                return true;
            }

            Vector3 center;
            try
            {
                // Character.Center indexes partDict directly and throws before the ragdoll registers.
                center = local.Center;
            }
            catch (Exception)
            {
                RateLimitedDiagnostics.Hit("StatusEmitterScan.center-unavailable");
                return true;
            }

            Vector3 position = __instance.transform.position;
            float threshold = __instance.radius + __instance.outerFade;
            // Bit-identical to the audited InRange: Vector3.Distance is
            // (float)Math.Sqrt(dx*dx + dy*dy + dz*dz) (Vector3.cs:809-815) and Mathf.Sqrt is the same
            // cast of the same intrinsic (Mathf.cs:98-101) over the same float sum that sqrMagnitude
            // computes (Vector3.cs:105-112). A squared compare would have been cheaper but is not
            // exact: `sqrt(d) < t` and `d < t*t` disagree inside the rounding band around t, and the
            // in-range side of that band is a real damage tick, so the cheaper form is not on offer
            // here. The saving this patch does take is that the value is computed once instead of
            // two to four times.
            float distance = Mathf.Sqrt((center - position).sqrMagnitude);
            bool inRange = distance < threshold;

            if (inRange && __instance.preventOverlap)
            {
                Dictionary<string, StatusEmitter> emitters = map();
                string key = __instance.overlapIdentifier;
                // A null map or a null key makes the audited ContainsKey/indexer throw. Nothing has
                // been written yet at this point, so handing the whole method back keeps that
                // behaviour where it belongs instead of inventing a value for isOverlapPriority.
                if (emitters == null || key == null)
                    return true;
                __instance.isOverlapPriority =
                    TryMakePriority(__instance, emitters, key, center, distance);
            }

            bool inZone = inZoneRef(__instance);
            if (__instance.inZoneWarning && inZone && (!inRange || __instance.emitterDisabledByWind))
            {
                // Checked before any state write so that deferring leaves the emitter exactly as the
                // original found it.
                if (!__instance.preventOverlap || __instance.isOverlapPriority)
                {
                    if (!TryEndWarning())
                        return true;
                }
                inZoneRef(__instance) = false;
                // The audited method does not return here: this block is a plain `if`, so control
                // falls into the second branch, whose condition is now false either way (this block
                // is only entered when !inRange or the wind disabled the emitter), and from there
                // into the else tail below.
                if (__instance.emitterDisabledByWind)
                    return false;
                tickRef(__instance) = 0f;
                return false;
            }

            if (__instance.inZoneWarning && inRange && !inZone && !__instance.emitterDisabledByWind)
            {
                if (!__instance.preventOverlap || __instance.isOverlapPriority)
                {
                    if (!TryStartWarning())
                        return true;
                }
                inZoneRef(__instance) = true;
                tickRef(__instance) = 0f - __instance.extraWarningTime;
                return false;
            }

            if (__instance.emitterDisabledByWind)
                return false;

            if (!inRange)
            {
                tickRef(__instance) = 0f;
                return false;
            }

            if (__instance.preventOverlap && !__instance.isOverlapPriority)
            {
                tickRef(__instance) = 0f;
                return false;
            }

            float sinceTick = tickRef(__instance) + Time.deltaTime;
            tickRef(__instance) = sinceTick;
            if (sinceTick < tickTimeRef(__instance))
                return false;

            ApplyDamage(__instance, refs.afflictions, distance, tickTimeRef(__instance));
            tickRef(__instance) = 0f;
            return false;
        }

        /// <summary>
        /// The audited method with a false InRange and no local character: the warning is ended if it
        /// was showing, otherwise the tick timer is cleared. Returns false unless the original has to
        /// take over.
        /// </summary>
        private static bool HandleOutOfRange(
            StatusEmitter instance,
            AccessTools.FieldRef<StatusEmitter, bool> inZoneRef,
            AccessTools.FieldRef<StatusEmitter, float> tickRef)
        {
            if (instance.inZoneWarning && inZoneRef(instance))
            {
                if (!instance.preventOverlap || instance.isOverlapPriority)
                {
                    if (!TryEndWarning())
                        return true;
                }
                inZoneRef(instance) = false;
                // Same fall-through as the audited method: the warning block is not an early return.
                if (instance.emitterDisabledByWind)
                    return false;
                tickRef(instance) = 0f;
                return false;
            }
            if (instance.emitterDisabledByWind)
                return false;
            tickRef(instance) = 0f;
            return false;
        }

        /// <summary>
        /// StatusEmitter.cs:98-117 verbatim, with the distance passed in rather than recomputed.
        /// Note the original reads <c>amount</c> again for the actual AddStatus (:112) rather than the
        /// faded <c>num</c> it just computed - that is reproduced exactly, faithful bug included,
        /// because changing it would change damage.
        /// </summary>
        private static void ApplyDamage(
            StatusEmitter instance, CharacterAfflictions afflictions, float distance, float tickTime)
        {
            float faded = instance.amount;
            if (instance.outerFade > 0.01f)
                faded *= Mathf.InverseLerp(instance.radius + instance.outerFade, distance, distance);

            float inner = 0f;
            if (instance.innerFade > 0.01f)
                inner = (distance - (instance.radius - instance.innerFade)) / instance.innerFade;
            inner = instance.debug = Mathf.Clamp(1f - inner, instance.minAmount, 1f);

            if (faded > 0f)
                afflictions.AddStatus(instance.statusType, instance.amount * tickTime * inner);
            if (faded < 0f)
                afflictions.SubtractStatus(
                    instance.statusType, Mathf.Abs(instance.amount * tickTime));
        }

        /// <summary>
        /// StatusEmitter.cs:133-156 with the local character's Center and this emitter's own distance
        /// passed in instead of resolved and recomputed two more times. Only reached while in range,
        /// exactly as in the original. The distance comparison stays a comparison of two
        /// Vector3.Distance results so the winner is the same one vanilla picks.
        /// </summary>
        private static bool TryMakePriority(
            StatusEmitter instance,
            Dictionary<string, StatusEmitter> emitters,
            string key,
            Vector3 center,
            float distance)
        {
            // TryGetValue in place of the audited ContainsKey followed by three separate indexer
            // lookups on the same string key.
            if (!emitters.TryGetValue(key, out StatusEmitter current))
            {
                emitters.Add(key, instance);
                return true;
            }
            if (current == instance)
                return true;
            if (current == null)
            {
                emitters[key] = instance;
                return true;
            }

            if (distance < Vector3.Distance(current.transform.position, center))
            {
                emitters[key] = instance;
                return true;
            }
            return false;
        }

        private static bool TryStartWarning()
        {
            ScreenVFX warning = ResolveWarning();
            if (warning == null)
                return false;
            warning.StartFX();
            return true;
        }

        private static bool TryEndWarning()
        {
            ScreenVFX warning = ResolveWarning();
            if (warning == null)
                return false;
            warning.EndFX();
            return true;
        }

        /// <summary>
        /// The original dereferences GUIManager.instance.sporesWarning unguarded. Returning null here
        /// makes the caller defer to the untouched original so its own exception behaviour is kept.
        /// </summary>
        private static ScreenVFX ResolveWarning()
        {
            GUIManager gui = GUIManager.instance;
            return gui != null ? gui.sporesWarning : null;
        }

        private static void RequireNoInRangeOverride(MethodInfo inRange)
        {
            Assembly assembly = typeof(StatusEmitter).Assembly;
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = Array.Empty<Type>();
                foreach (Type loaded in ex.Types)
                {
                    if (loaded != null)
                        types = Append(types, loaded);
                }
            }

            for (int i = 0; i < types.Length; i++)
            {
                Type type = types[i];
                if (type == typeof(StatusEmitter) || !typeof(StatusEmitter).IsAssignableFrom(type))
                    continue;
                MethodInfo declared = type.GetMethod(
                    "InRange",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic |
                    BindingFlags.DeclaredOnly,
                    null,
                    Type.EmptyTypes,
                    null);
                if (declared != null && declared != inRange)
                {
                    throw new InvalidOperationException(
                        $"{type.FullName} overrides StatusEmitter.InRange; the inlined range test " +
                        "would bypass it");
                }
            }
        }

        private static Type[] Append(Type[] source, Type value)
        {
            var result = new Type[source.Length + 1];
            Array.Copy(source, result, source.Length);
            result[source.Length] = value;
            return result;
        }
    }
}
