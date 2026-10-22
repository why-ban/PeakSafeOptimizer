using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using PeakSafeOptimizer.Diagnostics;
using PeakSafeOptimizer.Patches.Scale;
using UnityEngine;

namespace PeakSafeOptimizer.Patches.ScaleExperimental
{
    /// <summary>
    /// Equivalent replacement of MushroomZombie.DoSleeping: squared distance instead of Vector3.Distance,
    /// which agrees to within one ULP rather than bit-exactly, and Vector3.Angle computed only after the
    /// distance test passes. A non-positive threshold defers to the original, where the strict comparison
    /// can never pass.
    /// </summary>
    internal static class SleepingZombieScanPatch
    {
        private delegate bool CharacterPredicate(MushroomZombie instance, Character target);
        private delegate void ZeroArgAction(MushroomZombie instance);

        private static AccessTools.FieldRef<MushroomZombie, Character> _character;
        private static CharacterPredicate _targetIsValid;
        private static CharacterPredicate _hasLineOfSight;
        private static ZeroArgAction _wakeUpFromSleep;

        internal static void Initialize()
        {
            ResolveAndValidateTarget();

            FieldInfo characterField = ScaleValidation.RequireInstanceField(
                typeof(MushroomZombie), "character", typeof(Character));
            ScaleValidation.RequireInstanceField(typeof(MushroomZombie), "visible", typeof(bool));
            ScaleValidation.RequireInstanceField(
                typeof(MushroomZombie), "distanceBeforeWakeup", typeof(float));
            ScaleValidation.RequireInstanceField(
                typeof(MushroomZombie), "lookAngleBeforeWakeup", typeof(float));
            ScaleValidation.RequireInstanceField(typeof(Character), "data", typeof(CharacterData));
            ScaleValidation.RequireInstanceField(typeof(Character), "refs", typeof(Character.CharacterRefs));
            ScaleValidation.RequireInstanceField(
                typeof(Character.CharacterRefs), "head", typeof(Bodypart));
            ScaleValidation.RequireInheritedInstanceProperty(
                typeof(Bodypart), "transform", typeof(Transform));
            ScaleValidation.RequireInstanceField(typeof(CharacterData), "fallSeconds", typeof(float));
            ScaleValidation.RequireStaticField(typeof(Character), "AllCharacters", typeof(List<Character>));
            ScaleValidation.RequireInstanceProperty(typeof(Character), "Center", typeof(Vector3));
            ScaleValidation.RequireStaticMethod(
                typeof(Vector3), "Distance", typeof(float),
                new[] { typeof(Vector3), typeof(Vector3) }, true);
            ScaleValidation.RequireStaticMethod(
                typeof(Vector3), "Angle", typeof(float),
                new[] { typeof(Vector3), typeof(Vector3) }, true);

            MethodInfo targetIsValidMethod = ScaleValidation.RequireInstanceMethod(
                typeof(MushroomZombie), "TargetIsValid", typeof(bool), new[] { typeof(Character) }, false);
            MethodInfo hasLineOfSightMethod = ScaleValidation.RequireInstanceMethod(
                typeof(MushroomZombie), "HasLineOfSight", typeof(bool), new[] { typeof(Character) }, false);
            MethodInfo wakeUpMethod = ScaleValidation.RequireInstanceMethod(
                typeof(MushroomZombie), "WakeUpFromSleep", typeof(void), Type.EmptyTypes, false);

            CharacterPredicate targetIsValid =
                AccessTools.MethodDelegate<CharacterPredicate>(targetIsValidMethod);
            CharacterPredicate hasLineOfSight =
                AccessTools.MethodDelegate<CharacterPredicate>(hasLineOfSightMethod);
            ZeroArgAction wakeUpFromSleep =
                AccessTools.MethodDelegate<ZeroArgAction>(wakeUpMethod);
            AccessTools.FieldRef<MushroomZombie, Character> character =
                AccessTools.FieldRefAccess<MushroomZombie, Character>(characterField);
            if (targetIsValid == null || hasLineOfSight == null ||
                wakeUpFromSleep == null || character == null)
            {
                throw new InvalidOperationException("SleepingZombieScan delegate binding failed");
            }

            _character = character;
            _targetIsValid = targetIsValid;
            _hasLineOfSight = hasLineOfSight;
            _wakeUpFromSleep = wakeUpFromSleep;
        }

        internal static MethodInfo ResolveAndValidateTarget()
        {
            return ScaleValidation.RequireInstanceMethod(
                typeof(MushroomZombie), "DoSleeping", typeof(void), Type.EmptyTypes, false);
        }

        internal static void Reset()
        {
            _character = null;
            _targetIsValid = null;
            _hasLineOfSight = null;
            _wakeUpFromSleep = null;
        }

        internal static bool Prefix(MushroomZombie __instance)
        {
            AccessTools.FieldRef<MushroomZombie, Character> characterRef = _character;
            CharacterPredicate targetIsValid = _targetIsValid;
            CharacterPredicate hasLineOfSight = _hasLineOfSight;
            ZeroArgAction wakeUp = _wakeUpFromSleep;
            if (characterRef == null || targetIsValid == null || hasLineOfSight == null || wakeUp == null)
            {
                RateLimitedDiagnostics.Hit("SleepingZombieScan.accessors-unavailable");
                return true;
            }

            Character self = characterRef(__instance);
            if (self == null || self.data == null)
            {
                RateLimitedDiagnostics.Hit("SleepingZombieScan.self-unavailable");
                return true;
            }

            self.data.fallSeconds = 10f;

            if (!__instance.visible)
                return false;

            float threshold = __instance.distanceBeforeWakeup;
            if (!(threshold > 0f))
            {
                RateLimitedDiagnostics.Hit("SleepingZombieScan.non-positive-threshold-fallback");
                return true;
            }

            List<Character> all = Character.AllCharacters;
            if (all == null)
            {
                RateLimitedDiagnostics.Hit("SleepingZombieScan.roster-null");
                return true;
            }

            Vector3 selfCenter = self.Center;
            float thresholdSqr = threshold * threshold;
            float maxAngle = __instance.lookAngleBeforeWakeup;

            for (int i = 0; i < all.Count; i++)
            {
                Character candidate = all[i];
                if (candidate == null)
                {
                    RateLimitedDiagnostics.Hit("SleepingZombieScan.candidate-null");
                    return true;
                }
                if (!targetIsValid(__instance, candidate))
                    continue;

                Vector3 delta = candidate.Center - selfCenter;
                if (!(delta.sqrMagnitude < thresholdSqr))
                    continue;

                Character.CharacterRefs refs = candidate.refs;
                Bodypart head = refs != null ? refs.head : null;
                Transform headTransform = head != null ? head.transform : null;
                if (headTransform == null)
                {
                    // The original would throw here; defer instead of silently changing the outcome.
                    RateLimitedDiagnostics.Hit("SleepingZombieScan.candidate-head-unavailable");
                    return true;
                }

                float angle = Vector3.Angle(headTransform.forward, selfCenter - headTransform.position);
                if (angle <= maxAngle && hasLineOfSight(__instance, candidate))
                {
                    wakeUp(__instance);
                    break;
                }
            }
            return false;
        }
    }
}
