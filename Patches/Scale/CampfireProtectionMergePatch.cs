using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Peak.Afflictions;
using PeakSafeOptimizer.Diagnostics;
using UnityEngine;
using Zorro.Core;

namespace PeakSafeOptimizer.Patches.Scale
{
    /// <summary>
    /// Full narrow replacement of Campfire.ApplyCampfireProtection() (audited source
    /// Campfire.cs:222-237). The original performs the same full-player scan twice in one frame:
    /// once inside EveryoneInRange() (which already populates _charactersInRadius) and once inside
    /// PlayerCharactersInRadius(moraleBoostRadius) at the same radius. This patch performs a single
    /// scan that both produces the same final _charactersInRadius contents and computes
    /// everyoneInRange, then reuses those results for both the OrbFogHandler write and the buff
    /// branch, preserving the original semantics exactly.
    ///
    /// Equivalence notes:
    ///   - _charactersInRadius must end up holding exactly the alive players satisfying
    ///     !(Vector3.Distance(pos, center) > moraleBoostRadius), in PlayerHandler order. That is
    ///     what the original PlayerCharactersInRadius leaves behind, and the single scan reproduces it.
    ///   - everyoneInRange equals the original EveryoneInRange(): true iff every alive player is in
    ///     range. With PlayerHandler.Exists == false, GetAllPlayerCharacters() yields an empty list,
    ///     so everyoneInRange is true and _charactersInRadius is empty, matching the original.
    ///   - The buff branch uses localInRange = _charactersInRadius.Contains(localCharacter); a null
    ///     localCharacter yields false (List.Contains handles null), and the buff branch defensively
    ///     skips (with one rate-limited diagnostic) instead of the original's NullReferenceException.
    /// </summary>
    internal static class CampfireProtectionMergePatch
    {
        private static AccessTools.FieldRef<Campfire, List<Character>> _charactersInRadius;
        private static AccessTools.FieldRef<Campfire, float> _timebuffLastApplied;
        private static AccessTools.FieldRef<Affliction_NoHunger> _campfireBuffGetter;

        internal static void Initialize()
        {
            ResolveAndValidateTarget();

            FieldInfo charactersInRadiusField = ScaleValidation.RequireInstanceField(
                typeof(Campfire), "_charactersInRadius", typeof(List<Character>));
            FieldInfo timebuffField = ScaleValidation.RequireInstanceField(
                typeof(Campfire), "_timebuffLastApplied", typeof(float));
            ScaleValidation.RequireInstanceField(typeof(Campfire), "moraleBoostRadius", typeof(float));

            // s_CampfireBuff is a private static Affliction_NoHunger; type-checked once, but its
            // current value is read every Prefix so a runtime reassignment is never missed.
            FieldInfo buffField = typeof(Campfire).GetField(
                "s_CampfireBuff", BindingFlags.Static | BindingFlags.NonPublic);
            if (buffField == null || !buffField.IsStatic ||
                buffField.FieldType != typeof(Affliction_NoHunger))
            {
                throw new InvalidOperationException("Campfire.s_CampfireBuff failed structural self-check");
            }
            AccessTools.FieldRef<Affliction_NoHunger> campfireBuffGetter =
                AccessTools.StaticFieldRefAccess<Affliction_NoHunger>(buffField);
            if (campfireBuffGetter == null)
                throw new InvalidOperationException("Campfire.s_CampfireBuff getter creation failed");

            ScaleValidation.RequireStaticField(typeof(Character), "localCharacter", typeof(Character));
            ScaleValidation.RequireStaticProperty(typeof(PlayerHandler), "Exists", typeof(bool));
            ScaleValidation.RequireStaticMethod(
                typeof(PlayerHandler), "GetAllPlayerCharacters", typeof(List<Character>),
                Type.EmptyTypes, true);
            ScaleValidation.RequireInstanceField(typeof(Character), "data", typeof(CharacterData));
            ScaleValidation.RequireInstanceField(typeof(Character), "refs", typeof(Character.CharacterRefs));
            ScaleValidation.RequireInstanceField(
                typeof(Character.CharacterRefs), "afflictions", typeof(CharacterAfflictions));
            ScaleValidation.RequireInstanceProperty(typeof(CharacterData), "dead", typeof(bool));
            ScaleValidation.RequireInstanceProperty(typeof(Character), "Center", typeof(Vector3));
            ScaleValidation.RequireInstanceProperty(
                typeof(CharacterAfflictions), "canGetHungry", typeof(bool));
            ScaleValidation.RequireInstanceMethod(
                typeof(CharacterAfflictions), "AddAffliction", typeof(void),
                new[] { typeof(Affliction), typeof(bool) }, true);

            // OrbFogHandler is a Singleton<OrbFogHandler>; its Instance getter and the
            // PlayersAreResting setter must keep the audited shape.
            ScaleValidation.RequireStaticProperty(
                typeof(Singleton<OrbFogHandler>), "Instance", typeof(OrbFogHandler));
            ScaleValidation.RequireInstanceProperty(
                typeof(OrbFogHandler), "PlayersAreResting", typeof(bool));

            AccessTools.FieldRef<Campfire, List<Character>> charactersInRadius =
                AccessTools.FieldRefAccess<Campfire, List<Character>>(charactersInRadiusField);
            AccessTools.FieldRef<Campfire, float> timebuff =
                AccessTools.FieldRefAccess<Campfire, float>(timebuffField);
            if (charactersInRadius == null || timebuff == null)
                throw new InvalidOperationException("CampfireProtectionMerge accessor creation failed");

            _charactersInRadius = charactersInRadius;
            _timebuffLastApplied = timebuff;
            _campfireBuffGetter = campfireBuffGetter;
        }

        internal static MethodInfo ResolveAndValidateTarget()
        {
            return ScaleValidation.RequireInstanceMethod(
                typeof(Campfire), "ApplyCampfireProtection", typeof(void), Type.EmptyTypes, false);
        }

        internal static void Reset()
        {
            _charactersInRadius = null;
            _timebuffLastApplied = null;
            _campfireBuffGetter = null;
        }

        internal static bool Prefix(Campfire __instance)
        {
            AccessTools.FieldRef<Campfire, List<Character>> charactersInRadiusRef = _charactersInRadius;
            AccessTools.FieldRef<Campfire, float> timebuffRef = _timebuffLastApplied;
            AccessTools.FieldRef<Affliction_NoHunger> campfireBuffGetter = _campfireBuffGetter;
            Affliction buff = null;
            if (campfireBuffGetter != null)
                buff = campfireBuffGetter();
            if (charactersInRadiusRef == null || timebuffRef == null || buff == null)
            {
                RateLimitedDiagnostics.Hit("CampfireProtectionMerge.accessors-unavailable");
                return true;
            }

            List<Character> inRadius = charactersInRadiusRef(__instance);
            if (inRadius == null)
            {
                RateLimitedDiagnostics.Hit("CampfireProtectionMerge.charactersInRadius-null");
                return true;
            }

            // Single scan: reproduce EveryoneInRange() + PlayerCharactersInRadius() in one pass.
            bool everyone = true;
            inRadius.Clear();
            if (PlayerHandler.Exists)
            {
                List<Character> characters = PlayerHandler.GetAllPlayerCharacters();
                if (characters == null)
                {
                    RateLimitedDiagnostics.Hit("CampfireProtectionMerge.roster-null");
                    return true;
                }
                Vector3 position = __instance.transform.position;
                float radius = __instance.moraleBoostRadius;
                for (int i = 0; i < characters.Count; i++)
                {
                    Character candidate = characters[i];
                    if (candidate == null || candidate.data == null)
                        continue;
                    if (candidate.data.dead)
                        continue;
                    if (!(Vector3.Distance(position, candidate.Center) > radius))
                        inRadius.Add(candidate);
                    else
                        everyone = false;
                }
            }

            // OrbFogHandler write uses everyoneInRange (same as the original EveryoneInRange()).
            OrbFogHandler fog = Singleton<OrbFogHandler>.Instance;
            if ((bool)fog)
                fog.PlayersAreResting = everyone;

            // Buff branch: !Exists || localInRange. A null localCharacter is false here and
            // defensively skipped below instead of throwing.
            Character local = Character.localCharacter;
            bool localInRange = local != null && inRadius.Contains(local);
            if (!PlayerHandler.Exists || localInRange)
            {
                if (local == null || local.refs == null)
                {
                    RateLimitedDiagnostics.Hit("CampfireProtectionMerge.localCharacter-null");
                    return false;
                }
                CharacterAfflictions afflictions = local.refs.afflictions;
                if (afflictions == null)
                {
                    RateLimitedDiagnostics.Hit("CampfireProtectionMerge.afflictions-null");
                    return false;
                }
                if (afflictions.canGetHungry || Time.time - timebuffRef(__instance) > 2f)
                {
                    afflictions.AddAffliction(buff);
                    timebuffRef(__instance) = Time.time;
                }
            }
            return false;
        }
    }
}
