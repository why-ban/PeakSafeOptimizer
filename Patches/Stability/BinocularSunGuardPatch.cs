using System;
using System.Collections.Generic;
using System.Reflection;
using PeakSafeOptimizer.Diagnostics;
using UnityEngine;
using Zorro.Core;

namespace PeakSafeOptimizer.Patches.Stability
{
    /// <summary>
    /// Guards Action_ShowBinocularOverlay.TestLookAtSun, whose guard clause checks only localCharacter before
    /// dereferencing MapHandler, DayNightManager.sun, MainCamera, Character.Center (a partDict indexer) and
    /// AchievementManager. The Prefix reproduces that clause in vanilla's order first, since counting a later
    /// reason on a frame that returns at an earlier operand is a false repair.
    /// </summary>
    internal static class BinocularSunGuardPatch
    {
        internal static void Initialize()
        {
            ResolveAndValidateTarget();

            StabilityPatchValidation.RequireInstanceMethod(
                typeof(MapHandler), "GetCurrentBiome", typeof(Biome.BiomeType), Type.EmptyTypes);
            if (!Enum.IsDefined(typeof(Biome.BiomeType), Biome.BiomeType.Mesa))
                throw new InvalidOperationException("Biome.BiomeType.Mesa is no longer defined");
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
            Character local = Character.localCharacter;
            if (!(bool)local)
                return true;

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
                RateLimitedDiagnostics.Hit("BinocularSunGuard.biome-query-failed");
                return false;
            }
            if (biome != Biome.BiomeType.Mesa)
            {
                return true;
            }

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
                return true;
            }

            if (!(bool)MainCamera.instance)
            {
                RateLimitedDiagnostics.Hit("BinocularSunGuard.mainCamera-unavailable");
                return false;
            }

            if (!TorsoIsAvailable(local))
            {
                RateLimitedDiagnostics.Hit("BinocularSunGuard.center-unavailable");
                return false;
            }

            if (!(bool)(UnityEngine.Object)Singleton<AchievementManager>.Instance)
            {
                RateLimitedDiagnostics.Hit("BinocularSunGuard.achievementManager-unavailable");
                return false;
            }

            return true;
        }

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
