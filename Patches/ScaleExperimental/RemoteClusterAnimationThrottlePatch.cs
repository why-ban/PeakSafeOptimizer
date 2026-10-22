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
    /// Replaces CharacterRagdoll.FixedUpdate's body on every Nth physics tick, handing both graph
    /// evaluations the accumulated delta time: the graph is DirectorUpdateMode.Manual, so this call is
    /// its only clock. On a non-evaluating tick RotateCharacter/ResetRotation are a no-op pair and
    /// ResetTransform is unnecessary: the transform was never overwritten, so PhysX keeps driving the pose. SaveAnimationData and SaveAdditionalTransformPositions are skipped there because
    /// both read the animated pose; recomputing the stand heights from the physics pose makes Stand's
    /// target converge on the current pose and the sag self-reinforcing. SetPhysicsMats is not called: its
    /// only call is IsLocal-gated and every throttled character is remote by construction. A failed
    /// replacement latches that character to the original and keeps the tick skipped, because a second
    /// RotateCharacter would capture the already-rotated rig root; yielding a tick drops the accumulator,
    /// costing up to one fixed step per crossing.
    /// </summary>
    internal static class RemoteClusterAnimationThrottlePatch
    {
        private const int Interval = 2;
        private const int MaxTotalFailures = 8;

        private sealed class State
        {
            internal int LastTick = int.MinValue;
            internal float PendingDeltaTime;
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
        private static int _failureCount;
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

            var states = new ConditionalWeakTable<CharacterRagdoll, State>();

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
            _disabledAfterFailure = false;
            _failureCount = 0;
            if (_acquired)
            {
                _acquired = false;
                RemoteRagdollLod.Release();
            }
        }

        [HarmonyPriority(Priority.Low)]
        internal static bool Prefix(CharacterRagdoll __instance, bool __runOriginal)
        {
            if (!__runOriginal)
                return false;

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
            if (state.DisabledAfterFailure)
                return YieldState(state);
            if (state.LastTick == tick)
                return Yield(states, __instance);
            if (state.LastTick != tick - 1)
                state.PendingDeltaTime = 0f;
            state.LastTick = tick;
            state.PendingDeltaTime = Mathf.Min(state.PendingDeltaTime + deltaTime, deltaTime * Interval);

            int phase = RemoteRagdollLod.Phase(__instance.GetInstanceID(), Interval);
            bool evaluate = (tick + phase) % Interval == 0;
            if (!evaluate)
            {
                return false;
            }

            float accumulated = state.PendingDeltaTime;
            state.PendingDeltaTime = 0f;
            if (RunReplacement(__instance, refs, parts, accumulated))
                return false;

            state.DisabledAfterFailure = true;
            state.PendingDeltaTime = 0f;
            state.LastTick = int.MinValue;
            _failureCount++;
            if (_failureCount >= MaxTotalFailures && !_disabledAfterFailure)
            {
                _disabledAfterFailure = true;
                RateLimitedDiagnostics.Hit("RemoteClusterAnimationThrottle.lane-disabled");
            }
            return false;
        }

        private static bool Yield(ConditionalWeakTable<CharacterRagdoll, State> states, CharacterRagdoll instance)
        {
            if (states.TryGetValue(instance, out State state) && state != null)
                return YieldState(state);
            return true;
        }

        private static bool YieldState(State state)
        {
            state.PendingDeltaTime = 0f;
            state.LastTick = int.MinValue;
            return true;
        }

        private static bool RunReplacement(
            CharacterRagdoll instance,
            Character.CharacterRefs refs,
            List<Bodypart> parts,
            float deltaTime)
        {
            Transform rigRoot = refs.rigCreator != null ? refs.rigCreator.transform : null;
            if (rigRoot == null)
                return false;

            Quaternion rotationBefore = rigRoot.rotation;
            bool rotated = false;
            bool completed = false;
            try
            {
                _rotateCharacter(instance);
                rotated = true;
                RigBuilder rigBuilder = refs.ikRigBuilder;
                if (rigBuilder != null)
                {
                    rigBuilder.SyncLayers();
                    rigBuilder.Evaluate(deltaTime);
                }
                else
                {
                    RateLimitedDiagnostics.Hit("RemoteClusterAnimationThrottle.rig-builder-unavailable");
                }
                PlayableGraph graph = refs.animator.playableGraph;
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

            ScaleValidation.RequireInstanceProperty(typeof(Animator), "playableGraph", typeof(PlayableGraph));
            ScaleValidation.RequireInstanceMethod(
                typeof(PlayableGraph), "SetTimeUpdateMode", typeof(void),
                new[] { typeof(DirectorUpdateMode) }, requirePublic: true);
            ScaleValidation.RequireInstanceMethod(
                typeof(PlayableGraph), "Evaluate", typeof(void), new[] { typeof(float) },
                requirePublic: true);
            if (!Enum.IsDefined(typeof(DirectorUpdateMode), DirectorUpdateMode.Manual))
                throw new InvalidOperationException("DirectorUpdateMode.Manual is no longer defined");
            ScaleValidation.RequireInstanceMethod(
                typeof(CharacterRagdoll), "Start", typeof(void), Type.EmptyTypes, false);
        }
    }
}
