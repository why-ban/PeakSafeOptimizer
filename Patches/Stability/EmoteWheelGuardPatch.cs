using System;
using System.Reflection;
using PeakSafeOptimizer.Diagnostics;

namespace PeakSafeOptimizer.Patches.Stability
{
    /// <summary>
    /// Null guard for GUIManager.UpdateEmoteWheel() (audited source GUIManager.cs:552-567), invoked
    /// every frame from GUIManager.LateUpdate (:377).
    ///
    /// Root cause (confirmed in-game, 2315 occurrences in one multiplayer session): the canEmote
    /// getter and the method body walk the local character graph without null checks. During
    /// spectating, respawn, quit or a partial teardown a link in that graph can be unavailable.
    ///
    /// The original, verbatim:
    ///
    ///   canEmote (:303-313)            if (!localCharacter.data.dead) return !localCharacter.refs.stats.won;
    ///                                  return false;
    ///   UpdateEmoteWheel (:552-567)    if (canEmote &amp;&amp; localCharacter.input.emoteIsPressed
    ///                                          &amp;&amp; !localCharacter.data.isClimbing) { open }
    ///                                  else if (localCharacter.data.usingEmoteWheel) { close }
    ///
    /// Two short circuits decide where an exception is actually possible, and this Prefix has to
    /// mirror both of them:
    ///
    ///   * <c>data.dead == true</c> makes canEmote return false without ever touching
    ///     <c>refs.stats</c>, so a missing refs/stats cannot throw on a dead character.
    ///   * <c>canEmote == false</c> short-circuits the <c>&amp;&amp;</c> chain, so a missing
    ///     <c>input</c> cannot throw when the character is dead or has won.
    ///
    /// In both of those cases the original falls through to the <c>else if</c> and closes an open
    /// emote wheel. Skipping the original there is not a no-op: it suppresses the vanilla writes
    /// <c>emoteWheel.SetActive(false)</c> and <c>data.usingEmoteWheel = false</c>, which latches
    /// <c>usingEmoteWheel</c> - and with it CharacterData.usingWheel (CharacterData.cs:455-465),
    /// GUIManager.wheelActive (:279-289), the pause-menu gate in UpdatePaused (:415-418), the cursor
    /// lock in CursorHandler.cs:13 and the movement gate in CharacterMovement.cs:167 - in the open
    /// state with the wheel UI stuck on screen. So this Prefix skips the original only on the frames
    /// where the original would genuinely dereference a null, and lets it run in every other case
    /// including the closing branch.
    ///
    /// The Prefix itself is read-only; it never writes game state, and it deliberately does not try
    /// to perform the closing branch on its own (<c>emoteWheel</c> and <c>windowBlockingInput</c> are
    /// private GUIManager state, and writing them would race the original's other writers). Letting
    /// the original run is the fix.
    ///
    /// Correctness dependency: computing canEmote here duplicates the vanilla implementation
    /// <c>!data.dead &amp;&amp; !refs.stats.won</c>. If a game update reshapes canEmote, this
    /// short-circuit model no longer matches. What holds that closed is the structural assertion on
    /// the canEmote property below plus the assertions on data.dead and refs.stats.won: a reshaped
    /// canEmote that no longer reads those two members fails one of them and the patch is skipped. A
    /// reshaped canEmote that still reads exactly those two members but combines them differently is
    /// the residual risk, and it is why this patch is classified Fingerprint - i.e. re-read it against
    /// a fresh decompile after a game update.
    ///
    /// The members are reported separately, because which one is missing identifies the cause. All
    /// five diagnostics stay reachable, but the new structure narrows what each one means:
    /// refs-unavailable and stats-unavailable now require a character that is not dead, and
    /// input-unavailable now requires canEmote to have evaluated true, i.e. alive and not won.
    /// LateUpdate already guards localCharacter itself (:353) and dereferences localCharacter.data
    /// two lines before the call (:363), so localCharacter-unavailable and data-unavailable are only
    /// reachable through a third-party direct call; a hit in practice therefore means refs, refs.stats
    /// or input is missing on a live character - the local character is mid-rebuild or another mod has
    /// replaced it. Note this can never be a bot: localCharacter is the local player by definition.
    /// </summary>
    internal static class EmoteWheelGuardPatch
    {
        internal static void Initialize()
        {
            ResolveAndValidateTarget();

            // Hand-rolled because StabilityPatchValidation has no static-field helper. Checks match
            // Patches\Scale\ScaleValidation.RequireStaticField (:105-113): DeclaredOnly in the binding
            // flags plus an explicit DeclaringType test, so a same-named static inherited from a
            // future base class cannot satisfy this. Public-only rather than that helper's
            // Public|NonPublic, and deliberately so: the Prefix reads the field through directly
            // compiled access, which a demoted field would no longer permit. Verified declared on
            // Character itself (Character.cs:117).
            FieldInfo localCharacterField = typeof(Character).GetField(
                "localCharacter",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.DeclaredOnly);
            if (localCharacterField == null || !localCharacterField.IsStatic ||
                localCharacterField.DeclaringType != typeof(Character) ||
                localCharacterField.FieldType != typeof(Character))
            {
                throw new InvalidOperationException("Character.localCharacter failed structural self-check");
            }

            // Members the guard chain reads. Any reshape fails the patch closed instead of letting
            // the guard silently stop covering the audited canEmote dependencies.
            StabilityPatchValidation.RequireInstanceField(
                typeof(Character), "data", typeof(CharacterData));
            StabilityPatchValidation.RequireInstanceField(
                typeof(Character), "refs", typeof(Character.CharacterRefs));
            StabilityPatchValidation.RequireInstanceField(
                typeof(Character.CharacterRefs), "stats", typeof(CharacterStats));
            StabilityPatchValidation.RequireInstanceField(
                typeof(Character), "input", typeof(CharacterInput));

            // The two values this Prefix recomputes to reproduce vanilla canEmote
            // (GUIManager.cs:303-313). dead is a property with a side-effecting setter, but the
            // getter is a plain read of the backing field (CharacterData.cs:320-333).
            StabilityPatchValidation.RequireInstanceProperty(
                typeof(CharacterData), "dead", typeof(bool));
            StabilityPatchValidation.RequireInstanceField(
                typeof(CharacterStats), "won", typeof(bool));
            // canEmote itself: a private bool instance property on GUIManager. Asserted because the
            // whole short-circuit model above is a restatement of its body.
            StabilityPatchValidation.RequireInstanceProperty(
                typeof(GUIManager), "canEmote", typeof(bool), requirePublicGetter: false);

            // Not read by this Prefix - asserted to keep the two claims the design rests on honest.
            // emoteIsPressed is the dereference that the canEmote short circuit protects, and
            // usingEmoteWheel is the state the closing branch clears, which is exactly what must not
            // be suppressed.
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
            // localCharacter uses Unity null semantics - false for a null reference and for a
            // destroyed object - because that is exactly the test LateUpdate:353 applies before it
            // reaches this method, so a destroyed local character never gets here in vanilla either.
            Character local = Character.localCharacter;
            if (!(bool)local)
            {
                // Original: canEmote dereferences Character.localCharacter.data (:307). Only
                // reachable through a third-party direct call, since LateUpdate:353 already gates it.
                RateLimitedDiagnostics.Hit("EmoteWheelGuard.localCharacter-unavailable");
                return false;
            }

            // The remaining links are compared against a plain null reference rather than through the
            // UnityEngine.Object == overload, on purpose. Everything the original reads on them is a
            // managed field - data.dead backed by _dead (CharacterData.cs:320-333), data.isClimbing
            // (:117), data.usingEmoteWheel (:209), stats.won (CharacterStats.cs:96),
            // input.emoteIsPressed (CharacterInput.cs:131) - and a managed field read on a destroyed
            // component still succeeds. Treating "destroyed" as unavailable here would skip frames on
            // which the original does not throw, and with them the closing branch.
            CharacterData data = local.data;
            if ((object)data == null)
            {
                // Original: canEmote reads data.dead (:307).
                RateLimitedDiagnostics.Hit("EmoteWheelGuard.data-unavailable");
                return false;
            }

            bool canEmote;
            if (data.dead)
            {
                // Vanilla short circuit: canEmote returns false at :311 without reading refs.stats,
                // so refs/stats being absent is harmless here. The original then takes the closing
                // branch at :562, which must not be suppressed.
                canEmote = false;
            }
            else
            {
                Character.CharacterRefs refs = local.refs;
                if (refs == null)
                {
                    // Original: canEmote dereferences refs.stats (:309). CharacterRefs is a plain
                    // [Serializable] class, so this is an ordinary reference comparison already.
                    RateLimitedDiagnostics.Hit("EmoteWheelGuard.refs-unavailable");
                    return false;
                }

                CharacterStats stats = refs.stats;
                if ((object)stats == null)
                {
                    // Original: canEmote reads stats.won (:309).
                    RateLimitedDiagnostics.Hit("EmoteWheelGuard.stats-unavailable");
                    return false;
                }

                canEmote = !stats.won;
            }

            if (canEmote && (object)local.input == null)
            {
                // Original: the && chain reaches input.emoteIsPressed (:554) only when canEmote is
                // true. With canEmote false the original never touches input, so a missing input is
                // passed through instead of skipping the closing branch.
                RateLimitedDiagnostics.Hit("EmoteWheelGuard.input-unavailable");
                return false;
            }

            // Everything the original is about to dereference on this frame is present - including
            // every case where canEmote is false, which is where the closing branch lives.
            return true;
        }
    }
}
