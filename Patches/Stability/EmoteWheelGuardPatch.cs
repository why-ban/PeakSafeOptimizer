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
    /// UpdateEmoteWheel dereferences Character.localCharacter in both of its branches, and its guard
    /// expression calls the canEmote getter (GUIManager.cs:303-313), which dereferences
    /// Character.localCharacter.data.dead and Character.localCharacter.refs.stats.won. When the
    /// local character is null or destroyed (spectating / respawn / quitting), every LateUpdate
    /// throws NullReferenceException and the log floods.
    ///
    /// This Prefix is read-only: it only checks Character.localCharacter with Unity object semantics
    /// and skips the original when the character is gone. It never writes any state and never
    /// changes behavior for any frame where a local character exists.
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
                return true;

            RateLimitedDiagnostics.Hit("EmoteWheelGuard.localCharacter-unavailable");
            return false;
        }
    }
}
