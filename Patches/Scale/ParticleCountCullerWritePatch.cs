using System;
using System.Reflection;
using HarmonyLib;
using PeakSafeOptimizer.Diagnostics;
using UnityEngine;

namespace PeakSafeOptimizer.Patches.Scale
{
    /// <summary>
    /// Full equivalent replacement of ParticleCountCuller.Update() (audited source
    /// ParticleCountCuller.cs:22-28), the distance attenuation on the spore and mushroom clouds:
    ///
    ///   float t = Mathf.Clamp01((Vector3.Distance(transform.position, observedCharacter.Head)
    ///                            - minDistance) / maxDistance);
    ///   attenuationVal = Mathf.Lerp(1f, 0.2f, t);
    ///   ps.emission.rateOverTimeMultiplier = attenuationVal * startVal;
    ///
    /// Two things are wrong with that per instance per frame:
    ///
    /// First, <c>Character.observedCharacter</c> is not null-checked (compare ParticleCuller.cs:37-40
    /// and PlayerTrackParticles.cs:34-37, which both do check), and <c>.Head</c> indexes
    /// CharacterRagdoll.partDict directly (Character.cs:268 -> :1299-1302). Before a character exists
    /// this throws every frame, per instance.
    ///
    /// Second, the emission multiplier is written unconditionally even when the computed value has not
    /// changed. Assigning EmissionModule.rateOverTimeMultiplier is an InternalCall into native
    /// ParticleSystem state and dirties the system; for a stationary cloud and a stationary observer
    /// the value is identical frame after frame.
    ///
    /// The replacement keeps the arithmetic byte for byte - including the audited
    /// <c>(distance - minDistance) / maxDistance</c>, which is not a normalisation between the two
    /// distances and is deliberately left as-is because <c>attenuationVal</c> is a serialized field a
    /// scene may be tuned against - and changes only two things: the observer is resolved defensively,
    /// and the native write is skipped when the value already on the system equals the one about to be
    /// written.
    ///
    /// The comparison deliberately reads the live value rather than remembering what this patch last
    /// wrote. A remembered value is only equivalent while nothing else writes the same module, and
    /// this game does write emission multipliers from elsewhere - BeeSwarm's dispersal coroutine
    /// decrements <c>beeParticles.emission.rateOverTimeMultiplier</c> every frame for a second
    /// (BeeSwarm.cs, DisperseRoutine). On any system where both were present, a remembered value
    /// would let that decrement stand where vanilla overwrites it every frame. Reading first trades
    /// a native setter for a native getter and stays exact whoever else writes.
    ///
    /// Purely visual: <c>attenuationVal</c> and the emission rate are read by nothing else in the game
    /// (the only other reference to the field is its own declaration), and the spore damage lives in
    /// StatusEmitter, which holds no ParticleSystem reference at all.
    /// </summary>
    internal static class ParticleCountCullerWritePatch
    {
        private static AccessTools.FieldRef<ParticleCountCuller, ParticleSystem> _particleSystem;
        private static AccessTools.FieldRef<ParticleCountCuller, float> _startVal;

        internal static void Initialize()
        {
            ResolveAndValidateTarget();

            FieldInfo psField = ScaleValidation.RequireInstanceField(
                typeof(ParticleCountCuller), "ps", typeof(ParticleSystem));
            FieldInfo startValField = ScaleValidation.RequireInstanceField(
                typeof(ParticleCountCuller), "startVal", typeof(float));
            ScaleValidation.RequireInstanceField(
                typeof(ParticleCountCuller), "minDistance", typeof(float));
            ScaleValidation.RequireInstanceField(
                typeof(ParticleCountCuller), "maxDistance", typeof(float));
            ScaleValidation.RequireInstanceField(
                typeof(ParticleCountCuller), "attenuationVal", typeof(float));

            ScaleValidation.RequireStaticProperty(
                typeof(Character), "observedCharacter", typeof(Character));
            ScaleValidation.RequireInstanceProperty(typeof(Character), "Head", typeof(Vector3));

            // The emission module is a struct property; assigning rateOverTimeMultiplier on a local
            // copy still writes through to the system, which is what the audited code relies on.
            PropertyInfo emission = typeof(ParticleSystem).GetProperty(
                "emission", BindingFlags.Instance | BindingFlags.Public);
            if (emission == null || emission.PropertyType != typeof(ParticleSystem.EmissionModule))
                throw new InvalidOperationException("ParticleSystem.emission structure mismatch");
            PropertyInfo rate = typeof(ParticleSystem.EmissionModule).GetProperty(
                "rateOverTimeMultiplier", BindingFlags.Instance | BindingFlags.Public);
            if (rate == null || rate.PropertyType != typeof(float) ||
                rate.GetSetMethod() == null || rate.GetGetMethod() == null)
                throw new InvalidOperationException("EmissionModule.rateOverTimeMultiplier mismatch");

            AccessTools.FieldRef<ParticleCountCuller, ParticleSystem> ps =
                AccessTools.FieldRefAccess<ParticleCountCuller, ParticleSystem>(psField);
            AccessTools.FieldRef<ParticleCountCuller, float> startVal =
                AccessTools.FieldRefAccess<ParticleCountCuller, float>(startValField);
            if (ps == null || startVal == null)
                throw new InvalidOperationException("ParticleCountCullerWrite accessor creation failed");

            _particleSystem = ps;
            _startVal = startVal;
        }

        internal static MethodInfo ResolveAndValidateTarget()
        {
            return ScaleValidation.RequireInstanceMethod(
                typeof(ParticleCountCuller), "Update", typeof(void), Type.EmptyTypes,
                requirePublic: false);
        }

        internal static void Reset()
        {
            _particleSystem = null;
            _startVal = null;
        }

        internal static bool Prefix(ParticleCountCuller __instance)
        {
            AccessTools.FieldRef<ParticleCountCuller, ParticleSystem> psRef = _particleSystem;
            AccessTools.FieldRef<ParticleCountCuller, float> startValRef = _startVal;
            if (psRef == null || startValRef == null || __instance == null)
                return true;

            ParticleSystem ps = psRef(__instance);
            if (ps == null)
            {
                // Start() assigns this from GetComponent; a missing ParticleSystem makes the original
                // throw, and that stays its own behaviour.
                return true;
            }

            Character observed = Character.observedCharacter;
            if (observed == null)
            {
                // Vanilla throws here every frame before a character exists. Nothing to attenuate yet.
                RateLimitedDiagnostics.Hit("ParticleCountCullerWrite.observedCharacter-unavailable");
                return false;
            }

            Vector3 head;
            try
            {
                head = observed.Head;
            }
            catch (Exception)
            {
                RateLimitedDiagnostics.Hit("ParticleCountCullerWrite.head-unavailable");
                return false;
            }

            // Audited arithmetic, unchanged - including maxDistance being used as a divisor rather
            // than as the far end of a range.
            float distance = Vector3.Distance(__instance.transform.position, head);
            float t = Mathf.Clamp01((distance - __instance.minDistance) / __instance.maxDistance);
            float attenuation = Mathf.Lerp(1f, 0.2f, t);
            __instance.attenuationVal = attenuation;

            float multiplier = attenuation * startValRef(__instance);
            ParticleSystem.EmissionModule emission = ps.emission;
            // A NaN multiplier fails this comparison and is therefore still written, exactly as
            // vanilla writes it.
            if (emission.rateOverTimeMultiplier != multiplier)
                emission.rateOverTimeMultiplier = multiplier;
            return false;
        }
    }
}
