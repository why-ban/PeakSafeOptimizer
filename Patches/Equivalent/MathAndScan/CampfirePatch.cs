using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using PeakSafeOptimizer.Diagnostics;
using UnityEngine;

namespace PeakSafeOptimizer.Patches.Equivalent.MathAndScan
{
    internal static class CampfirePatch
    {
        private static AccessTools.FieldRef<Campfire, List<Character>> _charactersInRadius;

        internal static void Initialize()
        {
            ResolveAndValidateTarget();
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
                // This is patch-owned state, not an original-method failure. Let the game
                // run its unmodified method rather than introducing a runtime exception.
                RateLimitedDiagnostics.Hit("Campfire.charactersInRadius-fieldref-unavailable");
                return true;
            }

            List<Character> characters = PlayerHandler.GetAllPlayerCharacters();
            List<Character> charactersInRadius = charactersInRadiusRef(__instance);
            if (charactersInRadius == null)
            {
                // Preserve the original method's behavior for game-owned null state. Do not
                // catch or suppress any exception that the original method itself may raise.
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

                // Preserve the original !(Distance > range) comparison exactly, including
                // equality, negative range and NaN behavior.
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
