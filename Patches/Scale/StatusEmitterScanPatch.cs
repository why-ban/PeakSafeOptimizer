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
    /// Field writes keep vanilla's order as well as its values. In particular the warning-end block
    /// clears <c>inZone</c> before touching the overlay (StatusEmitter.cs:65-69), because the overlay
    /// call can throw on a destroyed Renderer and vanilla's ordering is what makes that throw
    /// self-limiting rather than per-frame.
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

        internal static void Initialize()
        {
            ResolveAndValidateTarget();

            FieldInfo timeSinceLastTick = ScaleValidation.RequireInstanceField(
                typeof(StatusEmitter), "timeSinceLastTick", typeof(float));
            FieldInfo tickTime = ScaleValidation.RequireInstanceField(
                typeof(StatusEmitter), "tickTime", typeof(float));
            FieldInfo inZone = ScaleValidation.RequireInstanceField(
                typeof(StatusEmitter), "inZone", typeof(bool));
            // Structural assertion only: the replacement reads this public static field directly (see
            // the Prefix), but the fail-closed precondition still has to hold - if the field is
            // renamed, retyped or made non-static, the patch must not install.
            ScaleValidation.RequireStaticField(
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

            // The warning overlay. ResolveWarning reads GUIManager.instance (GUIManager.cs:19) and
            // GUIManager.sporesWarning (GUIManager.cs:189), and the two overlay calls are
            // ScreenVFX.EndFX (ScreenVFX.cs:90) and ScreenVFX.StartFX (ScreenVFX.cs:70); vanilla
            // reaches all four through GUIManager.instance.sporesWarning.EndFX/StartFX
            // (StatusEmitter.cs:68 and :75). None of them was asserted before, which left the
            // replacement's only external call surface outside the fail-closed contract.
            ScaleValidation.RequireStaticField(typeof(GUIManager), "instance", typeof(GUIManager));
            ScaleValidation.RequireInstanceField(typeof(GUIManager), "sporesWarning", typeof(ScreenVFX));
            ScaleValidation.RequireInstanceMethod(
                typeof(ScreenVFX), "EndFX", typeof(void), Type.EmptyTypes, requirePublic: true);
            MethodInfo startFx = ScaleValidation.RequireInstanceMethod(
                typeof(ScreenVFX), "StartFX", typeof(void), new[] { typeof(float) }, requirePublic: true);
            RequireStartFxPhotosensitiveDefault(startFx);

            AccessTools.FieldRef<StatusEmitter, float> tickRef =
                AccessTools.FieldRefAccess<StatusEmitter, float>(timeSinceLastTick);
            AccessTools.FieldRef<StatusEmitter, float> tickTimeRef =
                AccessTools.FieldRefAccess<StatusEmitter, float>(tickTime);
            AccessTools.FieldRef<StatusEmitter, bool> inZoneRef =
                AccessTools.FieldRefAccess<StatusEmitter, bool>(inZone);
            if (tickRef == null || tickTimeRef == null || inZoneRef == null)
                throw new InvalidOperationException("StatusEmitterScan accessor creation failed");

            _timeSinceLastTick = tickRef;
            _tickTime = tickTimeRef;
            _inZone = inZoneRef;
        }

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
        }

        /// <param name="__runOriginal">
        /// HarmonyX runs every Prefix on a target and only then decides whether the original body
        /// executes, so a Prefix that returned false earlier in the chain does not stop this one from
        /// being called. Reading this flag is mandatory for a full-replacement patch: this body has
        /// side effects that another mod's `return false` was meant to suppress - it applies spore
        /// damage through CharacterAfflictions, writes inZone/timeSinceLastTick/isOverlapPriority and
        /// mutates the static overlap dictionary. Running it anyway would silently defeat that mod.
        /// </param>
        internal static bool Prefix(StatusEmitter __instance, bool __runOriginal)
        {
            if (!__runOriginal)
                return false;

            AccessTools.FieldRef<StatusEmitter, float> tickRef = _timeSinceLastTick;
            AccessTools.FieldRef<StatusEmitter, float> tickTimeRef = _tickTime;
            AccessTools.FieldRef<StatusEmitter, bool> inZoneRef = _inZone;
            if (tickRef == null || tickTimeRef == null || inZoneRef == null || __instance == null)
                return true;

            Character local = Character.localCharacter;
            // The original's InRange returns false for a null local character, and every later use of
            // it is inside the in-range branch, so this reproduces the whole method for that case.
            if (local == null)
                return HandleOutOfRange(__instance, inZoneRef, tickRef);

            Vector3 center;
            try
            {
                // Character.Center indexes partDict directly (Character.cs:266 -> :1299-1302) and
                // throws KeyNotFoundException before the ragdoll registers its parts.
                center = local.Center;
            }
            catch (Exception)
            {
                // In this window the original throws out of InRange (StatusEmitter.cs:53 reads the
                // same Center) once per emitter per frame, so this is a per-frame vanilla exception
                // and therefore inside this plugin's remit. Handing the method back would only add a
                // catch on top of the same exception, so the exception is removed instead: the emitter
                // is treated as out of range. HandleOutOfRange reads neither Center nor refs, so it
                // cannot re-enter the failure.
                //
                // The state difference against vanilla is bounded and in the safe direction. Vanilla's
                // exception escapes Update before any write, leaving timeSinceLastTick and inZone as
                // they were; the out-of-range path clears timeSinceLastTick (and, if a warning was
                // showing, ends it). Clearing the tick timer can only delay the next damage tick,
                // never bring it forward, because damage requires timeSinceLastTick >= tickTime - so
                // the player can get poisoned later than vanilla, never earlier. It is also exactly
                // the state vanilla itself produces for a character that really is out of range, which
                // is the honest reading of "the local character has no torso yet".
                RateLimitedDiagnostics.Hit("StatusEmitterScan.center-unavailable");
                return HandleOutOfRange(__instance, inZoneRef, tickRef);
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
                // Read straight off the public static field. It needs no reflection - this assembly
                // references Assembly-CSharp and this file already touches public members such as
                // radius and isOverlapPriority directly - and a FieldInfo.GetValue per emitter per
                // frame would have cost about as much as the dictionary lookups this patch saves.
                // The fail-closed precondition is unchanged: Initialize still asserts the field's
                // name, staticness and exact type before the patch is allowed to install.
                Dictionary<string, StatusEmitter> emitters =
                    StatusEmitter.overlapPreventedStatusEmitters;
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
                // The overlay is resolved - but not called - before any state write, so handing the
                // method back on a missing GUIManager still leaves the emitter exactly as the original
                // found it and lets the original's own unguarded dereference stand.
                bool endOverlay = !__instance.preventOverlap || __instance.isOverlapPriority;
                ScreenVFX warning = endOverlay ? ResolveWarning() : null;
                if (endOverlay && warning == null)
                    return true;
                inZoneRef(__instance) = false;
                // State first, overlay second: the audited order (StatusEmitter.cs:65-69). The order
                // matters for exactly the failure this plugin exists to prevent. EndFX dereferences
                // ScreenVFX.renderer.material (ScreenVFX.cs:90-93) and that public serialized Renderer
                // can already be destroyed during scene teardown. With inZone cleared first, a throw
                // out of EndFX makes this block's condition false on the next frame, so the exception
                // happens once and stops - vanilla's self-limiting behaviour. Calling EndFX first
                // would leave inZone true and re-enter, re-throwing every frame.
                if (endOverlay)
                    warning.EndFX();
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
                // Here vanilla calls StartFX *before* writing inZone (StatusEmitter.cs:73-77), so the
                // overlay attempt comes first and handing the method back on a missing GUIManager is
                // still write-free. The apparent asymmetry with the block above is vanilla's own.
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

            // The afflictions holder is resolved inside ApplyDamage, in the same two branches where
            // vanilla dereferences it (StatusEmitter.cs:112/116) and no earlier. Checking it at the
            // top of the method - as this patch used to - abandoned the whole replacement, range test
            // and warning overlay included, in a window where vanilla still runs all of it; and it
            // also gave up on emitters vanilla never trips over, since neither dereference is reached
            // when the faded amount is exactly zero (an emitter with amount == 0, or one sitting
            // exactly on radius + outerFade, where the audited InverseLerp(a, b, b) degenerates to 0).
            if (!ApplyDamage(__instance, local, distance, tickTimeRef(__instance)))
            {
                // Only reachable where vanilla throws NRE, i.e. on a tick that really would have
                // applied damage - not every frame. The state left behind matches vanilla's: its
                // exception escapes before the `timeSinceLastTick = 0f` tail (:119), and this path
                // likewise leaves the timer at the value just written, so the tick gate stays open and
                // the next frame retries identically. The exception itself is gone, which is what
                // makes the diagnostic worth its cost here.
                RateLimitedDiagnostics.Hit("StatusEmitterScan.afflictions-unavailable");
                return false;
            }
            tickRef(__instance) = 0f;
            return false;
        }

        /// <summary>
        /// The audited method with a false InRange and no local character: the warning is ended if it
        /// was showing, otherwise the tick timer is cleared. Returns false unless the original has to
        /// take over. Reads neither Character.Center nor Character.refs, which is what makes it usable
        /// as the fallback when the torso position cannot be resolved.
        /// </summary>
        private static bool HandleOutOfRange(
            StatusEmitter instance,
            AccessTools.FieldRef<StatusEmitter, bool> inZoneRef,
            AccessTools.FieldRef<StatusEmitter, float> tickRef)
        {
            if (instance.inZoneWarning && inZoneRef(instance))
            {
                // Resolved before any write so that handing the method back is write-free; called
                // after the write, because that is vanilla's order and it is what keeps a throw out of
                // EndFX self-limiting. See the same block in the Prefix.
                bool endOverlay = !instance.preventOverlap || instance.isOverlapPriority;
                ScreenVFX warning = endOverlay ? ResolveWarning() : null;
                if (endOverlay && warning == null)
                    return true;
                inZoneRef(instance) = false;
                if (endOverlay)
                    warning.EndFX();
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
        ///
        /// The afflictions holder is resolved lazily inside the two branches that use it, which is
        /// where the original resolves it too, so a null one is only noticed when the original would
        /// have thrown. Returns false in exactly that case, after the <c>debug</c> write the original
        /// has already performed by then, and without applying anything.
        /// </summary>
        private static bool ApplyDamage(
            StatusEmitter instance, Character local, float distance, float tickTime)
        {
            float faded = instance.amount;
            if (instance.outerFade > 0.01f)
                faded *= Mathf.InverseLerp(instance.radius + instance.outerFade, distance, distance);

            float inner = 0f;
            if (instance.innerFade > 0.01f)
                inner = (distance - (instance.radius - instance.innerFade)) / instance.innerFade;
            inner = instance.debug = Mathf.Clamp(1f - inner, instance.minAmount, 1f);

            if (faded > 0f)
            {
                CharacterAfflictions afflictions = ResolveAfflictions(local);
                if (afflictions == null)
                    return false;
                afflictions.AddStatus(instance.statusType, instance.amount * tickTime * inner);
            }
            if (faded < 0f)
            {
                CharacterAfflictions afflictions = ResolveAfflictions(local);
                if (afflictions == null)
                    return false;
                afflictions.SubtractStatus(
                    instance.statusType, Mathf.Abs(instance.amount * tickTime));
            }
            return true;
        }

        /// <summary>
        /// <c>Character.localCharacter.refs.afflictions</c> as the original spells it (:112), guarded.
        /// A NaN or zero faded amount never reaches this, exactly as in the original, where both
        /// dereferences sit inside the sign tests.
        /// </summary>
        private static CharacterAfflictions ResolveAfflictions(Character local)
        {
            Character.CharacterRefs refs = local.refs;
            return refs != null ? refs.afflictions : null;
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

        /// <summary>
        /// The original dereferences GUIManager.instance.sporesWarning unguarded. Returning null here
        /// makes the caller defer to the untouched original so its own exception behaviour is kept.
        /// Every caller resolves the overlay before writing any emitter state, which is what makes
        /// deferring exact - the end-of-warning path then still writes <c>inZone</c> before calling
        /// EndFX, matching the audited order.
        /// </summary>
        private static ScreenVFX ResolveWarning()
        {
            GUIManager gui = GUIManager.instance;
            return gui != null ? gui.sporesWarning : null;
        }

        /// <summary>
        /// StartFX's parameter is optional (ScreenVFX.cs:70, <c>float photosensitive = 0.5f</c>) and
        /// TryStartWarning calls it as <c>warning.StartFX()</c>, so the 0.5f is baked into this
        /// plugin's own IL at compile time. A signature assertion cannot see that: if the game ever
        /// changes the default, StartFX still has the exact same name, staticness, return type and
        /// parameter type, so every other check here keeps passing while vanilla passes the new value
        /// and this patch keeps passing 0.5f - a silent behaviour divergence in the photosensitivity
        /// path. Hence this extra check, which asserts the constant this assembly compiled against.
        ///
        /// The comparison unboxes through a type pattern rather than Equals or Convert: DefaultValue
        /// is typed object, and a retyped parameter would box something that is not a float, which
        /// the pattern rejects instead of throwing or coercing. 0.5f is exactly representable, so the
        /// float equality is exact.
        /// </summary>
        private static void RequireStartFxPhotosensitiveDefault(MethodInfo startFx)
        {
            ParameterInfo[] parameters = startFx.GetParameters();
            if (parameters.Length != 1)
                throw new InvalidOperationException("ScreenVFX.StartFX parameter count changed");
            ParameterInfo photosensitive = parameters[0];
            if (!photosensitive.IsOptional || !photosensitive.HasDefaultValue ||
                !(photosensitive.DefaultValue is float defaultValue) || defaultValue != 0.5f)
            {
                throw new InvalidOperationException(
                    "ScreenVFX.StartFX no longer defaults photosensitive to 0.5f; the value this " +
                    "patch compiled into its own call site would diverge from vanilla");
            }
        }

        /// <summary>
        /// Known limitation: only <c>StatusEmitter</c>'s own assembly is scanned. A subclass declared
        /// in another assembly - a mod's - that overrides InRange would not be found here, and the
        /// inlined range test would bypass it. Scanning the whole AppDomain instead was rejected: it
        /// would make the result depend on which plugins happen to be loaded when this runs, and force
        /// GetTypes() over every loaded assembly during startup.
        /// </summary>
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
