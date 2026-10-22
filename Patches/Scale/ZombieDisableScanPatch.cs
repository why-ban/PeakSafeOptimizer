using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using PeakSafeOptimizer.Diagnostics;
using UnityEngine;

namespace PeakSafeOptimizer.Patches.Scale
{
    /// <summary>
    /// Equivalent replacement of MushroomZombie.ReadyToDisable. Squared distances are exact because
    /// both thresholds are positive and the comparisons are non-strict; a non-positive
    /// distanceToEnable + 5 falls back to the original. The loop stops once both flags are set, which
    /// only skips writes of true into flags already true.
    /// </summary>
    internal static class ZombieDisableScanPatch
    {
        private static AccessTools.FieldRef<MushroomZombie, Character> _character;
        private static AccessTools.FieldRef<MushroomZombie, float> _timeDiedAt;

        internal static void Initialize()
        {
            ResolveAndValidateTarget();

            FieldInfo characterField = ScaleValidation.RequireInstanceField(
                typeof(MushroomZombie), "character", typeof(Character));
            ScaleValidation.RequireInstanceField(
                typeof(MushroomZombie), "distanceToEnable", typeof(float));
            ScaleValidation.RequireInstanceField(
                typeof(MushroomZombie), "_currentState", typeof(MushroomZombie.State));
            ScaleValidation.RequireInstanceProperty(
                typeof(MushroomZombie), "currentState", typeof(MushroomZombie.State));
            FieldInfo timeDiedAtField = ScaleValidation.RequireInstanceField(
                typeof(MushroomZombie), "timeDiedAt", typeof(float));
            if (timeDiedAtField.IsPublic)
                throw new InvalidOperationException(
                    "MushroomZombie.timeDiedAt is no longer a private instance field");
            ScaleValidation.RequireStaticField(typeof(Character), "AllCharacters", typeof(List<Character>));
            ScaleValidation.RequireInstanceProperty(typeof(Character), "Center", typeof(Vector3));
            ScaleValidation.RequireStaticMethod(
                typeof(Vector3), "Distance", typeof(float),
                new[] { typeof(Vector3), typeof(Vector3) }, true);
            if (!Enum.IsDefined(typeof(MushroomZombie.State), MushroomZombie.State.Dead) ||
                !Enum.IsDefined(typeof(MushroomZombie.State), MushroomZombie.State.Sleeping))
            {
                throw new InvalidOperationException("MushroomZombie.State.Dead/Sleeping is no longer defined");
            }

            AccessTools.FieldRef<MushroomZombie, Character> character =
                AccessTools.FieldRefAccess<MushroomZombie, Character>(characterField);
            AccessTools.FieldRef<MushroomZombie, float> timeDiedAt =
                AccessTools.FieldRefAccess<MushroomZombie, float>(timeDiedAtField);
            if (character == null || timeDiedAt == null)
                throw new InvalidOperationException("ZombieDisableScan accessor creation failed");
            _character = character;
            _timeDiedAt = timeDiedAt;
        }

        internal static MethodInfo ResolveAndValidateTarget()
        {
            return ScaleValidation.RequireInstanceMethod(
                typeof(MushroomZombie), "ReadyToDisable", typeof(bool), Type.EmptyTypes, true);
        }

        internal static void Reset()
        {
            _character = null;
            _timeDiedAt = null;
        }

        internal static bool Prefix(MushroomZombie __instance, ref bool __result)
        {
            AccessTools.FieldRef<MushroomZombie, Character> characterRef = _character;
            AccessTools.FieldRef<MushroomZombie, float> timeDiedAtRef = _timeDiedAt;
            if (characterRef == null || timeDiedAtRef == null)
            {
                RateLimitedDiagnostics.Hit("ZombieDisableScan.accessors-unavailable");
                return true;
            }

            if (__instance.currentState == MushroomZombie.State.Dead &&
                timeDiedAtRef(__instance) + 10f < Time.time)
            {
                __result = true;
                return false;
            }

            Character self = characterRef(__instance);
            List<Character> all = Character.AllCharacters;
            if (self == null || all == null)
            {
                RateLimitedDiagnostics.Hit("ZombieDisableScan.inputs-unavailable");
                return true;
            }

            float nearThreshold = __instance.distanceToEnable + 5f;
            if (!(nearThreshold > 0f))
            {
                RateLimitedDiagnostics.Hit("ZombieDisableScan.non-positive-threshold-fallback");
                return true;
            }

            Vector3 origin = self.Center;
            float farSqr = 100f * 100f;
            float nearSqr = nearThreshold * nearThreshold;
            if (!(nearSqr > 0f) || float.IsInfinity(nearSqr))
                return true;
            bool near = false;
            bool far = false;

            for (int i = 0; i < all.Count; i++)
            {
                Character candidate = all[i];
                if (candidate == null)
                    continue;
                Vector3 delta = candidate.Center - origin;
                float distanceSqr = delta.sqrMagnitude;
                if (distanceSqr <= farSqr)
                    far = true;
                if (distanceSqr <= nearSqr)
                    near = true;
                if (near && far)
                    break;
            }

            if (!far)
            {
                __result = true;
                return false;
            }
            if (__instance.currentState != MushroomZombie.State.Dead &&
                __instance.currentState != MushroomZombie.State.Sleeping)
            {
                __result = false;
                return false;
            }
            __result = !near;
            return false;
        }
    }
}
