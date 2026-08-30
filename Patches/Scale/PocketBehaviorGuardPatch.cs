using System;
using System.Reflection;
using HarmonyLib;
using PeakSafeOptimizer.Diagnostics;

namespace PeakSafeOptimizer.Patches.Scale
{
    /// <summary>
    /// Stability guard for CharacterItems.UpdatePocketBehaviors() (audited source
    /// CharacterItems.cs:1041-1051), called every frame per character from CharacterItems.Update
    /// (CharacterItems.cs:181-195).
    ///
    /// The original dereferences character.player.itemSlots without a guard, and
    /// Character.player resolves through PlayerHandler.GetPlayer(view.Owner) (Character.cs:201),
    /// which returns null while a character is registered but its Player entry is not linked yet.
    /// CharacterItems.Start itself waits for that with `while (!character.player)`
    /// (CharacterItems.cs:116), proving the window exists. In-game log evidence: 14
    /// NullReferenceException in UpdatePocketBehaviors during the join/registration window
    /// (LogOutput.log around the "Registering Character object" lines).
    ///
    /// This Prefix is read-only. It skips that one frame only when the data the original
    /// immediately dereferences is unavailable, and otherwise returns true so the original runs
    /// unchanged. Once player/itemSlots exist, behavior is byte-for-byte the original.
    /// </summary>
    internal static class PocketBehaviorGuardPatch
    {
        private static AccessTools.FieldRef<CharacterItems, Character> _character;

        internal static void Initialize()
        {
            ResolveAndValidateTarget();

            FieldInfo characterField = ScaleValidation.RequireInstanceField(
                typeof(CharacterItems), "character", typeof(Character));
            ScaleValidation.RequireInstanceProperty(typeof(Character), "player", typeof(Player));
            // Read by the bot exclusion in Prefix below (Character.cs:111).
            ScaleValidation.RequireInstanceField(typeof(Character), "isBot", typeof(bool));
            ScaleValidation.RequireInstanceField(typeof(Player), "itemSlots", typeof(ItemSlot[]));
            ScaleValidation.RequireInstanceMethod(
                typeof(ItemSlot), "IsEmpty", typeof(bool), Type.EmptyTypes, true);
            ScaleValidation.RequireInstanceMethod(
                typeof(ItemSlot), "UpdatePocketBehaviors", typeof(void), new[] { typeof(bool) }, true);

            AccessTools.FieldRef<CharacterItems, Character> character =
                AccessTools.FieldRefAccess<CharacterItems, Character>(characterField);
            if (character == null)
                throw new InvalidOperationException("PocketBehaviorGuard FieldRef creation failed");

            _character = character;
        }

        internal static MethodInfo ResolveAndValidateTarget()
        {
            return ScaleValidation.RequireInstanceMethod(
                typeof(CharacterItems), "UpdatePocketBehaviors", typeof(void), Type.EmptyTypes, false);
        }

        internal static void Reset()
        {
            _character = null;
        }

        internal static bool Prefix(CharacterItems __instance)
        {
            AccessTools.FieldRef<CharacterItems, Character> characterRef = _character;
            if (characterRef == null)
            {
                RateLimitedDiagnostics.Hit("PocketBehaviorGuard.accessor-unavailable");
                return true;
            }

            Character owner = characterRef(__instance);
            if (owner == null)
            {
                // CharacterItems.character is assigned in Awake (CharacterItems.cs:144), before any
                // Update can run, so this branch is unreachable in normal operation and a Hit here
                // really would indicate a fault.
                RateLimitedDiagnostics.Hit("PocketBehaviorGuard.character-unavailable");
                return false;
            }

            // Character.player is a property over PlayerHandler.GetPlayer; a plain reference check
            // matches what the original would dereference.
            Player player = owner.player;
            if (player == null)
            {
                // Bots have no Photon Player, so Character.player - a PlayerHandler lookup keyed by
                // view.Owner.ActorNumber (Character.cs:201, PlayerHandler.cs:206-209) - is null for
                // them by design. CharacterItems.Update gates only on isStruggling
                // (CharacterItems.cs:181-195), with no IsLocal/IsMine check, so without this
                // exclusion every bot would produce one Hit per frame and swamp the counter that is
                // read as evidence of a vanilla fault. Same reasoning and same fix as
                // Patches\Stability\ParachutePatch.cs:96-102. Skipping the call itself is still
                // correct for bots: the original would throw on character.player.itemSlots.
                if (!owner.isBot)
                    RateLimitedDiagnostics.Hit("PocketBehaviorGuard.player-unavailable");
                return false;
            }

            if (player.itemSlots == null)
            {
                // Player.itemSlots has a field initializer, `new ItemSlot[3]` (Player.cs:15), and
                // Player.Awake fills every element (Player.cs:40-43), so a live Player can never
                // expose a null array in normal operation.
                RateLimitedDiagnostics.Hit("PocketBehaviorGuard.itemSlots-unavailable");
                return false;
            }

            return true;
        }
    }
}
