using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using PeakSafeOptimizer.Config;
using PeakSafeOptimizer.Patches.Scale;
using Photon.Pun;
using UnityEngine;

namespace PeakSafeOptimizer.Patches.ScaleExperimental
{
    /// <summary>
    /// Shared distance tier decision for the four opt-in RemoteRagdollLod patches. It answers one
    /// question: may the animation-driven work of this remote character be skipped this frame?
    ///
    /// What the four patches skip when this returns true:
    ///   CharacterRagdoll.FixedUpdate  (animation graph + IK evaluation, SaveAnimationData,
    ///                                  ResetRotation/ResetTransform, SetPhysicsMats)
    ///   Bodypart.Animate              (joint target rotation, follow torque, follow force)
    ///   Bodypart.AddMovementForce     (locally simulated movement force)
    ///   CharacterAnimations.Update    (Animator parameter writes)
    ///
    /// What is deliberately NOT touched, and why the result is a passive ragdoll rather than a
    /// broken one (all four calls stay in CharacterMovement.FixedUpdate, audited source
    /// CharacterMovement.cs:304-315, and its structure is verified in Initialize):
    ///   Bodypart.Gravity          -> gravity keeps being accumulated, so nothing floats.
    ///   Bodypart.ToggleUseGravity -> Rigidbody.useGravity keeps tracking currentRagdollControll.
    ///   Bodypart.Drag             -> per-step velocity damping still runs, so velocity converges
    ///                                instead of growing without bound.
    ///   Bodypart.ApplyForces      -> forcesToAdd is applied and then zeroed every physics step,
    ///                                so the accumulator can never build up an explosive impulse.
    /// Colliders, Rigidbody enable state, kinematic state, CharacterSyncer position interpolation
    /// and every RPC path are untouched, so remote positions keep being driven by the network as
    /// usual and collision behaviour is unchanged. No new force, torque or transform write is
    /// introduced anywhere, so the patches cannot inject a NaN.
    ///
    /// The fallback list below guarantees that the local character, bots, the spectated character,
    /// zombies, the scoutmaster, warping, carried/carrying, grabbed, climbing, dead, fully passed
    /// out, kinematic and partially ragdolled characters are never downgraded. A crouch/sprint pose
    /// change and an item change additionally force a character back to full behaviour and re-arm the
    /// warm-up window.
    ///
    /// Distances are measured from Character.observedCharacter (falling back to
    /// Character.localCharacter), i.e. from what the camera is looking through rather than from
    /// whichever character this client owns. Those two differ while spectating, and the visible
    /// consequence of confusing them is a frozen pose right in front of the camera.
    /// </summary>
    internal static class RemoteRagdollLod
    {
        internal const int TierFull = 0;
        internal const int TierPassiveRagdoll = 2;

        /// <summary>
        /// Distance hysteresis: a remote character enters the passive tier at or beyond
        /// <see cref="EnterDistance"/> and returns to full behaviour below <see cref="ExitDistance"/>.
        /// The gap is what stops the tier oscillating every frame for someone walking the boundary.
        /// 60m is past the range where a frozen pose reads as wrong.
        /// </summary>
        private const float EnterDistance = 60f;
        private const float ExitDistance = 50f;
        private const float FullRagdollControlThreshold = 0.9f;
        /// <summary>
        /// Distinct *physics steps* a character must be seen at full behaviour before it may be
        /// downgraded, counted from first sight and re-armed on every item or pose change. Two
        /// separate correctness requirements share this one window:
        ///
        /// 1. Animation targets must exist. Bodypart.SaveAnimationData (the producer of
        ///    targetOffsetRelativeToHip / targetForward / targetUp) only runs inside
        ///    CharacterRagdoll.FixedUpdate. A character already beyond the enter distance on its very
        ///    first evaluated frame would otherwise be downgraded before those targets were ever
        ///    written, and any later Bodypart.SnapToAnim - which CharacterRagdoll.SnapToAnimation
        ///    calls directly from the item-equip coroutine (CharacterItems.cs:613-628) - would then
        ///    snap every bodypart onto the hip, because WorldTargetPos() = hip + Vector3.zero.
        ///    One physics step is not enough on its own: the very first CharacterRagdoll.FixedUpdate
        ///    of a character returns right after SetPhysicsMats through the firstFrame branch
        ///    (CharacterRagdoll.cs:153-158) and produces no targets at all, so the window has to span
        ///    strictly more than one step.
        /// 2. Pose changes must be given time to blend. While downgraded, data.targetHeadHeight and
        ///    data.targetHipHeight are frozen at their last really-evaluated values (see
        ///    RemoteRagdollLodPhysicsPatch for why re-deriving them from a physics pose is wrong),
        ///    and their consumers - CharacterMovement.Stand (CharacterMovement.cs:690-699) and
        ///    RaycastGroundCheck (:874-877) - do run for remote characters. A crouch or sprint
        ///    transition changes both heights, so the window has to be long enough for the animator
        ///    to blend into the new pose while the character is still fully evaluated. 15 steps is
        ///    0.3s at the default 50Hz fixed step. The exact blend length lives in the animator asset
        ///    and is not visible in the decompiled source, so 0.3s is a judgement call rather than a
        ///    derived number; it is the same order as the game's own crouch-related timings
        ///    (CharacterMovement.cs:240 uses a 0.2s sinceGrounded window, CharacterAnimations damps its
        ///    pose parameters over 0.125-0.25s). Erring long only delays a downgrade.
        ///
        /// Counted in physics steps rather than rendered frames on purpose: the producer in (1) is
        /// driven by FixedUpdate, so at 200fps over a 50Hz fixed step a frame-counted window could
        /// elapse without a single target write. The counter advances at most once per rendered frame,
        /// because the decision cache below answers repeat calls within a frame from cache, so when
        /// several physics steps share one rendered frame the window ends up longer than 15 steps
        /// rather than shorter - erring long only delays a downgrade, which is the safe direction.
        /// </summary>
        private const int WarmupPhysicsSteps = 15;
        /// <summary>
        /// Recency window that keeps a character at full behaviour around an item equip, where
        /// CharacterRagdoll.FixedUpdate and SnapToAnimation are invoked directly rather than by
        /// Unity, so the snapped pose must not be stale (CharacterItems.cs:619-627 resets
        /// data.sinceItemAttach on every iteration of that loop).
        /// </summary>
        private const float ItemAttachRecencySeconds = 0.5f;
        private const string FixedJointTypeName = "UnityEngine.FixedJoint";

        /// <summary>
        /// Per-character tier plus a one-frame decision cache. Never references its
        /// ConditionalWeakTable key.
        /// </summary>
        private sealed class State
        {
            internal int Tier;
            internal int LastFrame = int.MinValue;
            internal bool LastResult;
            internal int LastItemId = int.MinValue;
            /// <summary>
            /// Packed crouch/sprint flags last seen at evaluation time, or int.MinValue before the
            /// first sight. See <see cref="PoseFlags"/> for why these two states are tracked.
            /// </summary>
            internal int LastPoseFlags = int.MinValue;
            /// <summary>
            /// Distinct physics steps seen at full behaviour; capped at
            /// <see cref="WarmupPhysicsSteps"/>.
            /// </summary>
            internal int FullSteps;
            /// <summary>
            /// Physics step on which <see cref="FullSteps"/> was last incremented. The warm-up is
            /// counted per physics step because its producer runs in FixedUpdate, while the decision
            /// cache below stays per rendered frame; the two counters must therefore be separate.
            /// </summary>
            internal int LastWarmupTick = int.MinValue;

            /// <summary>
            /// Separate per-physics-tick cache for <see cref="ShouldThrottleAnimation"/>. It uses its
            /// own fields so the distance-tier state machine above cannot be perturbed by a caller
            /// that does not participate in the hysteresis.
            /// </summary>
            internal int ThrottleTick = int.MinValue;
            internal bool ThrottleResult;
            internal int ThrottleItemId = int.MinValue;
            internal int ThrottlePoseFlags = int.MinValue;
        }

        private static ConditionalWeakTable<Character, State> _states;
        private static AccessTools.FieldRef<Bodypart, Character> _bodypartCharacter;
        private static AccessTools.FieldRef<CharacterData, Character> _grabbedPlayer;
        private static AccessTools.FieldRef<CharacterData, Character> _grabbingPlayer;
        private static Func<CharacterData, UnityEngine.Object> _grabJoint;
        private static int _refCount;

        /// <summary>
        /// Structural validation plus accessor creation, shared by the four patches. Reference
        /// counted so that disabling one of them cannot pull the accessors out from under the
        /// others. Every accessor is created once here; the hot path uses no reflection, no LINQ and
        /// allocates only the per-character State on first sight.
        /// </summary>
        internal static void Acquire()
        {
            ValidateStructure();

            FieldInfo bodypartCharacterField = ScaleValidation.RequireInstanceField(
                typeof(Bodypart), "character", typeof(Character));
            FieldInfo grabJointField = ScaleValidation.RequireInstanceFieldByTypeName(
                typeof(CharacterData), "grabJoint", FixedJointTypeName);
            FieldInfo grabbedPlayerField = ScaleValidation.RequireInstanceField(
                typeof(CharacterData), "grabbedPlayer", typeof(Character));
            FieldInfo grabbingPlayerField = ScaleValidation.RequireInstanceField(
                typeof(CharacterData), "grabbingPlayer", typeof(Character));

            AccessTools.FieldRef<Bodypart, Character> bodypartCharacter =
                AccessTools.FieldRefAccess<Bodypart, Character>(bodypartCharacterField);
            Func<CharacterData, UnityEngine.Object> grabJoint =
                ScaleEmit.CreateUnityObjectFieldGetter<CharacterData>(grabJointField);
            AccessTools.FieldRef<CharacterData, Character> grabbedPlayer =
                AccessTools.FieldRefAccess<CharacterData, Character>(grabbedPlayerField);
            AccessTools.FieldRef<CharacterData, Character> grabbingPlayer =
                AccessTools.FieldRefAccess<CharacterData, Character>(grabbingPlayerField);
            if (bodypartCharacter == null || grabbedPlayer == null || grabbingPlayer == null || grabJoint == null)
                throw new InvalidOperationException("RemoteRagdollLod accessor creation failed");

            if (_states == null)
                _states = new ConditionalWeakTable<Character, State>();
            _bodypartCharacter = bodypartCharacter;
            _grabbedPlayer = grabbedPlayer;
            _grabbingPlayer = grabbingPlayer;
            _grabJoint = grabJoint;
            _refCount++;
        }

        /// <summary>
        /// Released by each patch's Reset, which the installer only calls after that patch's Harmony
        /// methods are confirmed detached. The accessors are dropped once the last user is gone.
        /// </summary>
        internal static void Release()
        {
            if (_refCount > 0)
                _refCount--;
            if (_refCount > 0)
                return;
            _refCount = 0;
            _states = null;
            _bodypartCharacter = null;
            _grabbedPlayer = null;
            _grabbingPlayer = null;
            _grabJoint = null;
        }

        /// <summary>
        /// Resolves the Character that owns a Bodypart through the game's own cached back reference
        /// (Bodypart.cs:15, assigned in Bodypart.Start at Bodypart.cs:97). No GetComponentInParent
        /// and no extra cache table is needed. A null result means Start has not run yet, which is
        /// reported as "do not downgrade".
        /// </summary>
        internal static Character ResolveOwner(Bodypart part)
        {
            AccessTools.FieldRef<Bodypart, Character> characterRef = _bodypartCharacter;
            if (characterRef == null || part == null)
                return null;
            return characterRef(part);
        }

        internal static bool ShouldDowngrade(Character c)
        {
            ConditionalWeakTable<Character, State> states = _states;
            Func<CharacterData, UnityEngine.Object> grabJoint = _grabJoint;
            AccessTools.FieldRef<CharacterData, Character> grabbedPlayerRef = _grabbedPlayer;
            AccessTools.FieldRef<CharacterData, Character> grabbingPlayerRef = _grabbingPlayer;
            if (states == null || grabJoint == null || grabbedPlayerRef == null || grabbingPlayerRef == null)
            {
                // No diagnostics: this is reached from Bodypart.Animate and Bodypart.AddMovementForce,
                // i.e. once per bodypart per physics step, so a Hit here would turn a missing accessor
                // into a per-part dictionary lookup and Interlocked on every call. Installation
                // failures are already reported by the installer.
                return false;
            }
            if (c == null)
                return false;

            CharacterData data = c.data;
            // c.refs is deliberately not read here. An earlier version fetched it and required it to
            // be non-null, but nothing below used the value, and TryGetCenter re-reads refs with its
            // own null handling - so the check was a dead read whose stated precondition was not a
            // precondition of anything.
            // Two different reference frames, deliberately kept apart:
            //   local            = "which character is mine". Used by the fallback list for the
            //                      c == local and photonView.IsMine identity tests. It must stay the
            //                      local character even while spectating, because owning a character
            //                      has nothing to do with where the camera is.
            //   viewpointCharacter = "what the camera is looking through". Used only as the distance
            //                      origin. Character.observedCharacter is MainCameraMovement's spec
            //                      target and falls back to localCharacter itself (Character.cs:
            //                      203-214), so after the local character dies and the player starts
            //                      spectating, the camera is somewhere else entirely. Measuring
            //                      distance from the local corpse instead of the camera would freeze
            //                      the pose of a character standing five metres in front of the
            //                      spectating player, which is directly visible.
            // Mixing the two would either downgrade the character the camera is following or stop
            // downgrading everyone whenever the two differ, so they are never interchangeable.
            Character local = Character.localCharacter;
            // Static property: read once into a local, then null-checked.
            Character viewpointCharacter = Character.observedCharacter;
            if (viewpointCharacter == null)
                viewpointCharacter = local;
            if (data == null || local == null || viewpointCharacter == null)
                return false;

            int frame = Time.frameCount;
            State state = states.GetOrCreateValue(c);
            // Checked before the per-frame cache on purpose. CharacterItems.Equip writes
            // data.currentItem and then, in the same rendered frame, drives
            // ragdoll.FixedUpdate()/SnapToAnimation() manually before sinceItemAttach is reset.
            // Reusing a cached downgrade there would let SnapToAnim consume stale animation targets.
            int itemId = data.currentItem != null ? data.currentItem.GetInstanceID() : 0;
            if (state.LastItemId == int.MinValue)
            {
                state.LastItemId = itemId;
            }
            else if (state.LastItemId != itemId)
            {
                state.LastItemId = itemId;
                state.Tier = TierFull;
                state.FullSteps = 0;
                state.LastWarmupTick = int.MinValue;
                state.LastResult = false;
                state.LastFrame = frame;
                return false;
            }

            // Same mechanism for the crouch/sprint pose, and for the same class of reason: the two
            // stand heights are frozen while downgraded, so a pose change has to be evaluated at full
            // behaviour long enough for the animator to blend into it. See PoseFlags.
            int poseFlags = PoseFlags(data);
            if (state.LastPoseFlags == int.MinValue)
            {
                state.LastPoseFlags = poseFlags;
            }
            else if (state.LastPoseFlags != poseFlags)
            {
                state.LastPoseFlags = poseFlags;
                state.Tier = TierFull;
                state.FullSteps = 0;
                state.LastWarmupTick = int.MinValue;
                state.LastResult = false;
                state.LastFrame = frame;
                return false;
            }

            // One evaluation per character per rendered frame. Animate/AddMovementForce are called
            // from physics while CharacterAnimations.Update is rendered; sharing this decision
            // avoids contradictory transitions between those call families.
            if (state.LastFrame == frame)
                return state.LastResult;

            bool warmed = state.FullSteps >= WarmupPhysicsSteps;
            bool result = warmed && Evaluate(
                c, data, local, viewpointCharacter, grabJoint, grabbedPlayerRef, grabbingPlayerRef, state.Tier);
            if (!warmed)
            {
                // Counted per physics step, not per rendered frame: the animation targets the warm-up
                // waits for are produced by CharacterRagdoll.FixedUpdate. The decision cache above
                // stays per rendered frame; only this counter changes unit.
                int warmupTick = PhysicsTick();
                if (state.LastWarmupTick != warmupTick)
                {
                    state.LastWarmupTick = warmupTick;
                    state.FullSteps++;
                }
            }
            // A fallback hit or a near distance both mean "full behaviour", so re-entry always has
            // to cross the enter distance again.
            state.Tier = result ? TierPassiveRagdoll : TierFull;
            state.LastFrame = frame;
            state.LastResult = result;
            return result;
        }

        /// <summary>
        /// Crouch and sprint packed into one int so a change in either can be detected with a single
        /// comparison and without allocating.
        ///
        /// Why these two specifically: while a character is downgraded,
        /// RemoteRagdollLodPhysicsPatch skips SaveAdditionalTransformPositions, so
        /// data.targetHeadHeight and data.targetHipHeight keep the value from the last real
        /// evaluation (re-deriving them from the physics pose is the self-reinforcing sag described in
        /// that patch). Verified that both values really are consumed for remote characters:
        /// CharacterMovement.FixedUpdate (CharacterMovement.cs:283-317) has no IsLocal or
        /// photonView.IsMine gate anywhere - the only local-only gates in that class are in Update
        /// (:181) and in TryJetpack/JetpackRPC (:410, :569) - so RaycastGroundCheck (:286 -> :874-877,
        /// which uses targetHipHeight as its ray length) and Stand (:288-291 -> :690-699, which uses
        /// targetHeadHeight as its InverseLerp target) both run for every character on this client,
        /// remote ones included. Neither field is in the fallback list, and neither
        /// CharacterMovement.SetMovementState (which is IsMine gated, :224) nor the crouch RPC
        /// (:216-220) nor CharacterSyncer (CharacterSyncer.cs:110, isSprinting) touches the heights;
        /// only CharacterRagdoll.SaveAdditionalTransformPositions writes them
        /// (CharacterRagdoll.cs:208, :215 - the only two writes in the whole assembly). A remote
        /// character crouching or sprinting 60m away would therefore keep standing to its previous
        /// height and ground-check with its previous ray length until it came back inside the exit
        /// distance. Both flags do arrive on this client for a remote character - isCrouching through
        /// the RPCA_SetCrouch RPC sent to RpcTarget.All (CharacterMovement.cs:212-220) and isSprinting
        /// through the sync stream (CharacterSyncer.cs:110) - so the change is observable here on the
        /// tick it happens. Treating a change as "return to full behaviour and re-arm the warm-up"
        /// costs one warm-up window per crouch or sprint transition and removes the stale-height window
        /// entirely.
        ///
        /// Accepted cost of that choice: a remote character that toggles sprint or crouch more often
        /// than once per warm-up window never gets downgraded at all. That is the vanilla behaviour it
        /// would have had anyway, i.e. the failure mode is "no optimisation", and the alternative - a
        /// character standing 80m away at a stale head height while its animation says it is crouched -
        /// is a visible physics artefact. Erring towards full behaviour is the only safe direction here.
        /// </summary>
        private static int PoseFlags(CharacterData data)
        {
            int flags = data.isCrouching ? 1 : 0;
            if (data.isSprinting)
                flags |= 2;
            return flags;
        }

        /// <summary>
        /// The same fallback list as <see cref="ShouldDowngrade"/> with the distance term removed, for
        /// callers that reduce - rather than remove - per-tick animation work and therefore have to
        /// keep helping when the whole lobby is standing in one spot. Cached once per physics tick per
        /// character, because it is asked from a per-character FixedUpdate.
        ///
        /// The item-identity check exists for the same reason as in <see cref="ShouldDowngrade"/>:
        /// CharacterItems.Equip writes data.currentItem and then drives
        /// CharacterRagdoll.FixedUpdate()/SnapToAnimation() manually in the same frame, before
        /// data.sinceItemAttach is reset (CharacterItems.cs:602-627). A cached "throttle" answer there
        /// could let a manual snap consume animation targets from an earlier tick.
        /// </summary>
        internal static bool ShouldThrottleAnimation(Character c)
        {
            ConditionalWeakTable<Character, State> states = _states;
            Func<CharacterData, UnityEngine.Object> grabJoint = _grabJoint;
            AccessTools.FieldRef<CharacterData, Character> grabbedPlayerRef = _grabbedPlayer;
            AccessTools.FieldRef<CharacterData, Character> grabbingPlayerRef = _grabbingPlayer;
            if (states == null || grabJoint == null || grabbedPlayerRef == null || grabbingPlayerRef == null)
                return false;
            if (c == null)
                return false;

            CharacterData data = c.data;
            Character local = Character.localCharacter;
            // Read the static property once; the fallback keeps the "never reduce the character the
            // camera is looking through" test meaningful even before a spec target exists.
            Character viewpointCharacter = Character.observedCharacter;
            if (viewpointCharacter == null)
                viewpointCharacter = local;
            if (data == null || local == null || viewpointCharacter == null)
                return false;

            State state = states.GetOrCreateValue(c);
            int itemId = data.currentItem != null ? data.currentItem.GetInstanceID() : 0;
            int poseFlags = PoseFlags(data);
            // Symmetrical with ShouldDowngrade's item/pose gates: a change in either invalidates the
            // cached answer for this tick. The pose gate matters less here than there (this lane keeps
            // evaluating the graph, just at a lower rate, so the heights are at most Interval-1 ticks
            // stale rather than frozen), but a crouch or sprint transition is exactly the moment where
            // a one-tick-old height is most visible, so it yields for one tick as well.
            if (state.ThrottleItemId != itemId || state.ThrottlePoseFlags != poseFlags)
            {
                state.ThrottleItemId = itemId;
                state.ThrottlePoseFlags = poseFlags;
                state.ThrottleResult = false;
                state.ThrottleTick = PhysicsTick();
                return false;
            }

            int tick = PhysicsTick();
            if (state.ThrottleTick == tick)
                return state.ThrottleResult;

            bool result = PassesFallbacks(
                c, data, local, viewpointCharacter, grabJoint, grabbedPlayerRef, grabbingPlayerRef);
            state.ThrottleTick = tick;
            state.ThrottleResult = result;
            return result;
        }

        /// <summary>
        /// Physics-step index, shared by every physics-phase patch in this lane so several of them
        /// cannot disagree about which step they are on.
        ///
        /// Computed from Time.fixedTimeAsDouble rather than Time.fixedTime. fixedTime accumulates from
        /// process start and never resets on a scene reload, so in a long session the float mantissa is
        /// no longer fine enough to separate two adjacent 0.02s steps, and any component that writes
        /// Time.fixedDeltaTime (UnityEngine.AdaptivePerformanceModule's AdaptivePhysics does) shifts the
        /// quotient as well. Either makes the floor repeat or skip a step index; a repeat costs one
        /// throttled tick (the cluster lane sees LastTick == tick and yields), a skip clears
        /// PendingDeltaTime and shows up as one very short animation stall. The double property
        /// (UnityEngine/Time.cs:63, unused by the game) has enough mantissa for any realistic session.
        ///
        /// The floor is taken in double and only the final integer is narrowed - narrowing the quotient
        /// to float first would have thrown away exactly the precision this is here for, since a float
        /// stops representing consecutive integers above 2^24 (~93 hours at 50Hz). The int itself wraps
        /// after 2^31 steps, about 497 days of continuous play at 50Hz, which is well past any session.
        /// A zero or non-finite fixed step would make the quotient non-finite and the narrowing
        /// implementation defined; that is unchanged from the previous float expression, is not
        /// reachable while physics is stepping, and the one lane that could act on such a value
        /// (RemoteClusterAnimationThrottle) validates Time.fixedDeltaTime itself before asking for the
        /// tick.
        ///
        /// Pure computation: it reads two Unity statics and touches none of this class's fields, so it
        /// is safe to call from a patch that has not called <see cref="Acquire"/>.
        /// </summary>
        internal static int PhysicsTick() =>
            (int)Math.Floor(Time.fixedTimeAsDouble / Time.fixedDeltaTime);

        /// <summary>
        /// Maps a Unity instance id to a phase in [0, interval).
        ///
        /// Why not `id % interval`: Unity hands out instance ids in a dense run to objects created in
        /// the same batch, and a character rig is one such batch, so with interval 2 the low bit is
        /// very often the same for every remote character in the lobby. All of them would then evaluate
        /// on the same tick, which inverts the point of having a phase at all - the flat cost this is
        /// supposed to spread becomes a spike every interval ticks. One xorshift-multiply finalizer
        /// round decorrelates the low bits from the allocation order before the modulo, which is enough
        /// for the two small intervals in use (2 and 3) and costs a handful of integer ops.
        ///
        /// Properties preserved: the phase is a pure function of the id, so it is stable for the
        /// object's lifetime (a phase that flipped between ticks would defeat the throttle entirely);
        /// there is no allocation, which matters because this runs per character - or per bodypart -
        /// per physics step; and it cannot overflow. The sign bit is masked off before the modulo, so
        /// the result is already non-negative and the int.MinValue trap that made the original write
        /// `Math.Abs(x % interval)` instead of `Math.Abs(x) % interval` cannot arise. unchecked keeps
        /// the multiplies from throwing if the assembly is ever built with /checked+.
        /// </summary>
        internal static int Phase(int instanceId, int interval)
        {
            unchecked
            {
                uint x = (uint)instanceId;
                x ^= x >> 16;
                x *= 0x7feb352du;
                x ^= x >> 15;
                x *= 0x846ca68bu;
                x ^= x >> 16;
                return (int)(x & 0x7fffffffu) % interval;
            }
        }

        /// <summary>
        /// Fallback list plus distance hysteresis. Returns true only when this remote character is
        /// far away and in a plain, fully controlled, non-interacting state.
        /// </summary>
        private static bool Evaluate(
            Character c,
            CharacterData data,
            Character local,
            Character viewpointCharacter,
            Func<CharacterData, UnityEngine.Object> grabJoint,
            AccessTools.FieldRef<CharacterData, Character> grabbedPlayerRef,
            AccessTools.FieldRef<CharacterData, Character> grabbingPlayerRef,
            int tier)
        {
            if (!PassesFallbacks(c, data, local, viewpointCharacter, grabJoint, grabbedPlayerRef, grabbingPlayerRef))
                return false;

            // Measured from the viewpoint, not from the local character: see the comment on the two
            // locals in ShouldDowngrade.
            if (!TryGetCenter(viewpointCharacter, out Vector3 viewpointCenter) ||
                !TryGetCenter(c, out Vector3 center))
                return false;
            float distance = Vector3.Distance(viewpointCenter, center);
            if (float.IsNaN(distance))
                return false;

            // Hysteresis: entering the passive tier needs distance >= enter, leaving it needs
            // distance < exit, and ExitDistance is strictly below EnterDistance.
            return tier == TierPassiveRagdoll ? !(distance < ExitDistance) : distance >= EnterDistance;
        }

        /// <summary>
        /// Everything except the distance term: a remote, player-controlled, fully controlled,
        /// non-interacting character. Any hit means "never reduce anything for this character".
        /// Ordered cheapest first.
        ///
        /// <paramref name="local"/> and <paramref name="viewpointCharacter"/> are not
        /// interchangeable: the identity tests below ask "is this my own character", which is a
        /// question about ownership, so they use <paramref name="local"/> even while spectating. Only
        /// the observed-character test and the distance term in <see cref="Evaluate"/> are about where
        /// the camera is.
        /// </summary>
        private static bool PassesFallbacks(
            Character c,
            CharacterData data,
            Character local,
            Character viewpointCharacter,
            Func<CharacterData, UnityEngine.Object> grabJoint,
            AccessTools.FieldRef<CharacterData, Character> grabbedPlayerRef,
            AccessTools.FieldRef<CharacterData, Character> grabbingPlayerRef)
        {
            // Fallback list. Any hit means "never downgrade"; the character keeps full vanilla
            // behaviour. Ordered cheapest first.
            if (c == local)
                return false;
            PhotonView view = c.photonView;
            if (view == null || view.IsMine)
                return false;
            // Character.IsPlayerControlled is !isBot && !isZombie && !isScoutmaster
            // (Character.cs:187-197), so this one test excludes bots, mushroom zombies and the
            // scoutmaster in a single read. All three are AI driven rather than input driven, so
            // their animation state is deliberately left fully intact.
            if (!c.IsPlayerControlled)
                return false;
            if (c.warping)
                return false;
            if (data.carrier != null || data.carriedPlayer != null)
                return false;
            if (data.isCarried || grabbedPlayerRef(data) != null || grabbingPlayerRef(data) != null)
                return false;
            if (data.dead || data.fullyPassedOut)
                return false;
            if (grabJoint(data) != null)
                return false;
            if (data.isClimbing || data.isRopeClimbing || data.isVineClimbing)
                return false;
            if (data.currentClimbHandle != null || data.isReaching || data.isKicking)
                return false;
            if (data.isKinecmatic)
                return false;
            // Equip window: FixedUpdate and SnapToAnimation are driven manually there.
            if (data.sinceItemAttach < ItemAttachRecencySeconds)
                return false;
            // Written as >= so that a NaN control value also falls back.
            if (!(data.currentRagdollControll >= FullRagdollControlThreshold))
                return false;
            // Uses the local already read by the caller rather than reading the static property a
            // second time, so the identity test and the distance origin cannot disagree within one
            // evaluation.
            if (viewpointCharacter == c)
                return false;
            return true;
        }

        /// <summary>
        /// Same value as Character.Center (Character.cs:266) without its throwing path:
        /// Character.Center calls GetBodypart, which indexes CharacterRagdoll.partDict directly and
        /// raises KeyNotFoundException before the ragdoll has registered its parts.
        /// </summary>
        private static bool TryGetCenter(Character c, out Vector3 center)
        {
            center = Vector3.zero;
            Character.CharacterRefs refs = c.refs;
            CharacterRagdoll ragdoll = refs != null ? refs.ragdoll : null;
            if (ragdoll == null)
                return false;
            Dictionary<BodypartType, Bodypart> parts = ragdoll.partDict;
            if (parts == null || !parts.TryGetValue(BodypartType.Torso, out Bodypart torso) || torso == null)
                return false;
            Transform torsoTransform = torso.transform;
            if (torsoTransform == null)
                return false;
            center = torsoTransform.position;
            return true;
        }

        /// <summary>
        /// Exact structure check for every member the decision reads, plus the four Bodypart calls
        /// that must stay in the physics loop for the safety argument above to hold. Anything that
        /// no longer matches throws, and the installer leaves the patch uninstalled.
        /// </summary>
        private static void ValidateStructure()
        {
            ScaleValidation.RequireStaticField(typeof(Character), "localCharacter", typeof(Character));
            ScaleValidation.RequireStaticProperty(typeof(Character), "observedCharacter", typeof(Character));
            ScaleValidation.RequireInstanceField(typeof(Character), "data", typeof(CharacterData));
            ScaleValidation.RequireInstanceField(typeof(Character), "refs", typeof(Character.CharacterRefs));
            ScaleValidation.RequireInstanceField(typeof(Character), "isBot", typeof(bool));
            ScaleValidation.RequireInstanceField(typeof(Character), "isZombie", typeof(bool));
            ScaleValidation.RequireInstanceField(typeof(Character), "isScoutmaster", typeof(bool));
            ScaleValidation.RequireInstanceProperty(typeof(Character), "IsPlayerControlled", typeof(bool));
            ScaleValidation.RequireInstanceProperty(typeof(Character), "warping", typeof(bool));
            ScaleValidation.RequireInstanceProperty(typeof(Character), "Center", typeof(Vector3));
            ScaleValidation.RequireInheritedInstanceProperty(typeof(Character), "photonView", typeof(PhotonView));

            ScaleValidation.RequireInstanceField(
                typeof(Character.CharacterRefs), "ragdoll", typeof(CharacterRagdoll));
            ScaleValidation.RequireInstanceField(
                typeof(CharacterRagdoll), "partDict", typeof(Dictionary<BodypartType, Bodypart>));
            ScaleValidation.RequireInheritedInstanceProperty(typeof(Bodypart), "transform", typeof(Transform));
            if (!Enum.IsDefined(typeof(BodypartType), BodypartType.Torso))
                throw new InvalidOperationException("BodypartType.Torso is no longer defined");

            ScaleValidation.RequireInstanceField(typeof(CharacterData), "carrier", typeof(Character));
            ScaleValidation.RequireInstanceField(typeof(CharacterData), "carriedPlayer", typeof(Character));
            ScaleValidation.RequireInstanceField(typeof(CharacterData), "grabbedPlayer", typeof(Character));
            ScaleValidation.RequireInstanceField(typeof(CharacterData), "grabbingPlayer", typeof(Character));
            ScaleValidation.RequireInstanceField(typeof(CharacterData), "isCarried", typeof(bool));
            ScaleValidation.RequireInstanceField(typeof(CharacterData), "currentClimbHandle", typeof(ClimbHandle));
            ScaleValidation.RequireInstanceField(typeof(CharacterData), "isReaching", typeof(bool));
            ScaleValidation.RequireInstanceField(typeof(CharacterData), "isKicking", typeof(bool));
            ScaleValidation.RequireInstanceField(typeof(CharacterData), "fullyPassedOut", typeof(bool));
            ScaleValidation.RequireInstanceField(typeof(CharacterData), "isClimbing", typeof(bool));
            ScaleValidation.RequireInstanceField(typeof(CharacterData), "isRopeClimbing", typeof(bool));
            ScaleValidation.RequireInstanceField(typeof(CharacterData), "isVineClimbing", typeof(bool));
            ScaleValidation.RequireInstanceField(typeof(CharacterData), "isKinecmatic", typeof(bool));
            ScaleValidation.RequireInstanceField(typeof(CharacterData), "sinceItemAttach", typeof(float));
            // Pose flags: read by PoseFlags to force a downgraded character back to full behaviour
            // while its frozen stand heights are re-derived.
            ScaleValidation.RequireInstanceField(typeof(CharacterData), "isCrouching", typeof(bool));
            ScaleValidation.RequireInstanceField(typeof(CharacterData), "isSprinting", typeof(bool));
            ScaleValidation.RequireInstanceProperty(typeof(CharacterData), "currentItem", typeof(Item));
            ScaleValidation.RequireInstanceProperty(typeof(CharacterData), "dead", typeof(bool));
            ScaleValidation.RequireInstanceProperty(
                typeof(CharacterData), "currentRagdollControll", typeof(float));

            // Physics-step index source. Asserted so a build that no longer exposes the double
            // property fails closed here instead of silently reverting to the float precision the
            // tick index used to have.
            ScaleValidation.RequireStaticProperty(typeof(Time), "fixedTimeAsDouble", typeof(double));

            // Proof that the load-bearing part of the physics loop is still where the safety
            // argument assumes it is. None of these is patched or called from here.
            ScaleValidation.RequireInstanceMethod(
                typeof(CharacterMovement), "FixedUpdate", typeof(void), Type.EmptyTypes, false);
            ScaleValidation.RequireInstanceMethod(
                typeof(Bodypart), "Gravity", typeof(void), new[] { typeof(Vector3) }, false);
            ScaleValidation.RequireInstanceMethod(
                typeof(Bodypart), "ToggleUseGravity", typeof(void), new[] { typeof(bool) }, false);
            ScaleValidation.RequireInstanceMethod(
                typeof(Bodypart), "Drag", typeof(void), new[] { typeof(float), typeof(bool) }, false);
            ScaleValidation.RequireInstanceMethod(
                typeof(Bodypart), "ApplyForces", typeof(void), Type.EmptyTypes, false);

            // Proof of the warm-up rationale: these are the only producers/consumers of the
            // animation targets that a premature downgrade could leave unwritten, and the firstFrame
            // field is the branch that makes the very first physics step of a character produce no
            // targets at all (CharacterRagdoll.cs:153-158), which is why the warm-up has to span more
            // than one step.
            ScaleValidation.RequireInstanceField(typeof(CharacterRagdoll), "firstFrame", typeof(bool));
            ScaleValidation.RequireInstanceMethod(
                typeof(Bodypart), "SaveAnimationData", typeof(void), Type.EmptyTypes, true);
            ScaleValidation.RequireInstanceMethod(
                typeof(Bodypart), "SnapToAnim", typeof(void), Type.EmptyTypes, true);
            ScaleValidation.RequireInstanceMethod(
                typeof(CharacterRagdoll), "SnapToAnimation", typeof(void), Type.EmptyTypes, true);

            // Proof that the two consumers of the frozen stand heights really do run for remote
            // characters, which is what makes the crouch/sprint pose gate necessary (see PoseFlags).
            ScaleValidation.RequireInstanceField(typeof(CharacterData), "targetHeadHeight", typeof(float));
            ScaleValidation.RequireInstanceField(typeof(CharacterData), "targetHipHeight", typeof(float));
            ScaleValidation.RequireInstanceMethod(
                typeof(CharacterMovement), "Stand", typeof(void), Type.EmptyTypes, false);
            ScaleValidation.RequireInstanceMethod(
                typeof(CharacterMovement), "RaycastGroundCheck", typeof(void), Type.EmptyTypes, false);
            ScaleValidation.RequireInstanceMethod(
                typeof(CharacterRagdoll), "SaveAdditionalTransformPositions", typeof(void),
                Type.EmptyTypes, false);
        }
    }
}
