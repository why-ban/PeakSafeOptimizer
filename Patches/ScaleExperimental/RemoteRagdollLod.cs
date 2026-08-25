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
    /// zombies, the scoutmaster, the spectated character, warping, carried/carrying, grabbed,
    /// climbing, dead, fully passed out, kinematic and partially ragdolled characters are never
    /// downgraded.
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
        /// Frames a character must be seen at full behaviour before it may be downgraded for the
        /// first time. This exists for correctness, not for smoothing: Bodypart.SaveAnimationData
        /// (the producer of targetOffsetRelativeToHip / targetForward / targetUp) only runs inside
        /// CharacterRagdoll.FixedUpdate. A character that is already beyond the enter distance on its
        /// very first evaluated frame would otherwise be downgraded before those targets were ever
        /// written, and any later Bodypart.SnapToAnim - which CharacterRagdoll.SnapToAnimation calls
        /// directly from the item-equip coroutine (CharacterItems.cs:613-627) - would then snap every
        /// bodypart onto the hip, because WorldTargetPos() = hip + Vector3.zero. The warm-up
        /// guarantees the animation targets exist before any downgrade.
        /// </summary>
        private const int WarmupFrames = 5;
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
            /// <summary>Distinct frames seen at full behaviour; capped at <see cref="WarmupFrames"/>.</summary>
            internal int FullFrames;

            /// <summary>
            /// Separate per-physics-tick cache for <see cref="ShouldThrottleAnimation"/>. It uses its
            /// own fields so the distance-tier state machine above cannot be perturbed by a caller
            /// that does not participate in the hysteresis.
            /// </summary>
            internal int ThrottleTick = int.MinValue;
            internal bool ThrottleResult;
            internal int ThrottleItemId = int.MinValue;
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
            Character.CharacterRefs refs = c.refs;
            Character local = Character.localCharacter;
            if (data == null || refs == null || local == null)
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
                state.FullFrames = 0;
                state.LastResult = false;
                state.LastFrame = frame;
                return false;
            }

            // One evaluation per character per rendered frame. Animate/AddMovementForce are called
            // from physics while CharacterAnimations.Update is rendered; sharing this decision
            // avoids contradictory transitions between those call families.
            if (state.LastFrame == frame)
                return state.LastResult;

            bool warmed = state.FullFrames >= WarmupFrames;
            bool result = warmed && Evaluate(c, data, local, grabJoint, grabbedPlayerRef, grabbingPlayerRef, state.Tier);
            if (!warmed)
                state.FullFrames++;
            // A fallback hit or a near distance both mean "full behaviour", so re-entry always has
            // to cross the enter distance again.
            state.Tier = result ? TierPassiveRagdoll : TierFull;
            state.LastFrame = frame;
            state.LastResult = result;
            return result;
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
            if (data == null || local == null)
                return false;

            State state = states.GetOrCreateValue(c);
            int itemId = data.currentItem != null ? data.currentItem.GetInstanceID() : 0;
            if (state.ThrottleItemId != itemId)
            {
                state.ThrottleItemId = itemId;
                state.ThrottleResult = false;
                state.ThrottleTick = PhysicsTick();
                return false;
            }

            int tick = PhysicsTick();
            if (state.ThrottleTick == tick)
                return state.ThrottleResult;

            bool result = PassesFallbacks(c, data, local, grabJoint, grabbedPlayerRef, grabbingPlayerRef);
            state.ThrottleTick = tick;
            state.ThrottleResult = result;
            return result;
        }

        /// <summary>
        /// Physics-step index, matching the expression used by the other physics-phase patches so
        /// several of them cannot disagree about which step they are on.
        /// </summary>
        internal static int PhysicsTick() => Mathf.FloorToInt(Time.fixedTime / Time.fixedDeltaTime);

        /// <summary>
        /// Fallback list plus distance hysteresis. Returns true only when this remote character is
        /// far away and in a plain, fully controlled, non-interacting state.
        /// </summary>
        private static bool Evaluate(
            Character c,
            CharacterData data,
            Character local,
            Func<CharacterData, UnityEngine.Object> grabJoint,
            AccessTools.FieldRef<CharacterData, Character> grabbedPlayerRef,
            AccessTools.FieldRef<CharacterData, Character> grabbingPlayerRef,
            int tier)
        {
            if (!PassesFallbacks(c, data, local, grabJoint, grabbedPlayerRef, grabbingPlayerRef))
                return false;

            if (!TryGetCenter(local, out Vector3 localCenter) || !TryGetCenter(c, out Vector3 center))
                return false;
            float distance = Vector3.Distance(localCenter, center);
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
        /// </summary>
        private static bool PassesFallbacks(
            Character c,
            CharacterData data,
            Character local,
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
            if (Character.observedCharacter == c)
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
            ScaleValidation.RequireInstanceProperty(typeof(CharacterData), "currentItem", typeof(Item));
            ScaleValidation.RequireInstanceProperty(typeof(CharacterData), "dead", typeof(bool));
            ScaleValidation.RequireInstanceProperty(
                typeof(CharacterData), "currentRagdollControll", typeof(float));

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
            // animation targets that a premature downgrade could leave unwritten.
            ScaleValidation.RequireInstanceMethod(
                typeof(Bodypart), "SaveAnimationData", typeof(void), Type.EmptyTypes, true);
            ScaleValidation.RequireInstanceMethod(
                typeof(Bodypart), "SnapToAnim", typeof(void), Type.EmptyTypes, true);
            ScaleValidation.RequireInstanceMethod(
                typeof(CharacterRagdoll), "SnapToAnimation", typeof(void), Type.EmptyTypes, true);
        }
    }
}
