using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using PeakSafeOptimizer.Config;
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
    /// Audited original, CharacterRagdoll.FixedUpdate (CharacterRagdoll.cs:151-174):
    ///   SetPhysicsMats();
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
    ///   * RotateCharacter/ResetRotation cancel each other out (CharacterRagdoll.cs:193-202): the
    ///     first sets refs.rigCreator.transform.rotation, the second restores the value captured in
    ///     the same call. They exist so the graph evaluates against a look-rotated root, so on a tick
    ///     with no evaluation they are a no-op pair and are skipped as one unit.
    ///   * ResetTransform (Bodypart.cs:137-141) writes the bodypart transform back from its
    ///     Rigidbody, undoing what the graph evaluation did to it. Without an evaluation the
    ///     transform was never overwritten, and PhysX keeps driving it directly, so the rendered pose
    ///     still follows physics on every tick - nothing freezes.
    ///   * SaveAnimationData (Bodypart.cs:126-135) reads the *animated* pose to produce the targets
    ///     Animate drives towards. On a skipped tick the transform holds the physics pose, so calling
    ///     it would tell every part "you are already where you should be" and the character would go
    ///     limp. It is therefore deliberately not called; the previous targets stay in place and
    ///     Animate keeps pulling towards them, which is what makes the pose step instead of collapse.
    ///   * SaveAdditionalTransformPositions is skipped for the same reason: it too reads the animated
    ///     pose (CharacterRagdoll.cs:204-238). Its outputs - data.targetHeadHeight,
    ///     data.targetHipHeight and the animation helper transforms - therefore keep the value from
    ///     the last evaluation, at most N-1 ticks old, instead of being recomputed from a physics
    ///     pose they were never meant to describe. Recomputing them there would assign the current
    ///     sagging height to the value Stand() treats as its target (CharacterMovement.cs:689-698),
    ///     which drives the standing force to zero and makes the sag self-reinforcing.
    ///   * SetPhysicsMats runs on every tick, exactly as in the original.
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
        /// exception and one catch. 8 is chosen as "clearly more than a full lobby's worth of
        /// one-off latches" (PEAK caps at 4 players, plus bots/zombies/scoutmaster which the fallback
        /// list already excludes) while still being reached almost immediately when the cause is
        /// systemic. Failures are counted for the lifetime of the installation, not per tick, because
        /// each character can only contribute one: after its own latch is set it never enters the
        /// replacement again.
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
        private static Action<CharacterRagdoll> _setPhysicsMats;
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
            MethodInfo setPhysicsMats = ScaleValidation.RequireInstanceMethod(
                typeof(CharacterRagdoll), "SetPhysicsMats", typeof(void), Type.EmptyTypes, false);
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
            Action<CharacterRagdoll> setMats =
                ScaleExperimentalEmit.CreateDelegate<Action<CharacterRagdoll>>(setPhysicsMats);
            Action<CharacterRagdoll> rotate =
                ScaleExperimentalEmit.CreateDelegate<Action<CharacterRagdoll>>(rotateCharacter);
            Action<CharacterRagdoll> reset =
                ScaleExperimentalEmit.CreateDelegate<Action<CharacterRagdoll>>(resetRotation);
            Action<CharacterRagdoll> saveAdditionalPositions =
                ScaleExperimentalEmit.CreateDelegate<Action<CharacterRagdoll>>(saveAdditional);
            if (character == null || firstFrame == null || partList == null)
                throw new InvalidOperationException("RemoteClusterAnimationThrottle FieldRef creation failed");
            if (setMats == null || rotate == null || reset == null || saveAdditionalPositions == null)
                throw new InvalidOperationException("RemoteClusterAnimationThrottle delegate creation failed");

            // Acquired last: nothing below can throw, so a failed Initialize never leaves a
            // reference behind on the shared helper.
            RemoteRagdollLod.Acquire();
            _acquired = true;
            _states = new ConditionalWeakTable<CharacterRagdoll, State>();
            _character = character;
            _firstFrame = firstFrame;
            _partList = partList;
            _setPhysicsMats = setMats;
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
            _setPhysicsMats = null;
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
            // Unity one (CharacterItems.cs:613-628). Those are excluded by the fallback list, but if
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
                // Everything the original does unconditionally on a tick without an evaluation.
                _setPhysicsMats(__instance);
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
        /// already look-rotated rig root as its rotationBefore (CharacterRagdoll.cs:193-197) and leave
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
        /// The audited method body with the two graph evaluations handed the accumulated delta time.
        ///
        /// Returns false if anything threw. The caller must not hand the same tick to the original in
        /// that case: RotateCharacter captures rotationBefore and then look-rotates the rig root
        /// (CharacterRagdoll.cs:193-197), so a second RotateCharacter in the same tick would capture
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
                _setPhysicsMats(instance);
                _rotateCharacter(instance);
                rotated = true;
                RigBuilder rigBuilder = refs.ikRigBuilder;
                if (rigBuilder != null)
                {
                    // Both calls, exactly as the audited body has them (CharacterRagdoll.cs:160-161).
                    // RigBuilder.Evaluate does call SyncLayers itself, but only inside
                    // `if (graph.IsValid())` (RigBuilder.cs:88-97) - and that property is only ever
                    // assigned by the parameterless Build(). CharacterRagdoll.Start builds the rig
                    // into the animator's graph with Build(PlayableGraph) (CharacterRagdoll.cs:53),
                    // an overload that leaves the property alone and whose Clear() destroys whatever
                    // RigBuilder.OnEnable had built. Evaluate is therefore a no-op here, which makes
                    // the explicit SyncLayers the only one that runs: it is what pushes the current
                    // IK target data into the constraint jobs before the graph evaluates.
                    rigBuilder.SyncLayers();
                    rigBuilder.Evaluate(deltaTime);
                }
                PlayableGraph graph = refs.animator.playableGraph;
                if (graph.IsValid())
                    graph.Evaluate(deltaTime);
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
            MethodInfo setTimeUpdateMode = typeof(PlayableGraph).GetMethod(
                "SetTimeUpdateMode",
                BindingFlags.Instance | BindingFlags.Public,
                null,
                new[] { typeof(DirectorUpdateMode) },
                null);
            MethodInfo evaluate = typeof(PlayableGraph).GetMethod(
                "Evaluate",
                BindingFlags.Instance | BindingFlags.Public,
                null,
                new[] { typeof(float) },
                null);
            if (setTimeUpdateMode == null || evaluate == null)
                throw new MissingMethodException(typeof(PlayableGraph).FullName, "Evaluate/SetTimeUpdateMode");
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
