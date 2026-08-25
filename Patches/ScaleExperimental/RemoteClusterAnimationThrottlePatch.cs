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
    ///     since the previous evaluation. Total animation time stays exact; only the temporal
    ///     resolution drops to 50/N Hz.
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
    /// partially ragdolled or inside the item-equip window keeps vanilla behaviour on every tick.
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

        private sealed class State
        {
            internal int LastTick = int.MinValue;
            /// <summary>Delta time of every tick since the last evaluation, including this one.</summary>
            internal float PendingDeltaTime;
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
        /// Set once the replacement has failed. From then on every call defers to the untouched
        /// original, so a persistent failure degrades to vanilla behaviour instead of repeating an
        /// exception, a catch and a diagnostics hit for every character on every physics step.
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
            _disabledAfterFailure = false;
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

            // A previous replacement threw; never try again for the rest of this session.
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
            // A manual FixedUpdate() call from the equip coroutine lands on the same tick as the
            // Unity one (CharacterItems.cs:613-628). Those are excluded by the fallback list, but if
            // one ever reaches here it must not consume a second slice of delta time.
            if (state.LastTick == tick)
                return true;
            // A gap means the untouched original ran in between and already advanced the animation by
            // its own delta time, so anything accumulated before the gap has been accounted for.
            if (state.LastTick != tick - 1)
                state.PendingDeltaTime = 0f;
            state.LastTick = tick;
            // Clamped so a long stall cannot hand the graph one huge step.
            state.PendingDeltaTime = Mathf.Min(state.PendingDeltaTime + deltaTime, deltaTime * Interval);

            // Per-instance phase so remote characters do not all evaluate on the same tick, which
            // would trade a flat cost for a periodic spike.
            int phase = Math.Abs(__instance.GetInstanceID() % Interval);
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

            // The replacement failed and has already restored the rig rotation it changed. Hand this
            // tick and every later one back to the original.
            _disabledAfterFailure = true;
            return false;
        }

        /// <summary>
        /// Hands this tick back to the untouched original. That call advances the animation by one
        /// fixedDeltaTime itself, so the accumulator is dropped rather than carried across the gap.
        /// </summary>
        private static bool Yield(ConditionalWeakTable<CharacterRagdoll, State> states, CharacterRagdoll instance)
        {
            if (states.TryGetValue(instance, out State state) && state != null)
            {
                state.PendingDeltaTime = 0f;
                state.LastTick = int.MinValue;
            }
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
                    // RigBuilder.Evaluate calls SyncLayers itself (RigBuilder.cs:88-97), so unlike the
                    // audited body this does not sync twice.
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
        }
    }
}
