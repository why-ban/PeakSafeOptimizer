using System;
using System.Reflection;
using HarmonyLib;
using PeakSafeOptimizer.Diagnostics;
using UnityEngine;

namespace PeakSafeOptimizer.Patches.Stability
{
    /// <summary>
    /// Full replacement for CharacterData.UpdateHasParachute. The Prefix always returns false, so it must
    /// reproduce vanilla's order: scoutmaster early return, the three clearing writes, held item, the hotbar
    /// loop with IsEmpty() before prefab, then the backpack. The three writes land before the character check
    /// on purpose, so a broken character clears the cached parachute instead of latching it.
    ///
    /// Player is compared by reference, not Unity null, because vanilla only reads managed fields on it.
    /// Character.player dereferences PlayerHandler.Instance and view.Owner unguarded, hence the catch.
    /// handlerOwner != character is stricter than vanilla, which resolves the player through the handler.
    /// Bots are excluded from the player-null reason because they have no Photon Player at all.
    /// </summary>
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
            ValidateEngineMembers();

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

        internal static bool Prefix(CharacterData __instance, bool __runOriginal)
        {
            if (!__runOriginal)
                return false;

            AccessTools.FieldRef<CharacterData, Character> characterRef = _character;
            if (characterRef == null)
            {
                RateLimitedDiagnostics.Hit("Parachute.accessors-unavailable");
                return true;
            }

            Character character = characterRef(__instance);

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

            Player player;
            try
            {
                player = character.player;
            }
            catch (Exception)
            {
                RateLimitedDiagnostics.Hit("Parachute.player-lookup-failed");
                return false;
            }
            if ((object)player == null)
            {
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

            AccessTools.FieldRef<CharacterBackpackHandler, Character> handlerCharacterRef = _handlerCharacter;
            if (handlerCharacterRef == null)
            {
                RateLimitedDiagnostics.Hit("Parachute.accessors-unavailable");
                return true;
            }
            Character handlerOwner = handlerCharacterRef(handler);
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
            StabilityPatchValidation.RequireInstanceField(
                typeof(BackpackSlot), "backpackType", typeof(BackpackSlot.BackpackType));

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

        private static void ValidateEngineMembers()
        {
            PropertyInfo itemGameObject = typeof(Item).GetProperty(
                "gameObject", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            MethodInfo itemGameObjectGetter = itemGameObject?.GetGetMethod(true);
            if (itemGameObject == null || itemGameObject.PropertyType != typeof(GameObject) ||
                itemGameObject.GetIndexParameters().Length != 0 ||
                itemGameObjectGetter == null || itemGameObjectGetter.IsStatic)
            {
                throw new InvalidOperationException("Item.gameObject failed structural self-check");
            }

            MethodInfo gameObjectCompareTag = typeof(GameObject).GetMethod(
                "CompareTag",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new[] { typeof(string) },
                null);
            if (gameObjectCompareTag == null || gameObjectCompareTag.IsStatic ||
                gameObjectCompareTag.ReturnType != typeof(bool))
            {
                throw new InvalidOperationException("GameObject.CompareTag failed structural self-check");
            }

            MethodInfo itemCompareTag = typeof(Item).GetMethod(
                "CompareTag",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new[] { typeof(string) },
                null);
            if (itemCompareTag == null || itemCompareTag.IsStatic ||
                itemCompareTag.ReturnType != typeof(bool))
            {
                throw new InvalidOperationException("Item.CompareTag failed structural self-check");
            }
        }
    }
}
