using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using PeakSafeOptimizer.Diagnostics;
using UnityEngine;

namespace PeakSafeOptimizer.Patches.Equivalent.MathAndScan
{
    /// <summary>
    /// Replacement for Campfire.EveryoneInRange(float), a per-frame call per lit campfire. A live player is in
    /// _charactersInRadius exactly when !(Vector3.Distance(position, character.Center) > range), so "absent
    /// from that list" and "out of range" are one predicate. The negated form is kept for equality, negative
    /// range and NaN, and the scan leaves the list holding the same players in the same order.
    /// </summary>
    internal static class CampfirePatch
    {
        private static AccessTools.FieldRef<Campfire, List<Character>> _charactersInRadius;

        internal static void Initialize()
        {
            MethodInfo target = ResolveAndValidateTarget();
            if (!string.Equals(target.GetParameters()[0].Name, "range", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Campfire.EveryoneInRange parameter is no longer named range");
            }

            FieldInfo charactersInRadiusField = MathAndScanValidation.RequireInstanceField(
                typeof(Campfire), "_charactersInRadius", typeof(List<Character>));
            if (charactersInRadiusField.IsInitOnly)
                throw new InvalidOperationException("Campfire._charactersInRadius is unexpectedly readonly");
            MathAndScanValidation.RequireStaticMethod(
                typeof(PlayerHandler), "GetAllPlayerCharacters", typeof(List<Character>),
                Type.EmptyTypes, true);
            MathAndScanValidation.RequireInstanceField(
                typeof(Character), "data", typeof(CharacterData));
            MathAndScanValidation.RequireInstanceProperty(
                typeof(CharacterData), "dead", typeof(bool));
            MathAndScanValidation.RequireInstanceProperty(
                typeof(Character), "Center", typeof(Vector3));
            MathAndScanValidation.RequireStaticMethod(
                typeof(Vector3), "Distance", typeof(float),
                new[] { typeof(Vector3), typeof(Vector3) }, true);

            PropertyInfo campfireTransform = typeof(Campfire).GetProperty(
                "transform", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            MethodInfo campfireTransformGetter = campfireTransform?.GetGetMethod(true);
            if (campfireTransform == null || campfireTransform.PropertyType != typeof(Transform) ||
                campfireTransform.GetIndexParameters().Length != 0 ||
                campfireTransformGetter == null || campfireTransformGetter.IsStatic)
            {
                throw new InvalidOperationException("Campfire.transform failed structural self-check");
            }

            AccessTools.FieldRef<Campfire, List<Character>> charactersInRadius =
                AccessTools.FieldRefAccess<Campfire, List<Character>>(charactersInRadiusField);
            if (charactersInRadius == null)
                throw new InvalidOperationException("Campfire._charactersInRadius FieldRef creation failed");
            _charactersInRadius = charactersInRadius;
        }

        internal static MethodInfo ResolveAndValidateTarget()
        {
            return MathAndScanValidation.RequireInstanceMethod(
                typeof(Campfire), "EveryoneInRange", typeof(bool),
                new[] { typeof(float) }, true);
        }

        internal static void Reset()
        {
            _charactersInRadius = null;
        }

        internal static bool Prefix(Campfire __instance, float range, ref bool __result)
        {
            AccessTools.FieldRef<Campfire, List<Character>> charactersInRadiusRef = _charactersInRadius;
            if (charactersInRadiusRef == null)
            {
                RateLimitedDiagnostics.Hit("Campfire.charactersInRadius-fieldref-unavailable");
                return true;
            }

            List<Character> characters = PlayerHandler.GetAllPlayerCharacters();
            List<Character> charactersInRadius = charactersInRadiusRef(__instance);
            if (charactersInRadius == null)
            {
                RateLimitedDiagnostics.Hit("Campfire.charactersInRadius-null-fallback-original");
                return true;
            }
            charactersInRadius.Clear();

            Vector3 position = __instance.transform.position;
            bool everyone = true;
            for (int i = 0; i < characters.Count; i++)
            {
                Character character = characters[i];
                if (character.data.dead)
                    continue;

                if (!(Vector3.Distance(position, character.Center) > range))
                    charactersInRadius.Add(character);
                else
                    everyone = false;
            }

            __result = everyone;
            return false;
        }
    }
}
