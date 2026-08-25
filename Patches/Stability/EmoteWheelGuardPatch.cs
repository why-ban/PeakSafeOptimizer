using System;
using System.Reflection;
using HarmonyLib;
using PeakSafeOptimizer.Diagnostics;
using UnityEngine;

namespace PeakSafeOptimizer.Patches.Stability
{
    /// <summary>
    /// Null guard for GUIManager.UpdateEmoteWheel() (audited source GUIManager.cs:552-567), invoked
    /// every frame from GUIManager.LateUpdate.
    ///
    /// Root cause (confirmed in-game, 2315 occurrences in one multiplayer session):
    /// the canEmote guard and UpdateEmoteWheel both depend on a complete local character graph.
    /// During spectating, respawn, quit, or a partial teardown, localCharacter, data, refs, or stats
    /// can be unavailable even though the call reaches this method through a third-party path.
    ///
    /// This Prefix is read-only: it checks the complete member chain with Unity object semantics and
    /// skips the original when any dependency is unavailable. It never writes state.
    ///
    /// The members are reported separately, because which one is missing identifies the cause.
    /// GUIManager.LateUpdate already guards localCharacter itself (GUIManager.cs:353) and dereferences
    /// localCharacter.data two lines before the call (:363), so in practice a hit here means
    /// refs, refs.stats or input is missing - i.e. the local character is mid-rebuild or another mod
    /// has replaced it. Note this can never be a bot: localCharacter is the local player by
    /// definition.
    /// </summary>
    internal static class EmoteWheelGuardPatch
    {
        internal static void Initialize()
        {
            ResolveAndValidateTarget();

            FieldInfo localCharacterField = typeof(Character).GetField(
                "localCharacter", BindingFlags.Static | BindingFlags.Public);
            if (localCharacterField == null || !localCharacterField.IsStatic ||
                localCharacterField.FieldType != typeof(Character))
            {
                throw new InvalidOperationException("Character.localCharacter failed structural self-check");
            }

            // Members the extended guard chain reads. Any reshape fails the patch closed instead of
            // letting the guard silently stop covering the audited canEmote dependencies.
            StabilityPatchValidation.RequireInstanceField(
                typeof(Character), "data", typeof(CharacterData));
            StabilityPatchValidation.RequireInstanceField(
                typeof(Character), "refs", typeof(Character.CharacterRefs));
            StabilityPatchValidation.RequireInstanceField(
                typeof(Character.CharacterRefs), "stats", typeof(CharacterStats));
            // canEmote reads data.dead and refs.stats.won (GUIManager.cs:303-313); the method body
            // then dereferences input.emoteIsPressed (:554), which is a serialized field and is the
            // one remaining unguarded link in the same chain.
            StabilityPatchValidation.RequireInstanceField(
                typeof(Character), "input", typeof(CharacterInput));
            StabilityPatchValidation.RequireInstanceField(
                typeof(CharacterInput), "emoteIsPressed", typeof(bool));
        }

        internal static MethodInfo ResolveAndValidateTarget()
        {
            return StabilityPatchValidation.RequireInstanceMethod(
                typeof(GUIManager), "UpdateEmoteWheel", typeof(void), Type.EmptyTypes,
                requirePublic: false);
        }

        internal static void Reset()
        {
        }

        internal static bool Prefix()
        {
            // Unity null semantics: false for both a null reference and a destroyed object.
            Character local = Character.localCharacter;
            if (!(bool)local)
            {
                RateLimitedDiagnostics.Hit("EmoteWheelGuard.localCharacter-unavailable");
                return false;
            }

            if (local.data == null)
            {
                RateLimitedDiagnostics.Hit("EmoteWheelGuard.data-unavailable");
                return false;
            }

            Character.CharacterRefs refs = local.refs;
            if (refs == null)
            {
                RateLimitedDiagnostics.Hit("EmoteWheelGuard.refs-unavailable");
                return false;
            }

            if (refs.stats == null)
            {
                RateLimitedDiagnostics.Hit("EmoteWheelGuard.stats-unavailable");
                return false;
            }

            if (local.input == null)
            {
                RateLimitedDiagnostics.Hit("EmoteWheelGuard.input-unavailable");
                return false;
            }

            return true;
        }
    }
}
