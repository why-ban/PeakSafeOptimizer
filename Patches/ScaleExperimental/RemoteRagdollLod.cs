using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using PeakSafeOptimizer.Patches.Scale;
using Photon.Pun;
using UnityEngine;

namespace PeakSafeOptimizer.Patches.ScaleExperimental
{
    /// <summary>
    /// Distance tier decision shared by the four RemoteRagdollLod patches. Skipping the work it selects
    /// removes only animation: CharacterMovement.FixedUpdate still runs Gravity, ToggleUseGravity, Drag and
    /// ApplyForces on every part. SetPhysicsMats is not skipped work, because its only call site is gated on
    /// character.IsLocal. Distances are measured from Character.observedCharacter, not from the character
    /// this client owns.
    /// </summary>
    internal static class RemoteRagdollLod
    {
        private const int TierFull = 0;
        private const int TierPassiveRagdoll = 2;

        private const float EnterDistance = 60f;
        private const float ExitDistance = 50f;
        private const float FullRagdollControlThreshold = 0.9f;
        /// <summary>
        /// Physics steps at full behaviour before a downgrade, re-armed on every item or pose change. The
        /// animation targets must exist first: Bodypart.SaveAnimationData only runs in
        /// CharacterRagdoll.FixedUpdate, and a later SnapToAnim would otherwise snap every part onto the hip
        /// </summary>
        private const int WarmupPhysicsSteps = 15;
        private const float ItemAttachRecencySeconds = 0.5f;
        private const string FixedJointTypeName = "UnityEngine.FixedJoint";

        /// <summary>
        /// Per-character tier plus a one-frame decision cache. Never references its ConditionalWeakTable key
        /// </summary>
        private sealed class State
        {
            internal int Tier;
            internal int LastFrame = int.MinValue;
            internal bool LastResult;
            internal int LastItemId = int.MinValue;
            internal int LastPoseFlags = int.MinValue;
            internal int FullSteps;
            internal int LastWarmupTick = int.MinValue;

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
        /// Called by each patch's Reset, which the installer runs only after that patch's Harmony methods
        /// are detached
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
                // No diagnostics: reached once per bodypart per physics step
                return false;
            }
            if (c == null)
                return false;

            CharacterData data = c.data;
            Character local = Character.localCharacter;
            // local answers "is this my character" for the identity tests and stays the owned character
            // even while spectating; only the distance origin follows the camera
            if (data == null || local == null)
                return false;

            int frame = Time.frameCount;
            if (!states.TryGetValue(c, out State state))
                state = states.GetOrCreateValue(c);
            int itemId = data.currentItem != null ? data.currentItem.GetInstanceID() : 0;
            // Checked before the per-frame cache: CharacterItems.Equip drives ragdoll.FixedUpdate and
            // SnapToAnimation manually in the same frame, before sinceItemAttach is reset
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

            if (state.LastFrame == frame)
                return state.LastResult;

            bool warmed = state.FullSteps >= WarmupPhysicsSteps;
            bool result = false;
            if (warmed)
            {
                Character viewpointCharacter = Character.observedCharacter;
                if (viewpointCharacter == null)
                    viewpointCharacter = local;
                result = Evaluate(
                    c, data, local, viewpointCharacter, grabJoint, grabbedPlayerRef, grabbingPlayerRef,
                    state.Tier);
            }
            else
            {
                int warmupTick = PhysicsTick();
                if (state.LastWarmupTick != warmupTick)
                {
                    state.LastWarmupTick = warmupTick;
                    state.FullSteps++;
                }
            }
            // A fallback hit or a near distance both mean full behaviour, so re-entry has to cross the
            // enter distance again
            state.Tier = result ? TierPassiveRagdoll : TierFull;
            state.LastFrame = frame;
            state.LastResult = result;
            return result;
        }

        /// <summary>
        /// Crouch and sprint packed into one int. A change forces full behaviour because the two stand
        /// heights freeze while downgraded, and both consumers, CharacterMovement.Stand and
        /// RaycastGroundCheck, run for every character on this client
        /// </summary>
        private static int PoseFlags(CharacterData data)
        {
            int flags = data.isCrouching ? 1 : 0;
            if (data.isSprinting)
                flags |= 2;
            return flags;
        }

        /// <summary>
        /// The fallback list with the distance term removed, for a caller that reduces rather than removes
        /// per-tick animation work, so it keeps helping when the whole lobby stands in one spot. Cached once
        /// per physics tick per character
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
            Character viewpointCharacter = Character.observedCharacter;
            if (viewpointCharacter == null)
                viewpointCharacter = local;
            if (data == null || local == null || viewpointCharacter == null)
                return false;

            if (!states.TryGetValue(c, out State state))
                state = states.GetOrCreateValue(c);
            int itemId = data.currentItem != null ? data.currentItem.GetInstanceID() : 0;
            int poseFlags = PoseFlags(data);
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
        /// Physics-step index, shared so no two lanes disagree about which step they are on. A zero or
        /// non-finite fixed step freezes the narrowed index, so every caller has to reject such a step
        /// itself; that is not screened out here
        /// </summary>
        internal static int PhysicsTick() =>
            (int)Math.Floor(Time.fixedTimeAsDouble / Time.fixedDeltaTime);

        /// <summary>
        /// Maps a Unity instance id to a phase in [0, interval). Not `id % interval`, because Unity hands
        /// out ids in dense runs and a rig's parts would share a phase
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

            if (!TryGetCenter(viewpointCharacter, out Vector3 viewpointCenter) ||
                !TryGetCenter(c, out Vector3 center))
                return false;
            float distance = Vector3.Distance(viewpointCenter, center);
            if (float.IsNaN(distance))
                return false;

            return tier == TierPassiveRagdoll ? !(distance < ExitDistance) : distance >= EnterDistance;
        }

        /// <summary>
        /// Everything except the distance and pose terms. This is the single definition of the fallback
        /// list, reached from Evaluate and ShouldThrottleAnimation, so a distance or pose term belongs in
        /// the callers. No diagnostics: it runs once per character per physics step
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
            if (c == local)
                return false;
            PhotonView view = c.photonView;
            if (view == null || view.IsMine)
                return false;
            if (!c.IsPlayerControlled)
                return false;
            if (c.warping)
                return false;
            if (data.carrier != null || data.carriedPlayer != null)
                return false;
            if (data.carrierCondor != null)
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
            if (data.sinceItemAttach < ItemAttachRecencySeconds)
                return false;
            // Written as >= so that a NaN control value also falls back
            if (!(data.currentRagdollControll >= FullRagdollControlThreshold))
                return false;
            if (viewpointCharacter == c)
                return false;
            return true;
        }

        /// <summary>
        /// Same value as Character.Center without its KeyNotFoundException, which GetBodypart raises
        /// before the ragdoll has registered its parts
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
            ScaleValidation.RequireInstanceField(typeof(CharacterData), "carrierCondor", typeof(Condor));
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
            ScaleValidation.RequireInstanceField(typeof(CharacterData), "isCrouching", typeof(bool));
            ScaleValidation.RequireInstanceField(typeof(CharacterData), "isSprinting", typeof(bool));
            ScaleValidation.RequireInstanceProperty(typeof(CharacterData), "currentItem", typeof(Item));
            ScaleValidation.RequireInstanceProperty(typeof(CharacterData), "dead", typeof(bool));
            ScaleValidation.RequireInstanceProperty(
                typeof(CharacterData), "currentRagdollControll", typeof(float));

            ScaleValidation.RequireStaticProperty(typeof(Time), "fixedTimeAsDouble", typeof(double));

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

            ScaleValidation.RequireInstanceField(typeof(CharacterRagdoll), "firstFrame", typeof(bool));
            ScaleValidation.RequireInstanceMethod(
                typeof(Bodypart), "SaveAnimationData", typeof(void), Type.EmptyTypes, true);
            ScaleValidation.RequireInstanceMethod(
                typeof(Bodypart), "SnapToAnim", typeof(void), Type.EmptyTypes, true);
            ScaleValidation.RequireInstanceMethod(
                typeof(CharacterRagdoll), "SnapToAnimation", typeof(void), Type.EmptyTypes, true);

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
