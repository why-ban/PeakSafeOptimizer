using System;
using System.Reflection;
using HarmonyLib;
using PeakSafeOptimizer.Diagnostics;
using UnityEngine;

namespace PeakSafeOptimizer.Patches.Stability
{
    /// <summary>
    /// Full replacement for CharacterData.UpdateHasParachute() (audited source
    /// CharacterData.cs:708-738), called once per frame per locally owned character from
    /// Character.Update (Character.cs:521, after the <c>!photonView.IsMine</c> early return at :516).
    /// This is the only Stability patch that replaces its target outright - the Prefix always returns
    /// false - and the only one that writes game state, so every branch is spelled out below against
    /// the line it stands in for.
    ///
    /// The original, and what it dereferences unguarded:
    ///
    ///   :710  <c>character.isScoutmaster</c> - a managed bool field (Character.cs:115) read with no
    ///         null check on <c>character</c>; returns early for the scoutmaster without touching
    ///         cached state.
    ///   :714-716  three unconditional writes: hasParachute = false, currentParachuteSlot = null,
    ///         currentParachuteItem = null.
    ///   :717  <c>character.data.currentItem</c> - note this goes through <c>character.data</c>, not
    ///         through <c>this</c>. The indirection is preserved here deliberately: the two can be
    ///         different instances, and reproducing vanilla means reading the same one vanilla reads.
    ///         Then <c>.gameObject.CompareTag("Parachute")</c>.
    ///   :723  <c>character.player.itemSlots</c> - Character.player is a property that resolves
    ///         through PlayerHandler.GetPlayer(view.Owner) (Character.cs:201,
    ///         PlayerHandler.cs:206-209); both <c>Instance</c> and <c>view.Owner</c> are unguarded.
    ///   :726  <c>itemSlot.IsEmpty()</c>, which for a plain ItemSlot is exactly
    ///         <c>prefab == null</c> (ItemSlot.cs:42-45), followed by <c>itemSlot.prefab.CompareTag</c>.
    ///   :733  <c>character.refs.backpackHandler.activeBackpackVisuals</c> - three links with no
    ///         check, and the getter itself walks <c>character.player.backpackSlot.backpackType</c>
    ///         with no check either (CharacterBackpackHandler.cs:37-58).
    ///
    /// Branch map of the replacement: the scoutmaster return is reproduced first, then the three
    /// writes, then the held-item branch (:717-721), then the hotbar loop in the original's
    /// IsEmpty-before-prefab order (:723-732), then the backpack branch (:733-737). Every write and
    /// every early return lands where the original's does.
    ///
    /// One deliberate behavioural difference, in the safe direction. When <c>character</c> is
    /// unavailable the original raises at :710 and <c>hasParachute</c> keeps its previous value; this
    /// patch performs the three clearing writes first and only then reports and returns. So the claim
    /// is NOT "identical except for not throwing": on a broken character this patch actively clears
    /// the cached parachute state where vanilla latches it. That is the improvement being bought -
    /// a latched stale <c>hasParachute</c> lets the player deploy a parachute that no longer exists -
    /// and it is why the writes are ordered before the character check rather than after.
    ///
    /// Third-party suppression is honoured: HarmonyX runs every Prefix and combines the verdicts
    /// afterwards, so priority alone cannot exclude. With <c>__runOriginal</c> false another mod has
    /// already decided the method must not run, and since this Prefix *is* the method, it performs no
    /// writes at all in that case and returns false to leave the verdict unchanged.
    ///
    /// Reachability of the fourteen diagnostic reasons, so nobody reads their counters as evidence
    /// they are:
    ///   * <c>player-null-or-destroyed</c> is the only one reachable in normal play, and only for
    ///     characters that are not bots - bots have no Photon Player at all, so the check is gated on
    ///     <c>!character.isBot</c> (Character.cs:111) after 1899 hits in one session turned out to be
    ///     entirely bots.
    ///   * <c>held-gameObject-null-or-destroyed</c>, <c>nonempty-prefab-null-or-destroyed</c> and
    ///     <c>prefab-gameObject-null-or-destroyed</c> are logically unreachable: a component that
    ///     passed a Unity liveness test cannot have a null <c>gameObject</c>, and <c>!IsEmpty()</c>
    ///     already implies a non-null prefab (ItemSlot.cs:42-45). The code stays as zero-cost
    ///     belt-and-braces, but a hit on any of them means a game update changed one of those two
    ///     facts, not that the guard is earning its keep.
    ///   * <c>accessors-unavailable</c> (both sites) is a fallback, not a repair: it means Reset() ran
    ///     while the patch was still attached.
    ///   * <c>player-lookup-failed</c>, characterData, itemSlots, slot, characterRefs,
    ///     backpackHandler, backpackHandler-owner and backpackSlot all describe a graph the original
    ///     could not survive either.
    ///
    /// One place where this patch is stricter than the original: <c>handlerOwner != character</c>
    /// (see the comment at the call site). The original's activeBackpackVisuals getter resolves the
    /// player through the handler's own <c>character</c> field and does not require the two to be the
    /// same instance. Kept as-is: divergence is limited to a frame in which a handler is attached to
    /// a different character than the data it is being asked about.
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

        // Full replacement for the short, MVID-gated method in CharacterData.cs:708-738.
        internal static bool Prefix(CharacterData __instance, bool __runOriginal)
        {
            // HarmonyX runs every Prefix and combines the verdicts afterwards, so a false
            // __runOriginal means another Prefix has already suppressed the method. This Prefix is
            // the method, so honouring that means performing none of its writes.
            if (!__runOriginal)
                return false;

            AccessTools.FieldRef<CharacterData, Character> characterRef = _character;
            if (characterRef == null)
            {
                // Reset() ran while the Harmony patch is still attached. Nothing has been written
                // yet, so handing the frame back to the original is exactly vanilla behaviour.
                RateLimitedDiagnostics.Hit("Parachute.accessors-unavailable");
                return true;
            }

            Character character = characterRef(__instance);

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

            // Character.player is a property, not a field: PlayerHandler.GetPlayer(view.Owner)
            // (Character.cs:201) dereferences PlayerHandler.Instance and photonPlayer.ActorNumber
            // (PlayerHandler.cs:206-209), either of which can raise. The original raises in the same
            // place, but the three clearing writes above have already landed on this path, so the
            // frame is reported and abandoned rather than handed back: returning true would let the
            // original repeat the writes and then raise the very exception this replaces.
            // This is not the bot case: the measured 1899 bot frames all took the null branch below,
            // i.e. the lookup completed and simply found no Player, so this catch is not a source of
            // bot noise.
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
            // Player derives from MonoBehaviourPunCallbacks, i.e. from UnityEngine.Object
            // (Player.cs:11), but everything the original reads on it is a managed field -
            // itemSlots (Player.cs:15) at CharacterData.cs:723 and backpackSlot (Player.cs:19) inside
            // the activeBackpackVisuals getter - and a managed field read on a destroyed wrapper
            // still succeeds. Hence a plain reference comparison: treating "destroyed" as
            // unavailable here would abandon frames on which the original does not raise.
            if ((object)player == null)
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

            // activeBackpackVisuals dereferences handler.character.player.backpackSlot
            // (CharacterBackpackHandler.cs:41-57). handlerOwner == character below plus the already
            // completed character.player lookup above mean the getter repeats the exact same lookup
            // on the same actor within the same frame, so it cannot raise where that one did not;
            // validate all other getter prerequisites before invoking it.
            AccessTools.FieldRef<CharacterBackpackHandler, Character> handlerCharacterRef = _handlerCharacter;
            if (handlerCharacterRef == null)
            {
                // Reset() ran mid-flight. Unlike the accessor check at the top of this method, the
                // three clearing writes have already landed here - but UpdateHasParachute is an
                // idempotent full recompute of exactly those three fields, and the values written so
                // far are the same ones the original writes at CharacterData.cs:714-716 on its way
                // through. So handing the frame to the original re-derives the vanilla answer
                // including the backpack branch, which is the one branch this patch can no longer
                // evaluate without the handler's character field. The accepted cost is that the
                // original may then raise at :733 on a broken graph - which is vanilla behaviour,
                // during a window in which this patch has lost the accessors it would need to stand
                // in for it.
                RateLimitedDiagnostics.Hit("Parachute.accessors-unavailable");
                return true;
            }
            Character handlerOwner = handlerCharacterRef(handler);
            if (handlerOwner == null || handlerOwner != character)
            {
                RateLimitedDiagnostics.Hit("Parachute.backpackHandler-owner-invalid");
                return false;
            }
            // BackpackSlot is a plain [Serializable] class (BackpackSlot.cs:4), not a
            // UnityEngine.Object, so this is an ordinary reference comparison already.
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
            // The last link the activeBackpackVisuals getter actually dereferences:
            // character.player.backpackSlot.backpackType (CharacterBackpackHandler.cs:41-57).
            // Declared on BackpackSlot (BackpackSlot.cs:15), so the declared-only helper applies.
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

        /// <summary>
        /// The three engine members the tag comparisons rely on. All are inherited - Item.gameObject
        /// and Item.CompareTag come from UnityEngine.Component, and GameObject.CompareTag is reached
        /// on an instance the patch resolves itself - so StabilityPatchValidation's declared-only
        /// lookups cannot express them and they are checked by hand here, in the same style as
        /// BinocularSunGuardPatch's Bodypart.transform check. DeclaredOnly is deliberately absent for
        /// the two inherited members.
        /// </summary>
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
