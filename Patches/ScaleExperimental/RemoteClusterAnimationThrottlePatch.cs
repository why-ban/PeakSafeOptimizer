using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using PeakSafeOptimizer.Diagnostics;
using PeakSafeOptimizer.Patches.Scale;
using UnityEngine;
using UnityEngine.Animations.Rigging;
using UnityEngine.Playables;

namespace PeakSafeOptimizer.Patches.ScaleExperimental
{
    /// <summary>
    /// Opt-in: evaluates the animation PlayableGraph of a remote, fully controlled, non-interacting
    /// player character once every N physics ticks instead of every tick.
    ///
    /// Audited original, CharacterRagdoll.FixedUpdate (CharacterRagdoll.cs:151-177):
    ///   if (character.IsLocal) { SetPhysicsMats(); }
    ///   if (firstFrame) { firstFrame = false; return; }
    ///   RotateCharacter();
    ///   refs.ikRigBuilder.SyncLayers();
    ///   refs.ikRigBuilder.Evaluate(Time.fixedDeltaTime);
    ///   refs.animator.playableGraph.Evaluate(Time.fixedDeltaTime);
    ///   refs.animations.ConfigureIK();
    ///   for (i) partList[i].SaveAnimationData();
    ///   SaveAdditionalTransformPositions();
    ///   ResetRotation();
    ///   for (j) partList[j].ResetTransform();
    ///
    /// This is the single most expensive managed per-character physics-step cost in the game, and
    /// unlike <see cref="RemoteRagdollLod"/> this patch has no distance term, so it is the lane that
    /// still helps when the whole lobby stands in one spot.
    ///
    /// Why a skipped tick is coherent rather than broken:
    ///   * The graph is in DirectorUpdateMode.Manual (CharacterRagdoll.cs:59-60), so this call is the
    ///     only thing that advances animation time. Skipping ticks would therefore slow the animation
    ///     down - which is why the execute tick is handed the accumulated delta time of every tick
    ///     since the previous evaluation. Inside a continuous throttled stretch the animation clock is
    ///     exact and only its temporal resolution drops to 50/N Hz. Crossing *out* of the throttle is
    ///     the one exception: the accumulator has to be dropped there because the original advances by
    ///     a full fixedDeltaTime of its own, which costs up to (N-1) * fixedDeltaTime of animation time
    ///     per crossing. See <see cref="Yield"/> for why that is accepted rather than fixed.
    ///   * RotateCharacter/ResetRotation cancel each other out (CharacterRagdoll.cs:196-205): the
    ///     first sets refs.rigCreator.transform.rotation, the second restores the value captured in
    ///     the same call. They exist so the graph evaluates against a look-rotated root, so on a tick
    ///     with no evaluation they are a no-op pair and are skipped as one unit.
    ///   * ResetTransform (Bodypart.cs:138-141) writes the bodypart transform back from its
    ///     Rigidbody, undoing what the graph evaluation did to it. Without an evaluation the
    ///     transform was never overwritten, and PhysX keeps driving it directly, so the rendered pose
    ///     still follows physics on every tick - nothing freezes.
    ///   * SaveAnimationData (Bodypart.cs:126-136) reads the *animated* pose to produce the targets
    ///     Animate drives towards. On a skipped tick the transform holds the physics pose, so calling
    ///     it would tell every part "you are already where you should be" and the character would go
    ///     limp. It is therefore deliberately not called; the previous targets stay in place and
    ///     Animate keeps pulling towards them, which is what makes the pose step instead of collapse.
    ///   * SaveAdditionalTransformPositions is skipped for the same reason: it too reads the animated
    ///     pose (CharacterRagdoll.cs:207-238). Its outputs - data.targetHeadHeight,
    ///     data.targetHipHeight and the animation helper transforms - therefore keep the value from
    ///     the last evaluation, at most N-1 ticks old, instead of being recomputed from a physics
    ///     pose they were never meant to describe. Recomputing them there would assign the current
    ///     sagging height to the value Stand() treats as its target (CharacterMovement.cs:709-718),
    ///     which drives the standing force to zero and makes the sag self-reinforcing.
    ///   * SetPhysicsMats is *not* called, on any tick, and that is what keeps this patch equivalent
    ///     rather than what breaks it. The call is gated on `if (character.IsLocal)`
    ///     (CharacterRagdoll.cs:153-156; IsLocal is `this == localCharacter`, Character.cs:235), and
    ///     the eligibility test <see cref="RemoteRagdollLod.ShouldThrottleAnimation"/> rejects the
    ///     local character outright - `c == local` is the first entry of the shared fallback list
    ///     (RemoteRagdollLod.cs:717). So every character that reaches the replacement is one for which
    ///     vanilla performs zero SetPhysicsMats calls per physics step, and a replacement that called
    ///     it would be *adding* per-step native col.sharedMaterial writes that the game no longer
    ///     performs. Earlier versions of this patch did call it - once per tick, evaluating or not -
    ///     because in the previously audited build the call was unconditional; that is now a
    ///     behavioural deviation and the calls, the accessor and the delegate behind them are gone.
    ///
    /// Nothing here applies a force, moves a Rigidbody, touches collider or kinematic state, or goes
    /// near CharacterSyncer or any RPC. The eligibility test is
    /// <see cref="RemoteRagdollLod.ShouldThrottleAnimation"/>, i.e. the audited fallback list with the
    /// distance term removed, so the local character, the spectated character, bots, zombies, the
    /// scoutmaster, and anyone climbing, grabbing, carrying, warping, dead, passed out, kinematic,
    /// partially ragdolled or inside the item-equip window keeps vanilla behaviour on every tick. A
    /// crouch or sprint transition also yields for a tick, so the two stand heights described above are
    /// refreshed at full rate exactly when they change.
    /// </summary>
    internal static class RemoteClusterAnimationThrottlePatch
    {
        /// <summary>
        /// The animation graph is evaluated once every this many physics ticks, i.e. at 50/Interval Hz.
        /// 2 halves the work while still stepping at 25Hz; beyond 2 the stepping stops reading as a low
        /// frame rate animation and starts reading as a broken one, which is not a trade this patch
        /// offers.
        /// </summary>
        private const int Interval = 2;
        /// <summary>
        /// Process-wide backstop for the per-instance failure latch below. A malformed character (a
        /// custom model without a rigCreator, say) is expected to fail on every tick, and its own
        /// latch already handles that in one catch. This counter exists only for the case where the
        /// failure is systemic rather than per character - a game update that changes the call
        /// sequence, for example - where every character in the lobby would otherwise pay one
        /// exception and one catch.
        ///
        /// 8 is not derived from a player cap, and the inference it looks like it is making does not
        /// hold. PEAK's default lobby size is 4 (Peak.Network.NetworkingUtilities.MAX_PLAYERS is a
        /// `{ get; set; } = 4`, NetworkingUtilities.cs:21) but SetMaxPlayers clamps to [1, 256] and is
        /// a [ConsoleCommand] (NetworkingUtilities.cs:39-43, registered in
        /// Zorro.Core.CLI/ConsoleCommands.cs:81), so a lobby can legitimately be far larger than the
        /// count. In such a lobby eight *independent* per-character failures no longer imply that the
        /// cause is systemic: a batch of malformed custom models would trip the backstop and switch
        /// the whole lane back to vanilla for everyone, including the well-formed characters.
        ///
        /// That misfire is in the safe direction - it gives up the optimisation, it does not corrupt
        /// anything - which is why the value stands. What it costs is the optimisation in a large lobby
        /// full of bad rigs, and the fix would be a rate rather than a count. Failures are counted for
        /// the lifetime of the installation, not per tick, because each character can only contribute
        /// one: after its own latch is set it never enters the replacement again.
        /// </summary>
        private const int MaxTotalFailures = 8;

        private sealed class State
        {
            internal int LastTick = int.MinValue;
            /// <summary>Delta time of every tick since the last evaluation, including this one.</summary>
            internal float PendingDeltaTime;
            /// <summary>
            /// Set once the replacement has failed for *this* CharacterRagdoll. From then on this one
            /// character defers to the untouched original forever, while every other character keeps
            /// being throttled. Per instance rather than per process because the most likely causes
            /// are per character - a malformed rig, or a transient like rigCreator.transform being
            /// destroyed during a scene change - and letting one such character disable the lane for
            /// everybody was silently giving up the whole optimisation.
            /// </summary>
            internal bool DisabledAfterFailure;
        }

        private static AccessTools.FieldRef<CharacterRagdoll, Character> _character;
        private static AccessTools.FieldRef<CharacterRagdoll, bool> _firstFrame;
        private static AccessTools.FieldRef<CharacterRagdoll, List<Bodypart>> _partList;
        private static Action<CharacterRagdoll> _rotateCharacter;
        private static Action<CharacterRagdoll> _resetRotation;
        private static Action<CharacterRagdoll> _saveAdditionalTransformPositions;
        private static ConditionalWeakTable<CharacterRagdoll, State> _states;
        private static bool _acquired;
        /// <summary>
        /// Number of distinct characters whose replacement has failed since installation. Each
        /// character can contribute at most one, because its own <see cref="State.DisabledAfterFailure"/>
        /// keeps it out of the replacement afterwards. Once this reaches
        /// <see cref="MaxTotalFailures"/> the whole lane defers to the original for every character:
        /// that many independent failures means the cause is the call sequence itself, not one bad
        /// rig.
        /// </summary>
        private static int _failureCount;
        /// <summary>
        /// Set when <see cref="_failureCount"/> reaches <see cref="MaxTotalFailures"/>. From then on
        /// every call defers to the untouched original, so a systemic failure degrades to vanilla
        /// behaviour instead of repeating an exception, a catch and a diagnostics hit for every
        /// character on every physics step.
        /// </summary>
        private static bool _disabledAfterFailure;

        internal static void Initialize()
        {
            ResolveAndValidateTarget();
            ValidateStructure();

            FieldInfo characterField = ScaleValidation.RequireInstanceField(
                typeof(CharacterRagdoll), "character", typeof(Character));
            FieldInfo firstFrameField = ScaleValidation.RequireInstanceField(
                typeof(CharacterRagdoll), "firstFrame", typeof(bool));
            FieldInfo partListField = ScaleValidation.RequireInstanceField(
                typeof(CharacterRagdoll), "partList", typeof(List<Bodypart>));
            // Asserted but deliberately *not* bound to a delegate any more: the replacement no longer
            // calls it, because the original's only call site is gated on character.IsLocal and a
            // throttled character is never the local one. The assertion stays so that a build which
            // un-gates the call fails this patch closed instead of silently dropping a per-step
            // material re-assert that vanilla had started performing again.
            ScaleValidation.RequireInstanceMethod(
                typeof(CharacterRagdoll), "SetPhysicsMats", typeof(void), Type.EmptyTypes, false);
            ScaleValidation.RequireInstanceProperty(typeof(Character), "IsLocal", typeof(bool));
            MethodInfo rotateCharacter = ScaleValidation.RequireInstanceMethod(
                typeof(CharacterRagdoll), "RotateCharacter", typeof(void), Type.EmptyTypes, false);
            MethodInfo resetRotation = ScaleValidation.RequireInstanceMethod(
                typeof(CharacterRagdoll), "ResetRotation", typeof(void), Type.EmptyTypes, false);
            MethodInfo saveAdditional = ScaleValidation.RequireInstanceMethod(
                typeof(CharacterRagdoll), "SaveAdditionalTransformPositions", typeof(void),
                Type.EmptyTypes, false);

            AccessTools.FieldRef<CharacterRagdoll, Character> character =
                AccessTools.FieldRefAccess<CharacterRagdoll, Character>(characterField);
            AccessTools.FieldRef<CharacterRagdoll, bool> firstFrame =
                AccessTools.FieldRefAccess<CharacterRagdoll, bool>(firstFrameField);
            AccessTools.FieldRef<CharacterRagdoll, List<Bodypart>> partList =
                AccessTools.FieldRefAccess<CharacterRagdoll, List<Bodypart>>(partListField);
            Action<CharacterRagdoll> rotate =
                ScaleExperimentalEmit.CreateDelegate<Action<CharacterRagdoll>>(rotateCharacter);
            Action<CharacterRagdoll> reset =
                ScaleExperimentalEmit.CreateDelegate<Action<CharacterRagdoll>>(resetRotation);
            Action<CharacterRagdoll> saveAdditionalPositions =
                ScaleExperimentalEmit.CreateDelegate<Action<CharacterRagdoll>>(saveAdditional);
            if (character == null || firstFrame == null || partList == null)
                throw new InvalidOperationException("RemoteClusterAnimationThrottle FieldRef creation failed");
            if (rotate == null || reset == null || saveAdditionalPositions == null)
                throw new InvalidOperationException("RemoteClusterAnimationThrottle delegate creation failed");

            // Allocated into a local, before Acquire. Two separate reasons:
            //   * `new` can raise OutOfMemoryException, so it is a throwing statement and has to sit
            //     on the near side of the shared helper's reference count. An earlier version had it
            //     after Acquire under a comment claiming nothing below Acquire could throw, which was
            //     wrong; it only happened to be harmless because this patch registers a Reset and that
            //     Reset releases whenever _acquired is set.
            //   * A local rather than a direct write to _states, so a failed Acquire cannot leave a
            //     published table behind for a Prefix that is about to be installed - the same shape
            //     DetailBodypartThrottlePatch.Initialize uses.
            var states = new ConditionalWeakTable<CharacterRagdoll, State>();

            // Acquired last: it is genuinely the final statement here that can throw (it runs
            // RemoteRagdollLod.ValidateStructure and builds the shared accessors). Everything below is
            // a plain field assignment.
            RemoteRagdollLod.Acquire();
            _acquired = true;
            _states = states;
            _character = character;
            _firstFrame = firstFrame;
            _partList = partList;
            _rotateCharacter = rotate;
            _resetRotation = reset;
            _saveAdditionalTransformPositions = saveAdditionalPositions;
        }

        internal static MethodInfo ResolveAndValidateTarget()
        {
            return ScaleValidation.RequireInstanceMethod(
                typeof(CharacterRagdoll), "FixedUpdate", typeof(void), Type.EmptyTypes, requirePublic: true);
        }

        internal static void Reset()
        {
            _character = null;
            _firstFrame = null;
            _partList = null;
            _rotateCharacter = null;
            _resetRotation = null;
            _saveAdditionalTransformPositions = null;
            _states = null;
            // Both halves of the failure latch are cleared: the process-wide backstop here, and every
            // per-instance flag together with the table that held them on the line above.
            _disabledAfterFailure = false;
            _failureCount = 0;
            if (_acquired)
            {
                _acquired = false;
                RemoteRagdollLod.Release();
            }
        }

        /// <summary>
        /// Shares its target with <see cref="RemoteRagdollLodPhysicsPatch"/>, which already replaces
        /// this method outright for a distant character. HarmonyX calls every Prefix and only combines
        /// their verdicts afterwards, so <paramref name="__runOriginal"/> is what makes the two lanes
        /// exclusive: when the distance lane has already handled this tick, this Prefix does nothing at
        /// all. The low priority keeps the ordering deterministic, it is not what provides the
        /// exclusion.
        ///
        /// With both switches on the two lanes now agree trivially instead of interacting: neither of
        /// them calls SetPhysicsMats, because the original does not either for any character either
        /// lane can act on (the call is gated on character.IsLocal, CharacterRagdoll.cs:153-156, and
        /// both eligibility tests reject the local character). The note that used to live here - about
        /// this Prefix's per-tick SetPhysicsMats being suppressed by the distance lane's earlier
        /// verdict - described a call that no longer exists.
        /// </summary>
        [HarmonyPriority(Priority.Low)]
        internal static bool Prefix(CharacterRagdoll __instance, bool __runOriginal)
        {
            if (!__runOriginal)
                return false;

            // Enough independent characters have failed that the cause cannot be one bad rig; the
            // whole lane defers to the original for the rest of the session.
            if (_disabledAfterFailure)
                return true;

            AccessTools.FieldRef<CharacterRagdoll, Character> characterRef = _character;
            AccessTools.FieldRef<CharacterRagdoll, bool> firstFrameRef = _firstFrame;
            AccessTools.FieldRef<CharacterRagdoll, List<Bodypart>> partListRef = _partList;
            ConditionalWeakTable<CharacterRagdoll, State> states = _states;
            if (characterRef == null || firstFrameRef == null || partListRef == null ||
                states == null || __instance == null)
            {
                return true;
            }

            // The original's own first-frame branch also initialises firstFrame; leave it alone.
            if (firstFrameRef(__instance))
                return Yield(states, __instance);

            Character owner = characterRef(__instance);
            if (owner == null)
                return Yield(states, __instance);
            Character.CharacterRefs refs = owner.refs;
            if (refs == null || refs.animator == null || refs.animations == null || refs.rigCreator == null)
                return Yield(states, __instance);
            List<Bodypart> parts = partListRef(__instance);
            if (parts == null)
                return Yield(states, __instance);
            if (!RemoteRagdollLod.ShouldThrottleAnimation(owner))
                return Yield(states, __instance);

            float deltaTime = Time.fixedDeltaTime;
            if (!(deltaTime > 0f) || float.IsInfinity(deltaTime))
                return Yield(states, __instance);

            int tick = RemoteRagdollLod.PhysicsTick();
            State state = states.GetOrCreateValue(__instance);
            // This one character's replacement has already failed once; it alone stays on the
            // original. Checked after the state lookup because that is where the flag lives, and
            // before any bookkeeping so a latched character behaves exactly like an unpatched one.
            if (state.DisabledAfterFailure)
                return YieldState(state);
            // A manual FixedUpdate() call from the equip coroutine lands on the same tick as the
            // Unity one (CharacterItems.cs:607-650, the manual pairs at :614-615 and :627-628). Those
            // are excluded by the fallback list, but if
            // one ever reaches here it must not consume a second slice of delta time - and the
            // original it is handed to advances the animation by a full fixedDeltaTime of its own, so
            // the accumulator has to be dropped rather than carried into the next tick.
            if (state.LastTick == tick)
                return Yield(states, __instance);
            // A gap means the untouched original ran in between and already advanced the animation by
            // its own delta time, so anything accumulated before the gap has been accounted for.
            if (state.LastTick != tick - 1)
                state.PendingDeltaTime = 0f;
            state.LastTick = tick;
            // Clamped so a long stall cannot hand the graph one huge step.
            state.PendingDeltaTime = Mathf.Min(state.PendingDeltaTime + deltaTime, deltaTime * Interval);

            // Per-instance phase so remote characters do not all evaluate on the same tick, which
            // would trade a flat cost for a periodic spike. The id is mixed before the modulo
            // (RemoteRagdollLod.Phase): with Interval == 2 a raw `id % 2` is just the low bit of the
            // instance id, and Unity hands out ids in a dense run to objects created in the same
            // batch, so the characters of one lobby very often share a parity - every one of them
            // would then evaluate on the same tick and the spike this phase exists to prevent would be
            // exactly what it produced. The mix is a pure function of the id, so the phase is still
            // stable for the instance's lifetime, allocation free, and safe for int.MinValue.
            int phase = RemoteRagdollLod.Phase(__instance.GetInstanceID(), Interval);
            bool evaluate = (tick + phase) % Interval == 0;
            if (!evaluate)
            {
                // A non-evaluating tick now has literally nothing to do. The original body is
                // `if (character.IsLocal) SetPhysicsMats();` followed by the firstFrame branch and the
                // animation work, and every character that reaches this point is remote by construction
                // - `c == local` is the first entry of RemoteRagdollLod's fallback list, and
                // Character.IsLocal is the same `this == localCharacter` compare - so the gated call is
                // not one vanilla would have made. Earlier versions called SetPhysicsMats here to mirror
                // an unconditional original; doing so now would add per-step native col.sharedMaterial
                // writes the game no longer performs. Nothing on this path can throw any more, so it
                // also needs no failure latch.
                return false;
            }

            float accumulated = state.PendingDeltaTime;
            state.PendingDeltaTime = 0f;
            if (RunReplacement(__instance, refs, parts, accumulated))
                return false;

            // The replacement failed. It has already restored the rig rotation it changed, so this
            // tick stays skipped - handing it to the original as well would run RotateCharacter a
            // second time on the same tick. Every later tick for *this* character goes back to the
            // original; other characters are unaffected unless the failure count shows the cause is
            // systemic.
            state.DisabledAfterFailure = true;
            state.PendingDeltaTime = 0f;
            state.LastTick = int.MinValue;
            _failureCount++;
            if (_failureCount >= MaxTotalFailures && !_disabledAfterFailure)
            {
                _disabledAfterFailure = true;
                // Reachable at most once per installation, so a Hit here cannot become a hot-path cost.
                RateLimitedDiagnostics.Hit("RemoteClusterAnimationThrottle.lane-disabled");
            }
            return false;
        }

        /// <summary>
        /// Hands this tick back to the untouched original. That call advances the animation by one
        /// fixedDeltaTime itself, so the accumulator is dropped rather than carried across the gap.
        ///
        /// Known and accepted limitation of dropping it: if the tick before this one was a
        /// non-evaluating tick, its accumulated delta time is lost, so the animation of this character
        /// falls behind by up to (Interval - 1) * fixedDeltaTime - one fixed step, ~20ms at the default
        /// 50Hz - each time it crosses into a fallback state (starts climbing, gets grabbed, swaps an
        /// item, comes into the equip window...). The loss is monotone but not cumulative in any
        /// visible sense: it is bounded per boundary crossing, and crossings happen at human rates
        /// while ticks happen 50 times a second, so it cannot become a perceptible speed difference.
        /// Inside a continuous throttled stretch the animation clock is exact, which is the property
        /// the accumulator exists to provide.
        ///
        /// Considered and rejected: evaluating once with the accumulated delta time here, before
        /// handing the tick over. That would put two evaluations on the same tick - the replacement's
        /// and the original's - which means two RotateCharacter calls, and the second would capture the
        /// already look-rotated rig root as its rotationBefore (CharacterRagdoll.cs:196-200) and leave
        /// the root permanently skewed. Also rejected: carrying the remainder into the next tick, which
        /// would double-count it because the original always advances by a full fixedDeltaTime of its
        /// own regardless of what happened before. Neither is worth a permanently skewed rig or a
        /// fast-running animation to recover 20ms at a state transition, so the behaviour is left as
        /// it is.
        /// </summary>
        private static bool Yield(ConditionalWeakTable<CharacterRagdoll, State> states, CharacterRagdoll instance)
        {
            if (states.TryGetValue(instance, out State state) && state != null)
                return YieldState(state);
            return true;
        }

        /// <summary>Same as <see cref="Yield"/> for a state that is already in hand.</summary>
        private static bool YieldState(State state)
        {
            state.PendingDeltaTime = 0f;
            state.LastTick = int.MinValue;
            return true;
        }

        /// <summary>
        /// The audited body of CharacterRagdoll.FixedUpdate - :162-176, i.e. everything after the
        /// IsLocal-gated SetPhysicsMats at :153-156 and the firstFrame branch at :157-161 - in the same
        /// order and with the same calls, but it is not a literal transcription. The complete list of
        /// deviations, each with its direction:
        ///   1. Delta time. Both evaluations are handed the accumulated <paramref name="deltaTime"/>
        ///      instead of Time.fixedDeltaTime (the original passes the latter at
        ///      CharacterRagdoll.cs:164-165). That is the entire point of the patch: the graph is in
        ///      DirectorUpdateMode.Manual, so the animation clock has to be paid the ticks that were
        ///      skipped. Direction: preserves total animation time, lowers its resolution.
        ///   2. SetPhysicsMats is not called. In the audited build it opened the method
        ///      unconditionally, so the replacement had to call it; it is now gated on
        ///      character.IsLocal (CharacterRagdoll.cs:153-156) and every character that reaches this
        ///      replacement is remote (`c == local` is the first fallback, RemoteRagdollLod.cs:717).
        ///      Calling it would therefore add native col.sharedMaterial writes vanilla does not
        ///      perform. Direction: exact match with the new original, and it removes the only call in
        ///      this method that could throw for a reason outside the animation path.
        ///   3. `if (rigRoot == null) return false;` before anything runs. The original reaches
        ///      refs.rigCreator.transform inside RotateCharacter (CharacterRagdoll.cs:198) and would
        ///      throw there. Since deviation 2 there is nothing before that point in the original body
        ///      for a remote character, so the early return is now exactly "the tick did not happen",
        ///      with nothing skipped that vanilla would have completed first. The check is mandatory
        ///      here regardless: the rollback path below needs a transform to write the rotation back
        ///      to.
        ///   4. `if (rigBuilder != null)` around the SyncLayers/Evaluate pair. The original
        ///      dereferences character.refs.ikRigBuilder bare (CharacterRagdoll.cs:163-164).
        ///      Direction: more tolerant. Reported - see below.
        ///   5. `if (graph.IsValid())` around the animator graph evaluation. The original calls
        ///      playableGraph.Evaluate bare (CharacterRagdoll.cs:165) and the native binding is
        ///      declared ThrowsException (UnityEngine/Playables/PlayableGraph.cs:137-139), so an
        ///      invalid graph raises there rather than being ignored. Direction: more tolerant.
        ///      Reported - see below.
        ///   6. try/catch around the whole body plus a finally that runs ResetRotation and the
        ///      ResetTransform loop even when the body threw. The original has no handler, so a throw
        ///      anywhere between :162 and :171 leaves the rig root look-rotated and skips every
        ///      ResetTransform for that tick. Direction: strictly better than vanilla, and it is what
        ///      makes "failure does not hand the tick back" safe (see the last paragraph).
        ///   7. The firstFrame branch (CharacterRagdoll.cs:157-161) is not here; the Prefix yields to
        ///      the original on that tick instead, so the original owns both the check and the flag.
        ///      Direction: identical behaviour, different location.
        ///   8. refs and parts are resolved once by the Prefix and passed in, where the original
        ///      re-reads character.refs.* and partList on every line (CharacterRagdoll.cs:162-176).
        ///      Equivalent: nothing in the body reassigns Character.refs or CharacterRagdoll.partList,
        ///      and the Prefix has already null-checked both. Likewise the local rotationBefore is
        ///      only the fallback for deviation 6 - the normal path still restores through the game's
        ///      own ResetRotation, which reads the game's own rotationBefore field
        ///      (CharacterRagdoll.cs:202-205) exactly as vanilla does.
        /// Nothing else differs: RotateCharacter, SyncLayers, Evaluate, the graph evaluation,
        /// ConfigureIK, the SaveAnimationData loop, SaveAdditionalTransformPositions, ResetRotation and
        /// the ResetTransform loop all run, once each, in the audited order.
        ///
        /// On deviations 4 and 5 specifically. A null ikRigBuilder is not hypothetical: Character.Awake
        /// assigns it with refs.rigCreator.GetComponent&lt;RigBuilder&gt;() and then guards its own
        /// follow-up work with `if ((bool)refs.ikRigBuilder)` (Character.cs:377-388), so the game
        /// itself treats the component as optional - it is CharacterRagdoll.FixedUpdate that does not.
        /// On such a rig - a custom model with no RigBuilder, say - the original raises once per
        /// physics step, and because that throw happens at :163, after RotateCharacter at :162 and
        /// before ResetRotation at :172, vanilla also leaves the rig root look-rotated and skips every
        /// ResetTransform for that tick. The replacement skips just the IK sync (or just the graph
        /// evaluation), completes the rest of the body, reports `completed`, and keeps the character
        /// throttled.
        ///
        /// Accepted, for three reasons rather than one:
        ///   1. It replaces a per-tick exception plus a skewed rig root with a single no-op, so the
        ///      direction is strictly better than vanilla for that character.
        ///   2. It is bounded. If the rig builder is missing there is no IK to sync; if the animator
        ///      graph is invalid nothing is playing, so the accumulated delta time the caller has
        ///      already zeroed (see the evaluate branch in Prefix) had nothing to advance anyway.
        ///   3. This whole lane is opt-in and off by default.
        /// What made that trade worse than it needed to be was silence: the guards left a broken rig
        /// quiet *and* throttled forever, because nothing throws and so the per-instance failure latch
        /// is never armed. Both branches therefore report through RateLimitedDiagnostics -
        /// "RemoteClusterAnimationThrottle.rig-builder-unavailable" and
        /// "RemoteClusterAnimationThrottle.animator-graph-invalid". That restores observability
        /// without restoring the per-step exception: this method runs once per character per physics
        /// step (not per bodypart), so it is outside the hot-path logging ban that
        /// DetailBodypartThrottlePatch carries, and Hit logs the first occurrence and then at most one
        /// aggregate line per reason per 30s. Neither branch is reachable on a healthy rig, so on a
        /// healthy game the two reasons never appear at all.
        ///
        /// The asymmetry this note used to describe - guard the two testable calls, but keep the
        /// untestable SetPhysicsMats call and merely catch its exception - is gone with deviation 2:
        /// there is no untestable call left in this method, and the whole
        /// "RemoteClusterAnimationThrottle.set-physics-mats-failed" path went with it.
        ///
        /// Returns false if anything threw. The caller must not hand the same tick to the original in
        /// that case: RotateCharacter captures rotationBefore and then look-rotates the rig root
        /// (CharacterRagdoll.cs:196-200), so a second RotateCharacter in the same tick would capture
        /// the already-rotated value and leave the rig root permanently look-rotated.
        ///
        /// The rotation is therefore restored here, and restored twice over: through the game's own
        /// ResetRotation, and - if that itself throws - by writing back the value this method captured
        /// before rotating. That second path is what makes a failure recoverable rather than a
        /// permanently skewed rig.
        /// </summary>
        private static bool RunReplacement(
            CharacterRagdoll instance,
            Character.CharacterRefs refs,
            List<Bodypart> parts,
            float deltaTime)
        {
            Transform rigRoot = refs.rigCreator != null ? refs.rigCreator.transform : null;
            if (rigRoot == null)
                return false;

            // Captured before RotateCharacter so the rotation can be restored even if the game's own
            // ResetRotation is unavailable.
            Quaternion rotationBefore = rigRoot.rotation;
            bool rotated = false;
            bool completed = false;
            try
            {
                _rotateCharacter(instance);
                rotated = true;
                RigBuilder rigBuilder = refs.ikRigBuilder;
                // Not in the original: CharacterRagdoll.cs:163-164 dereferences
                // character.refs.ikRigBuilder bare, so a rig with no RigBuilder throws an NRE there on
                // every physics step. Here the IK sync is skipped instead and this tick is still
                // reported as completed, so such a character stays throttled. The else branch is what
                // keeps that from being silent - see the method docstring for the trade.
                if (rigBuilder != null)
                {
                    // Both calls, exactly as the audited body has them (CharacterRagdoll.cs:163-164),
                    // and both must stay. RigBuilder.Evaluate does call SyncLayers itself, but only
                    // inside `if (graph.IsValid())` (the guard is RigBuilder.cs:92, inside Evaluate at
                    // :90-97), and that property has exactly one assignment: the parameterless Build()
                    // (RigBuilder.cs:138). Which of the two calls actually does the work therefore
                    // depends on the component's enable history:
                    //   * RigBuilder.OnEnable calls the parameterless Build() while playing
                    //     (RigBuilder.cs:67-74) and so leaves `graph` valid. OnEnable runs before
                    //     Start, and CharacterRagdoll.Start then builds into the animator's graph with
                    //     the Build(PlayableGraph) overload (CharacterRagdoll.cs:53), which starts with
                    //     Clear() (RigBuilder.cs:153) - and Clear() destroys the graph
                    //     (RigBuilder.cs:171-173) without resetting the property. So in the steady
                    //     state `graph` is a stale invalid handle, Evaluate is a no-op, and the
                    //     explicit SyncLayers is the only one that runs.
                    //   * If the rig's GameObject is ever disabled and re-enabled, OnEnable builds a
                    //     valid graph again and nothing clears it, so Evaluate stops being a no-op and
                    //     runs its own SyncLayers plus a graph evaluation.
                    // Dropping either call would therefore change behaviour in one of those two
                    // states, and the explicit SyncLayers is what pushes the current IK target data
                    // into the constraint jobs before the graph evaluates.
                    rigBuilder.SyncLayers();
                    rigBuilder.Evaluate(deltaTime);
                }
                else
                {
                    // Restores the observability the guard above takes away. Not a hot path: this
                    // patch runs once per character per physics step, and this branch only on a rig
                    // where vanilla would raise an NRE every step instead. First hit always logs, then
                    // at most one aggregate line per 30s (RateLimitedDiagnostics.Hit).
                    RateLimitedDiagnostics.Hit("RemoteClusterAnimationThrottle.rig-builder-unavailable");
                }
                PlayableGraph graph = refs.animator.playableGraph;
                // Also not in the original: CharacterRagdoll.cs:165 calls Evaluate unguarded, and the
                // native binding is declared ThrowsException
                // (UnityEngine/Playables/PlayableGraph.cs:137-139), so an invalid graph raises there
                // every tick. Same trade, and same reporting, as the rigBuilder guard above.
                if (graph.IsValid())
                {
                    graph.Evaluate(deltaTime);
                }
                else
                {
                    RateLimitedDiagnostics.Hit("RemoteClusterAnimationThrottle.animator-graph-invalid");
                }
                refs.animations.ConfigureIK();
                for (int i = 0; i < parts.Count; i++)
                    parts[i].SaveAnimationData();
                _saveAdditionalTransformPositions(instance);
                completed = true;
            }
            catch (Exception)
            {
                RateLimitedDiagnostics.Hit("RemoteClusterAnimationThrottle.replacement-failed");
            }
            finally
            {
                if (rotated)
                {
                    try
                    {
                        _resetRotation(instance);
                    }
                    catch (Exception)
                    {
                        // Last line of defence: the rig root must not be left look-rotated.
                        rigRoot.rotation = rotationBefore;
                        RateLimitedDiagnostics.Hit("RemoteClusterAnimationThrottle.reset-failed");
                        completed = false;
                    }

                    try
                    {
                        for (int j = 0; j < parts.Count; j++)
                            parts[j].ResetTransform();
                    }
                    catch (Exception)
                    {
                        RateLimitedDiagnostics.Hit("RemoteClusterAnimationThrottle.reset-transform-failed");
                        completed = false;
                    }
                }
            }
            return completed;
        }

        /// <summary>
        /// Exact structure check for everything the replacement calls or relies on, including the
        /// two facts the safety argument rests on: the graph is in manual update mode, and the
        /// rotate/reset pair is what brackets the evaluation.
        /// </summary>
        private static void ValidateStructure()
        {
            ScaleValidation.RequireInstanceField(typeof(Character), "refs", typeof(Character.CharacterRefs));
            ScaleValidation.RequireInstanceField(
                typeof(Character.CharacterRefs), "ikRigBuilder", typeof(RigBuilder));
            ScaleValidation.RequireInstanceField(
                typeof(Character.CharacterRefs), "animator", typeof(Animator));
            ScaleValidation.RequireInstanceField(
                typeof(Character.CharacterRefs), "animations", typeof(CharacterAnimations));
            ScaleValidation.RequireInstanceField(
                typeof(Character.CharacterRefs), "rigCreator", typeof(RigCreator));

            ScaleValidation.RequireInstanceMethod(
                typeof(CharacterAnimations), "ConfigureIK", typeof(void), Type.EmptyTypes, true);
            ScaleValidation.RequireInstanceMethod(
                typeof(Bodypart), "SaveAnimationData", typeof(void), Type.EmptyTypes, true);
            ScaleValidation.RequireInstanceMethod(
                typeof(Bodypart), "ResetTransform", typeof(void), Type.EmptyTypes, true);
            ScaleValidation.RequireInstanceMethod(
                typeof(RigBuilder), "SyncLayers", typeof(void), Type.EmptyTypes, true);
            ScaleValidation.RequireInstanceMethod(
                typeof(RigBuilder), "Evaluate", typeof(void), new[] { typeof(float) }, true);

            // The manual update mode is what makes graph.Evaluate the only clock of the animation,
            // and therefore what makes passing the accumulated delta time mandatory.
            ScaleValidation.RequireInstanceProperty(typeof(Animator), "playableGraph", typeof(PlayableGraph));
            // Asserted through the shared helper rather than a bare GetMethod + null check. The
            // previous form only proved the two members existed; RequireInstanceMethod additionally
            // pins the declaring type, the instance/static shape, the return type, the parameter count
            // and every parameter type, which is the same standard every other assertion in this
            // method is held to. PlayableGraph and DirectorUpdateMode are both plain compile-time
            // types here - they live in UnityEngine.CoreModule, which the project references
            // (PeakSafeOptimizer.csproj:33-36), and typeof(PlayableGraph) is already used on the line
            // above - so the by-name variant is not needed. RequireInstanceMethod also throws on a
            // mismatch rather than returning null, so the two locals and the manual
            // MissingMethodException throw are gone; nothing consumes the MethodInfo, these are pure
            // structural assertions. Evaluate(float) is distinguished from the parameterless
            // Evaluate() (UnityEngine/Playables/PlayableGraph.cs:75-78 and :137-139) by the parameter
            // type array, exactly as before.
            ScaleValidation.RequireInstanceMethod(
                typeof(PlayableGraph), "SetTimeUpdateMode", typeof(void),
                new[] { typeof(DirectorUpdateMode) }, requirePublic: true);
            ScaleValidation.RequireInstanceMethod(
                typeof(PlayableGraph), "Evaluate", typeof(void), new[] { typeof(float) },
                requirePublic: true);
            if (!Enum.IsDefined(typeof(DirectorUpdateMode), DirectorUpdateMode.Manual))
                throw new InvalidOperationException("DirectorUpdateMode.Manual is no longer defined");
            // CharacterRagdoll.Start is where Stop() + SetTimeUpdateMode(Manual) happen.
            ScaleValidation.RequireInstanceMethod(
                typeof(CharacterRagdoll), "Start", typeof(void), Type.EmptyTypes, false);
            // The physics-step index this patch throttles on comes from RemoteRagdollLod.PhysicsTick,
            // whose Time.fixedTimeAsDouble assertion runs in RemoteRagdollLod.Acquire - which this
            // patch's Initialize always calls - so it is not repeated here.
        }
    }
}
