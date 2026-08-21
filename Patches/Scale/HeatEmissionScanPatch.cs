using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using PeakSafeOptimizer.Diagnostics;
using UnityEngine;

namespace PeakSafeOptimizer.Patches.Scale
{
    /// <summary>
    /// Narrow equivalent replacement of CharacterHeatEmission.Update() (audited source
    /// CharacterHeatEmission.cs:20-40). Ordering is preserved exactly:
    /// hip position write -> sinceAddedCold early exit -> counter accumulation and rate gate
    /// -> counter reset -> per-character scan -> SubtractStatus(Cold, heatAmount).
    /// The only behavioral change is how the distance test is evaluated; see EvaluateRadius.
    /// </summary>
    internal static class HeatEmissionScanPatch
    {
        private static AccessTools.FieldRef<CharacterHeatEmission, Character> _character;
        private static AccessTools.FieldRef<CharacterHeatEmission, float> _counter;
        private static AccessTools.FieldRef<CharacterAfflictions, Character> _afflictionsCharacter;
        private static bool _ownerShortCircuitProven;

        internal static void Initialize()
        {
            ResolveAndValidateTarget();

            FieldInfo characterField = ScaleValidation.RequireInstanceField(
                typeof(CharacterHeatEmission), "character", typeof(Character));
            FieldInfo counterField = ScaleValidation.RequireInstanceField(
                typeof(CharacterHeatEmission), "counter", typeof(float));
            ScaleValidation.RequireInstanceField(
                typeof(CharacterHeatEmission), "radius", typeof(float));
            ScaleValidation.RequireInstanceField(
                typeof(CharacterHeatEmission), "heatAmount", typeof(float));
            ScaleValidation.RequireInstanceField(
                typeof(CharacterHeatEmission), "rate", typeof(float));

            ScaleValidation.RequireInstanceField(typeof(Character), "refs", typeof(Character.CharacterRefs));
            ScaleValidation.RequireInstanceField(typeof(Character), "data", typeof(CharacterData));
            ScaleValidation.RequireInstanceField(typeof(Character.CharacterRefs), "hip", typeof(Bodypart));
            ScaleValidation.RequireInstanceField(
                typeof(Character.CharacterRefs), "afflictions", typeof(CharacterAfflictions));
            ScaleValidation.RequireInstanceField(typeof(CharacterData), "sinceAddedCold", typeof(float));
            ScaleValidation.RequireStaticField(typeof(Character), "AllCharacters", typeof(List<Character>));
            ScaleValidation.RequireInstanceProperty(typeof(Character), "Center", typeof(Vector3));
            ScaleValidation.RequireInheritedInstanceProperty(typeof(Bodypart), "transform", typeof(Transform));

            ScaleValidation.RequireInstanceMethod(
                typeof(CharacterAfflictions),
                "SubtractStatus",
                typeof(void),
                new[]
                {
                    typeof(CharacterAfflictions.STATUSTYPE),
                    typeof(float),
                    typeof(bool),
                    typeof(bool)
                },
                true);

            AccessTools.FieldRef<CharacterHeatEmission, Character> character =
                AccessTools.FieldRefAccess<CharacterHeatEmission, Character>(characterField);
            AccessTools.FieldRef<CharacterHeatEmission, float> counter =
                AccessTools.FieldRefAccess<CharacterHeatEmission, float>(counterField);
            if (character == null || counter == null)
                throw new InvalidOperationException("HeatEmissionScan FieldRef creation failed");

            // Owner short-circuit is only enabled when the audited guard is still present:
            // SubtractStatus returns immediately, before any mutation, when
            // (!character.photonView.IsMine && !fromRPC). See CharacterAfflictions.cs:887-892.
            // The afflictions -> character back reference must exist so the guard subject can be
            // proven identical to the scanned character; otherwise the optimization is skipped.
            _ownerShortCircuitProven = false;
            _afflictionsCharacter = null;
            try
            {
                FieldInfo afflictionsCharacter = ScaleValidation.RequireInstanceField(
                    typeof(CharacterAfflictions), "character", typeof(Character));
                AccessTools.FieldRef<CharacterAfflictions, Character> afflictionsCharacterRef =
                    AccessTools.FieldRefAccess<CharacterAfflictions, Character>(afflictionsCharacter);
                if (afflictionsCharacterRef != null)
                {
                    _afflictionsCharacter = afflictionsCharacterRef;
                    _ownerShortCircuitProven = true;
                }
            }
            catch (Exception)
            {
                // Structure not provable on this build: keep the unconditional call path.
                _ownerShortCircuitProven = false;
                _afflictionsCharacter = null;
            }

            _character = character;
            _counter = counter;
        }

        internal static MethodInfo ResolveAndValidateTarget()
        {
            return ScaleValidation.RequireInstanceMethod(
                typeof(CharacterHeatEmission), "Update", typeof(void), Type.EmptyTypes, true);
        }

        internal static void Reset()
        {
            _character = null;
            _counter = null;
            _afflictionsCharacter = null;
            _ownerShortCircuitProven = false;
        }

        internal static bool Prefix(CharacterHeatEmission __instance)
        {
            AccessTools.FieldRef<CharacterHeatEmission, Character> characterRef = _character;
            AccessTools.FieldRef<CharacterHeatEmission, float> counterRef = _counter;
            if (characterRef == null || counterRef == null)
            {
                RateLimitedDiagnostics.Hit("HeatEmissionScan.accessors-unavailable");
                return true;
            }

            Character owner = characterRef(__instance);
            if (owner == null)
            {
                RateLimitedDiagnostics.Hit("HeatEmissionScan.character-null");
                return true;
            }

            Character.CharacterRefs refs = owner.refs;
            Bodypart hip = refs != null ? refs.hip : null;
            CharacterData data = owner.data;
            if (refs == null || hip == null || data == null)
            {
                RateLimitedDiagnostics.Hit("HeatEmissionScan.owner-refs-incomplete");
                return true;
            }

            List<Character> all = Character.AllCharacters;
            if (all == null)
            {
                RateLimitedDiagnostics.Hit("HeatEmissionScan.allCharacters-null");
                return true;
            }

            Transform selfTransform = __instance.transform;
            Transform hipTransform = hip.transform;
            if (selfTransform == null || hipTransform == null)
            {
                RateLimitedDiagnostics.Hit("HeatEmissionScan.transform-null");
                return true;
            }

            float radius = __instance.radius;
            float radiusSqr = radius * radius;
            if (radius > 0f && (!(radiusSqr > 0f) || float.IsInfinity(radiusSqr)))
            {
                // Decide before any field write. Falling back after counter reset would make the
                // original add delta a second time and leave a different counter value.
                RateLimitedDiagnostics.Hit("HeatEmissionScan.radius-out-of-square-range");
                return true;
            }

            // Step 1: unchanged position write.
            selfTransform.position = hipTransform.position;

            // Step 2: unchanged cold-recency early exit.
            if (data.sinceAddedCold < 3f)
                return false;

            // Step 3: unchanged counter accumulation and rate gate.
            float counter = counterRef(__instance) + Time.deltaTime;
            if (counter < __instance.rate)
            {
                counterRef(__instance) = counter;
                return false;
            }
            counterRef(__instance) = 0f;

            if (!(radius > 0f))
            {
                // radius <= 0 or NaN: the original `Distance < radius` is false for every
                // character (Distance is non-negative, and any NaN comparison is false), so the
                // loop can be skipped without losing a single side effect.
                return false;
            }

            Vector3 origin = selfTransform.position;
            float heatAmount = __instance.heatAmount;
            bool ownerShortCircuit = _ownerShortCircuitProven;
            AccessTools.FieldRef<CharacterAfflictions, Character> afflictionsCharacterRef = _afflictionsCharacter;

            for (int i = 0; i < all.Count; i++)
            {
                Character candidate = all[i];
                if (candidate == null)
                    continue;

                Vector3 delta = origin - candidate.Center;
                // radius > 0 here, so sqrMagnitude < radius*radius is exactly equivalent to
                // Vector3.Distance(origin, center) < radius for all finite and NaN inputs.
                if (!(delta.sqrMagnitude < radiusSqr))
                    continue;

                Character.CharacterRefs candidateRefs = candidate.refs;
                CharacterAfflictions afflictions = candidateRefs != null ? candidateRefs.afflictions : null;
                if (afflictions == null)
                    continue;

                if (ownerShortCircuit && afflictionsCharacterRef != null &&
                    afflictionsCharacterRef(afflictions) == candidate && !candidate.photonView.IsMine)
                {
                    // Proven no-op on this build: SubtractStatus returns before mutating anything
                    // when the guarded character is not owned locally and fromRPC is false.
                    continue;
                }

                afflictions.SubtractStatus(CharacterAfflictions.STATUSTYPE.Cold, heatAmount);
            }
            return false;
        }
    }
}
