using System;
using System.Reflection;
using HarmonyLib;
using PeakSafeOptimizer.Diagnostics;
using UnityEngine;

namespace PeakSafeOptimizer.Patches.Stability
{
    internal static class ParachutePatch
    {
        private static AccessTools.FieldRef<CharacterData, Character> _character;
        private static AccessTools.FieldRef<CharacterBackpackHandler, Character> _handlerCharacter;

        internal static void Initialize()
        {
            FieldInfo characterField = StabilityPatchValidation.RequireInstanceField(
                typeof(CharacterData), "character", typeof(Character));
            FieldInfo handlerCharacterField = StabilityPatchValidation.RequireInstanceField(
                typeof(CharacterBackpackHandler), "character", typeof(Character));

            ValidateCharacterStructure();
            ValidateCharacterDataStructure();
            ValidateInventoryStructure();
            ValidateBackpackStructure();

            AccessTools.FieldRef<CharacterData, Character> character =
                AccessTools.FieldRefAccess<CharacterData, Character>(characterField);
            AccessTools.FieldRef<CharacterBackpackHandler, Character> handlerCharacter =
                AccessTools.FieldRefAccess<CharacterBackpackHandler, Character>(handlerCharacterField);
            if (character == null || handlerCharacter == null)
                throw new InvalidOperationException("Parachute FieldRef accessor creation failed");

            _character = character;
            _handlerCharacter = handlerCharacter;
        }

        internal static MethodInfo ResolveAndValidateTarget()
        {
            return StabilityPatchValidation.RequireInstanceMethod(
                typeof(CharacterData),
                "UpdateHasParachute",
                typeof(void),
                Type.EmptyTypes);
        }

        internal static void Reset()
        {
            _character = null;
            _handlerCharacter = null;
        }

        // Full replacement for the short, MVID-gated method in CharacterData.cs:708-738.
        internal static bool Prefix(CharacterData __instance)
        {
            Character character = _character(__instance);

            // The original Scoutmaster branch returns before touching cached state.
            if (character != null && character.isScoutmaster)
                return false;

            __instance.hasParachute = false;
            __instance.currentParachuteSlot = null;
            __instance.currentParachuteItem = null;

            if (character == null)
            {
                RateLimitedDiagnostics.Hit("Parachute.character-null-or-destroyed");
                return false;
            }

            CharacterData data = character.data;
            if (data == null)
            {
                RateLimitedDiagnostics.Hit("Parachute.characterData-null-or-destroyed");
                return false;
            }
            Item held = data.currentItem;
            if (held != null)
            {
                GameObject heldObject = held.gameObject;
                if (heldObject == null)
                {
                    RateLimitedDiagnostics.Hit("Parachute.held-gameObject-null-or-destroyed");
                }
                else if (heldObject.CompareTag("Parachute"))
                {
                    __instance.currentParachuteItem = held;
                    __instance.hasParachute = true;
                    return false;
                }
            }

            Player player = character.player;
            if (player == null)
            {
                // Bots have no Photon Player, so Character.player - a PlayerHandler lookup keyed by
                // view.Owner.ActorNumber (Character.cs:201, PlayerHandler.cs:206-209) - is null for
                // them by design, on every frame. That is not a fault and must not be accounted as a
                // repair reason: 1899 hits in one session were all bots. The held-item branch above
                // still runs first, because a bot can carry a parachute.
                if (!character.isBot)
                    RateLimitedDiagnostics.Hit("Parachute.player-null-or-destroyed");
                return false;
            }

            ItemSlot[] slots = player.itemSlots;
            if (slots == null)
            {
                RateLimitedDiagnostics.Hit("Parachute.itemSlots-null");
                return false;
            }

            foreach (ItemSlot slot in slots)
            {
                if (slot == null)
                {
                    RateLimitedDiagnostics.Hit("Parachute.slot-null");
                    continue;
                }

                // Keep the audited method's IsEmpty-before-prefab access order.
                if (slot.IsEmpty())
                    continue;

                Item prefab = slot.prefab;
                if (prefab == null)
                {
                    RateLimitedDiagnostics.Hit("Parachute.nonempty-prefab-null-or-destroyed");
                    continue;
                }
                if (prefab.gameObject == null)
                {
                    RateLimitedDiagnostics.Hit("Parachute.prefab-gameObject-null-or-destroyed");
                    continue;
                }
                if (prefab.CompareTag("Parachute"))
                {
                    __instance.currentParachuteSlot = slot;
                    __instance.hasParachute = true;
                    return false;
                }
            }

            Character.CharacterRefs refs = character.refs;
            if (refs == null)
            {
                RateLimitedDiagnostics.Hit("Parachute.characterRefs-null");
                return false;
            }

            CharacterBackpackHandler handler = refs.backpackHandler;
            if (handler == null)
            {
                RateLimitedDiagnostics.Hit("Parachute.backpackHandler-null-or-destroyed");
                return false;
            }

            // activeBackpackVisuals dereferences handler.character.player.backpackSlot.
            // On the Unity main thread the already-resolved player remains the same lookup;
            // validate all other getter prerequisites before invoking it.
            Character handlerOwner = _handlerCharacter(handler);
            if (handlerOwner == null || handlerOwner != character)
            {
                RateLimitedDiagnostics.Hit("Parachute.backpackHandler-owner-invalid");
                return false;
            }
            if (player.backpackSlot == null)
            {
                RateLimitedDiagnostics.Hit("Parachute.backpackSlot-null");
                return false;
            }

            BackpackOnBackVisuals visuals = handler.activeBackpackVisuals;
            if (visuals != null && visuals.ContainsParachute(out Item spawnedParachute))
            {
                __instance.currentParachuteItem = spawnedParachute;
                __instance.hasParachute = true;
            }
            return false;
        }

        private static void ValidateCharacterStructure()
        {
            StabilityPatchValidation.RequireInstanceField(
                typeof(Character), "isScoutmaster", typeof(bool));
            StabilityPatchValidation.RequireInstanceField(
                typeof(Character), "isBot", typeof(bool));
            StabilityPatchValidation.RequireInstanceField(
                typeof(Character), "data", typeof(CharacterData));
            StabilityPatchValidation.RequireInstanceProperty(
                typeof(Character), "player", typeof(Player));
            StabilityPatchValidation.RequireInstanceField(
                typeof(Character), "refs", typeof(Character.CharacterRefs));
        }

        private static void ValidateCharacterDataStructure()
        {
            StabilityPatchValidation.RequireInstanceField(
                typeof(CharacterData), "hasParachute", typeof(bool));
            StabilityPatchValidation.RequireInstanceField(
                typeof(CharacterData), "currentParachuteSlot", typeof(ItemSlot));
            StabilityPatchValidation.RequireInstanceField(
                typeof(CharacterData), "currentParachuteItem", typeof(Item));
            StabilityPatchValidation.RequireInstanceProperty(
                typeof(CharacterData), "currentItem", typeof(Item));
        }

        private static void ValidateInventoryStructure()
        {
            StabilityPatchValidation.RequireInstanceField(
                typeof(Player), "itemSlots", typeof(ItemSlot[]));
            StabilityPatchValidation.RequireInstanceField(
                typeof(Player), "backpackSlot", typeof(BackpackSlot));
            StabilityPatchValidation.RequireInstanceMethod(
                typeof(ItemSlot), "IsEmpty", typeof(bool), Type.EmptyTypes);
            StabilityPatchValidation.RequireInstanceProperty(
                typeof(ItemSlot), "prefab", typeof(Item));
        }

        private static void ValidateBackpackStructure()
        {
            StabilityPatchValidation.RequireInstanceField(
                typeof(Character.CharacterRefs),
                "backpackHandler",
                typeof(CharacterBackpackHandler));
            StabilityPatchValidation.RequireInstanceProperty(
                typeof(CharacterBackpackHandler),
                "activeBackpackVisuals",
                typeof(BackpackOnBackVisuals));

            Type itemByRef = typeof(Item).MakeByRefType();
            MethodInfo containsParachute = StabilityPatchValidation.RequireInstanceMethod(
                typeof(BackpackVisuals),
                "ContainsParachute",
                typeof(bool),
                new[] { itemByRef });
            ParameterInfo output = containsParachute.GetParameters()[0];
            if (!output.IsOut || output.IsIn)
                throw new InvalidOperationException(
                    "BackpackVisuals.ContainsParachute parameter is no longer an out Item");
        }
    }
}
