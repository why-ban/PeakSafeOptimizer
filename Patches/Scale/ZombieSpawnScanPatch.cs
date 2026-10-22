using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using PeakSafeOptimizer.Diagnostics;
using UnityEngine;

namespace PeakSafeOptimizer.Patches.Scale
{
    /// <summary>
    /// Equivalent replacement of MushroomZombieSpawner.ReadyToSpawn. The squared comparison is exact
    /// because the threshold is non-negative and the original comparison is non-strict; a negative
    /// threshold falls back to the original. transform.position is hoisted out of the loop because
    /// nothing in it moves the spawner.
    /// </summary>
    internal static class ZombieSpawnScanPatch
    {
        private static AccessTools.FieldRef<MushroomZombieSpawner, bool> _spawned;

        internal static void Initialize()
        {
            ResolveAndValidateTarget();

            FieldInfo spawnedField = ScaleValidation.RequireInstanceField(
                typeof(MushroomZombieSpawner), "spawned", typeof(bool));
            if (spawnedField.IsPublic)
                throw new InvalidOperationException(
                    "MushroomZombieSpawner.spawned is no longer a private instance field");
            ScaleValidation.RequireInstanceField(
                typeof(MushroomZombieSpawner), "spawnedZombie", typeof(MushroomZombie));
            ScaleValidation.RequireInstanceField(
                typeof(MushroomZombieSpawner), "mushroomZombiePrefab", typeof(MushroomZombie));
            ScaleValidation.RequireInstanceField(
                typeof(MushroomZombie), "distanceToEnable", typeof(float));
            ScaleValidation.RequireStaticField(typeof(Character), "AllCharacters", typeof(List<Character>));
            ScaleValidation.RequireInstanceProperty(typeof(Character), "Center", typeof(Vector3));
            ScaleValidation.RequireStaticMethod(
                typeof(Vector3), "Distance", typeof(float),
                new[] { typeof(Vector3), typeof(Vector3) }, true);

            AccessTools.FieldRef<MushroomZombieSpawner, bool> spawned =
                AccessTools.FieldRefAccess<MushroomZombieSpawner, bool>(spawnedField);
            if (spawned == null)
                throw new InvalidOperationException("ZombieSpawnScan accessor creation failed");
            _spawned = spawned;
        }

        internal static MethodInfo ResolveAndValidateTarget()
        {
            return ScaleValidation.RequireInstanceMethod(
                typeof(MushroomZombieSpawner), "ReadyToSpawn", typeof(bool), Type.EmptyTypes, true);
        }

        internal static void Reset() => _spawned = null;

        internal static bool Prefix(MushroomZombieSpawner __instance, ref bool __result)
        {
            AccessTools.FieldRef<MushroomZombieSpawner, bool> spawnedRef = _spawned;
            if (spawnedRef == null)
            {
                RateLimitedDiagnostics.Hit("ZombieSpawnScan.accessors-unavailable");
                return true;
            }

            if (__instance.spawnedZombie != null || spawnedRef(__instance))
            {
                __result = false;
                return false;
            }

            MushroomZombie prefab = __instance.mushroomZombiePrefab;
            List<Character> all = Character.AllCharacters;
            if (prefab == null || all == null)
            {
                RateLimitedDiagnostics.Hit("ZombieSpawnScan.inputs-unavailable");
                return true;
            }

            float threshold = prefab.distanceToEnable;
            if (!(threshold >= 0f))
            {
                RateLimitedDiagnostics.Hit("ZombieSpawnScan.negative-threshold-fallback");
                return true;
            }

            Vector3 origin = __instance.transform.position;
            float thresholdSqr = threshold * threshold;
            if (threshold > 0f && (!(thresholdSqr > 0f) || float.IsInfinity(thresholdSqr)))
                return true;
            for (int i = 0; i < all.Count; i++)
            {
                Character candidate = all[i];
                if (candidate == null)
                    continue;
                Vector3 delta = candidate.Center - origin;
                if (delta.sqrMagnitude <= thresholdSqr)
                {
                    __result = true;
                    return false;
                }
            }

            __result = false;
            return false;
        }
    }
}
