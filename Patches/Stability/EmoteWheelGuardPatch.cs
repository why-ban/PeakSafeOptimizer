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
            if ((bool)Character.localCharacter)
            {
                Character local = Character.localCharacter;
                if (local.data != null && local.refs != null && local.refs.stats != null)
                    return true;

                RateLimitedDiagnostics.Hit("EmoteWheelGuard.localCharacter-members-unavailable");
                return false;
            }

            RateLimitedDiagnostics.Hit("EmoteWheelGuard.localCharacter-unavailable");
            return false;
        }
    }
}
