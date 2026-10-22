using System;
using System.Collections.Generic;
using System.Reflection;
using PeakSafeOptimizer.Diagnostics;
using PeakSafeOptimizer.Patches.Scale;
using UnityEngine;

namespace PeakSafeOptimizer.Patches.ScaleExperimental
{
    /// <summary>
    /// Same-frame coalescing for CharacterItems.RefreshAllCharacterCarryWeightRPC. A call is skipped only
    /// when an earlier call in the same frame already ran the original with the same folded content
    /// signature, so the skipped one would have been a no-op: UpdateWeight recomputes from scratch and its
    /// SetStatus writes are absolute. The signature is a hash, so a collision drops a refresh.
    /// </summary>
    internal static class WeightRefreshCoalescingPatch
    {
        private static bool _hasRun;
        private static int _lastRefreshFrame;
        private static int _lastSignature;

        internal static void Initialize()
        {
            ResolveAndValidateTarget();

            ScaleValidation.RequireStaticMethod(
                typeof(PlayerHandler), "GetAllPlayerCharacters", typeof(List<Character>),
                Type.EmptyTypes, true);
            ScaleValidation.RequireStaticProperty(typeof(PlayerHandler), "Exists", typeof(bool));
            ScaleValidation.RequireInstanceField(typeof(Character), "refs", typeof(Character.CharacterRefs));
            ScaleValidation.RequireInstanceField(
                typeof(Character.CharacterRefs), "afflictions", typeof(CharacterAfflictions));
            ScaleValidation.RequireInstanceField(
                typeof(CharacterAfflictions), "physicalThorns", typeof(List<ThornOnMe>));
            ScaleValidation.RequireInstanceField(typeof(ThornOnMe), "stuckIn", typeof(bool));
            ScaleValidation.RequireInstanceField(typeof(ThornOnMe), "type", typeof(int));
            ScaleValidation.RequireInstanceMethod(
                typeof(ThornOnMe), "GetThornDamage", typeof(int), Type.EmptyTypes, true);
            ScaleValidation.RequireStaticProperty(typeof(Ascents), "etcDamageMultiplier", typeof(float));

            ScaleValidation.RequireInstanceMethod(
                typeof(CharacterAfflictions), "UpdateWeight", typeof(void), Type.EmptyTypes, false);

            ScaleValidation.RequireInstanceProperty(typeof(Character), "player", typeof(Player));
            ScaleValidation.RequireInstanceField(typeof(Character), "data", typeof(CharacterData));
            ScaleValidation.RequireInstanceField(typeof(Player), "itemSlots", typeof(ItemSlot[]));
            ScaleValidation.RequireInstanceField(typeof(Player), "backpackSlot", typeof(BackpackSlot));
            ScaleValidation.RequireInstanceMethod(
                typeof(Player), "GetItemSlot", typeof(ItemSlot), new[] { typeof(byte) }, true);
            ScaleValidation.RequireInstanceProperty(typeof(ItemSlot), "prefab", typeof(Item));
            ScaleValidation.RequireInstanceProperty(typeof(ItemSlot), "data", typeof(ItemInstanceData));
            ScaleValidation.RequireInstanceField(
                typeof(BackpackSlot), "backpackType", typeof(BackpackSlot.BackpackType));
            ScaleValidation.RequireInstanceMethod(
                typeof(BackpackSlot), "IsEmpty", typeof(bool), Type.EmptyTypes, true);
            ScaleValidation.RequireInstanceField(typeof(BackpackData), "itemSlots", typeof(ItemSlot[]));
            ScaleValidation.RequireInstanceField(typeof(CharacterData), "carriedPlayer", typeof(Character));
            ScaleValidation.RequireInstanceField(
                typeof(CharacterData), "currentStickyItem", typeof(StickyItemComponent));
            ScaleValidation.RequireInstanceProperty(typeof(CharacterData), "isSkeleton", typeof(bool));
            ScaleValidation.RequireStaticField(
                typeof(StickyItemComponent), "ALL_STUCK_ITEMS", typeof(List<StickyItemComponent>));
            ScaleValidation.RequireInstanceProperty(
                typeof(StickyItemComponent), "stuckToCharacter", typeof(Character));
            ScaleValidation.RequireInstanceField(
                typeof(StickyItemComponent), "addWeightToStuckPlayer", typeof(int));
            ScaleValidation.RequireInstanceField(
                typeof(StickyItemComponent), "addThornsToStuckPlayer", typeof(int));
            if (!Enum.IsDefined(typeof(DataEntryKey), DataEntryKey.BackpackData))
                throw new InvalidOperationException("DataEntryKey.BackpackData is no longer defined");

            _hasRun = false;
            _lastRefreshFrame = 0;
            _lastSignature = 0;
        }

        internal static MethodInfo ResolveAndValidateTarget()
        {
            return ScaleValidation.RequireInstanceMethod(
                typeof(CharacterItems), "RefreshAllCharacterCarryWeightRPC",
                typeof(void), Type.EmptyTypes, true);
        }

        internal static void Reset()
        {
            _hasRun = false;
            _lastRefreshFrame = 0;
            _lastSignature = 0;
        }

        internal static bool Prefix()
        {
            // Roster service gone (scene teardown / reload): never coalesce across that boundary.
            if (!PlayerHandler.Exists)
            {
                _hasRun = false;
                _lastRefreshFrame = 0;
                _lastSignature = 0;
                return true;
            }

            List<Character> characters = PlayerHandler.GetAllPlayerCharacters();
            if (characters == null)
            {
                RateLimitedDiagnostics.Hit("WeightRefreshCoalescing.roster-null");
                _hasRun = false;
                _lastRefreshFrame = 0;
                _lastSignature = 0;
                return true;
            }

            int frame = Time.frameCount;
            if (!TryComputeSignature(characters, out int signature))
            {
                _hasRun = false;
                _lastRefreshFrame = 0;
                _lastSignature = 0;
                return true;
            }

            if (!_hasRun ||
                frame != _lastRefreshFrame ||
                signature != _lastSignature)
            {
                _hasRun = true;
                _lastRefreshFrame = frame;
                _lastSignature = signature;
                return true;
            }

            // The designed path, not a repair or a fallback, so no diagnostics here
            return false;
        }

        /// Folds every input UpdateWeight reads into one int. Returns false when the roster or any element is null, so the caller defers
        private static bool TryComputeSignature(List<Character> characters, out int signature)
        {
            signature = 0;
            int hash = 17;
            int count = characters.Count;
            hash = unchecked(hash * 31 + count);
            // GetThornDamage multiplies by this global, so its bits are part of the content
            hash = unchecked(hash * 31 + BitConverter.SingleToInt32Bits(Ascents.etcDamageMultiplier));

            List<StickyItemComponent> stuck = StickyItemComponent.ALL_STUCK_ITEMS;
            if (stuck == null)
                return false;
            hash = unchecked(hash * 31 + stuck.Count);
            for (int i = 0; i < stuck.Count; i++)
            {
                StickyItemComponent sticky = stuck[i];
                if (sticky == null)
                    return false;
                Character target = sticky.stuckToCharacter;
                hash = unchecked(hash * 31 + (target != null ? target.GetInstanceID() : 0));
                hash = unchecked(hash * 31 + sticky.addWeightToStuckPlayer);
                hash = unchecked(hash * 31 + sticky.addThornsToStuckPlayer);
            }

            for (int i = 0; i < count; i++)
            {
                Character character = characters[i];
                if (character == null)
                    return false;
                CharacterData data = character.data;
                Player player = character.player;
                if (data == null || player == null)
                    return false;

                hash = unchecked(hash * 31 + character.GetInstanceID());

                ItemSlot[] itemSlots = player.itemSlots;
                if (itemSlots == null)
                    return false;
                hash = unchecked(hash * 31 + itemSlots.Length);
                for (int s = 0; s < itemSlots.Length; s++)
                {
                    ItemSlot slot = itemSlots[s];
                    if (slot == null)
                        return false;
                    Item prefab = slot.prefab;
                    hash = unchecked(hash * 31 + (prefab != null ? prefab.GetInstanceID() : 0));
                }

                BackpackSlot backpack = player.backpackSlot;
                if (backpack == null)
                    return false;
                hash = unchecked(hash * 31 + (int)backpack.backpackType);
                if (!backpack.IsEmpty())
                {
                    ItemInstanceData backpackInstance = backpack.data;
                    if (backpackInstance == null)
                        return false;
                    if (backpackInstance.TryGetDataEntry<BackpackData>(
                        DataEntryKey.BackpackData, out BackpackData backpackData) &&
                        backpackData != null)
                    {
                        ItemSlot[] innerSlots = backpackData.itemSlots;
                        if (innerSlots == null)
                            return false;
                        hash = unchecked(hash * 31 + innerSlots.Length);
                        for (int s = 0; s < innerSlots.Length; s++)
                        {
                            ItemSlot slot = innerSlots[s];
                            if (slot == null)
                                return false;
                            Item prefab = slot.prefab;
                            hash = unchecked(hash * 31 + (prefab != null ? prefab.GetInstanceID() : 0));
                        }
                    }
                }

                ItemSlot tempSlot = player.GetItemSlot(250);
                if (tempSlot == null)
                    return false;
                Item tempPrefab = tempSlot.prefab;
                hash = unchecked(hash * 31 + (tempPrefab != null ? tempPrefab.GetInstanceID() : 0));

                Character carried = data.carriedPlayer;
                hash = unchecked(hash * 31 + (carried != null ? carried.GetInstanceID() : 0));

                StickyItemComponent currentSticky = data.currentStickyItem;
                hash = unchecked(hash * 31 + (currentSticky != null ? 1 : 0));
                if (currentSticky != null)
                {
                    hash = unchecked(hash * 31 + currentSticky.GetInstanceID());
                    hash = unchecked(hash * 31 + currentSticky.addThornsToStuckPlayer);
                }

                hash = unchecked(hash * 31 + (data.isSkeleton ? 1 : 0));

                CharacterAfflictions afflictions = character.refs != null ? character.refs.afflictions : null;
                if (afflictions == null || afflictions.physicalThorns == null)
                    return false;
                List<ThornOnMe> thorns = afflictions.physicalThorns;
                hash = unchecked(hash * 31 + thorns.Count);
                for (int t = 0; t < thorns.Count; t++)
                {
                    ThornOnMe thorn = thorns[t];
                    if (thorn == null)
                        return false;
                    hash = unchecked(hash * 31 + (thorn.stuckIn ? 1 : 0));
                    hash = unchecked(hash * 31 + thorn.type);
                    hash = unchecked(hash * 31 + thorn.GetThornDamage());
                }
            }

            signature = hash;
            return true;
        }
    }
}
