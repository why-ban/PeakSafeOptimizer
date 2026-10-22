using System;
using System.Reflection;
using PeakSafeOptimizer.Diagnostics;

namespace PeakSafeOptimizer.Patches.Stability
{
    /// <summary>
    /// Guards GUIManager.UpdateEmoteWheel. It mirrors both short circuits of the vanilla condition, so it
    /// skips only frames vanilla would dereference null on and never the closing branch that clears
    /// usingEmoteWheel, whose latch also holds the movement gate and cursor lock. localCharacter uses Unity
    /// null because LateUpdate already gates on it, the rest by reference since vanilla reads managed fields.
    /// </summary>
    internal static class EmoteWheelGuardPatch
    {
        internal static void Initialize()
        {
            ResolveAndValidateTarget();

            FieldInfo localCharacterField = typeof(Character).GetField(
                "localCharacter",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.DeclaredOnly);
            if (localCharacterField == null || !localCharacterField.IsStatic ||
                localCharacterField.DeclaringType != typeof(Character) ||
                localCharacterField.FieldType != typeof(Character))
            {
                throw new InvalidOperationException("Character.localCharacter failed structural self-check");
            }

            StabilityPatchValidation.RequireInstanceField(
                typeof(Character), "data", typeof(CharacterData));
            StabilityPatchValidation.RequireInstanceField(
                typeof(Character), "refs", typeof(Character.CharacterRefs));
            StabilityPatchValidation.RequireInstanceField(
                typeof(Character.CharacterRefs), "stats", typeof(CharacterStats));
            StabilityPatchValidation.RequireInstanceField(
                typeof(Character), "input", typeof(CharacterInput));

            StabilityPatchValidation.RequireInstanceProperty(
                typeof(CharacterData), "dead", typeof(bool));
            StabilityPatchValidation.RequireInstanceField(
                typeof(CharacterStats), "won", typeof(bool));
            StabilityPatchValidation.RequireInstanceProperty(
                typeof(GUIManager), "canEmote", typeof(bool), requirePublicGetter: false);

            StabilityPatchValidation.RequireInstanceField(
                typeof(CharacterInput), "emoteIsPressed", typeof(bool));
            StabilityPatchValidation.RequireInstanceField(
                typeof(CharacterData), "usingEmoteWheel", typeof(bool));
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
            Character local = Character.localCharacter;
            if (!(bool)local)
            {
                RateLimitedDiagnostics.Hit("EmoteWheelGuard.localCharacter-unavailable");
                return false;
            }

            CharacterData data = local.data;
            if ((object)data == null)
            {
                RateLimitedDiagnostics.Hit("EmoteWheelGuard.data-unavailable");
                return false;
            }

            bool canEmote;
            if (data.dead)
            {
                canEmote = false;
            }
            else
            {
                Character.CharacterRefs refs = local.refs;
                if (refs == null)
                {
                    RateLimitedDiagnostics.Hit("EmoteWheelGuard.refs-unavailable");
                    return false;
                }

                CharacterStats stats = refs.stats;
                if ((object)stats == null)
                {
                    RateLimitedDiagnostics.Hit("EmoteWheelGuard.stats-unavailable");
                    return false;
                }

                canEmote = !stats.won;
            }

            if (canEmote && (object)local.input == null)
            {
                RateLimitedDiagnostics.Hit("EmoteWheelGuard.input-unavailable");
                return false;
            }

            return true;
        }
    }
}
