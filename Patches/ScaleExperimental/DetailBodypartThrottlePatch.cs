using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using BepInEx.Configuration;
using HarmonyLib;
using PeakSafeOptimizer.Config;
using PeakSafeOptimizer.Patches.Scale;
using Photon.Pun;
using UnityEngine;

namespace PeakSafeOptimizer.Patches.ScaleExperimental
{
    /// <summary>
    /// Opt-in: runs Bodypart.Animate(float, float) (audited source Bodypart.cs:143-153) only every
    /// Nth rendered frame for the *detail* bodyparts (fingers, jiggle bones, toes, mouth/jaw) of a
    /// remote, player-controlled, fully controlled and non-interacting character. Every other frame
    /// the Prefix returns false and that one part is left to swing passively for a step.
    ///
    /// Why this exists next to <see cref="RemoteRagdollLod"/>: that helper is distance gated, so in
    /// the worst case for host physics cost - eight players standing on top of each other - it never
    /// triggers at all. This patch has no distance term, so it is the only lever that still reduces
    /// remote ragdoll cost in a tight cluster. The count is the whole point: 59 of the BodypartType
    /// values are listed as detail parts below, the fingers and jiggle bones among them dominate the
    /// part count of a player rig, and Animate is the most expensive of the six calls
    /// CharacterMovement.FixedUpdate makes per part per physics step (CharacterMovement.cs:304-315).
    ///
    /// Only Animate is throttled. Gravity, ToggleUseGravity, Drag and ApplyForces keep running for
    /// every part on every physics step, which is what makes the skipped step passive rather than
    /// broken:
    ///   Gravity          -> gravity keeps being accumulated, so a detail part cannot float.
    ///   ToggleUseGravity -> Rigidbody.useGravity keeps tracking currentRagdollControll.
    ///   Drag             -> per-step velocity damping still runs, so velocity converges.
    ///   ApplyForces      -> forcesToAdd is flushed and zeroed every step (Bodypart.cs:281-288),
    ///                       so nothing can accumulate into an explosive impulse.
    /// Animate itself only writes joint.targetRotation and adds torque/force towards the animated
    /// pose (Bodypart.cs:150-152 -> FollowRotation_Joint / _Rotation / _Position), so skipping a
    /// step strictly removes energy from the ragdoll and cannot introduce a NaN: this patch performs
    /// no force, torque or transform write of its own.
    ///
    /// Joint parenting is unaffected. The ConfigurableJoint and Bodypart.jointParent are established
    /// once in Bodypart.Start (Bodypart.cs:98-102) from the prefab rig; Animate only updates an
    /// existing joint's drive target, so a skipped step leaves the joint - and PhysX' constraint
    /// solving of the whole chain - exactly as it was, just without a new target for that step.
    ///
    /// The animation targets Animate consumes (targetRotation / targetForward / targetUp /
    /// targetOffsetRelativeToHip) keep being produced every physics step by
    /// CharacterRagdoll.FixedUpdate -> Bodypart.SaveAnimationData (CharacterRagdoll.cs:164-167),
    /// which this patch does not touch. There is therefore no warm-up requirement here: on an
    /// execute frame the part is driven towards a fresh pose, and any Bodypart.SnapToAnim triggered
    /// elsewhere reads targets that were written on that same step.
    /// </summary>
    internal static class DetailBodypartThrottlePatch
    {
        private const int DefaultInterval = 3;
        /// <summary>
        /// Upper clamp for the configured interval. A larger value would leave fingers and jaws
        /// visibly frozen instead of merely lagging, which is not a trade this patch offers.
        /// </summary>
        private const int MaxInterval = 10;
        private const float FullRagdollControlThreshold = 0.9f;
        /// <summary>
        /// Recency window that keeps a character unthrottled around an item equip, where
        /// CharacterRagdoll.FixedUpdate and SnapToAnimation are driven manually from the equip
        /// coroutine rather than by Unity (CharacterItems.cs:613-627 resets data.sinceItemAttach on
        /// every iteration of that loop).
        /// </summary>
        private const float ItemAttachRecencySeconds = 0.5f;
        private const string FixedJointTypeName = "UnityEngine.FixedJoint";

        /// <summary>
        /// Detail parts: high joint count, low visual weight. Verified against the audited
        /// BodypartType enum (BodypartType.cs) and re-verified with Enum.IsDefined in Initialize.
        /// 30 subdivided fingers + 2 legacy fingers + 20 jiggle bones + 2 toes + 5 mouth/jaw = 59.
        /// </summary>
        private static readonly string[] DetailPartNames =
        {
            "Finger_1_1_R", "Finger_1_2_R", "Finger_1_3_R",
            "Finger_2_1_R", "Finger_2_2_R", "Finger_2_3_R",
            "Finger_3_1_R", "Finger_3_2_R", "Finger_3_3_R",
            "Finger_4_1_R", "Finger_4_2_R", "Finger_4_3_R",
            "Finger_5_1_R", "Finger_5_2_R", "Finger_5_3_R",
            "Finger_1_1_L", "Finger_1_2_L", "Finger_1_3_L",
            "Finger_2_1_L", "Finger_2_2_L", "Finger_2_3_L",
            "Finger_3_1_L", "Finger_3_2_L", "Finger_3_3_L",
            "Finger_4_1_L", "Finger_4_2_L", "Finger_4_3_L",
            "Finger_5_1_L", "Finger_5_2_L", "Finger_5_3_L",
            "Finger_L", "Finger_R",
            "Jiggle_1_L", "Jiggle_1_R", "Jiggle_2_L", "Jiggle_2_R",
            "Jiggle_3_L", "Jiggle_3_R", "Jiggle_4_L", "Jiggle_4_R",
            "Jiggle_5_L", "Jiggle_5_R", "Jiggle_6_L", "Jiggle_6_R",
            "Jiggle_7_L", "Jiggle_7_R", "Jiggle_8_L", "Jiggle_8_R",
            "Jiggle_9_L", "Jiggle_9_R", "Jiggle_10_L", "Jiggle_10_R",
            "Toe_L", "Toe_R",
            "Mouth", "Jaw_U", "Jaw_D", "Jaw_L", "Jaw_R"
        };

        /// <summary>
        /// Parts that must keep full per-step fidelity. Initialize proves each one exists and is not
        /// in the throttle table, so a typo or an enum reshuffle can never silently downgrade the
        /// spine, the limbs or the held item. The monster-only chains (Leg_*, Knee_*, Foot_*,
        /// Spine_*, Tail_*, Extra_*) are covered by the representative entries below; none of them is
        /// listed as a detail part.
        /// </summary>
        private static readonly string[] ProtectedPartNames =
        {
            "Hip", "Mid", "Torso", "Neck", "Head",
            "Arm_L", "Elbow_L", "Hand_L", "Arm_R", "Elbow_R", "Hand_R",
            "Leg_L", "Knee_L", "Foot_L", "Leg_R", "Knee_R", "Foot_R",
            "Hip_L", "Hip_R", "Shoulder_L", "Shoulder_R", "Item",
            "Leg_1_L", "Knee_1_L", "Foot_1_L", "Spine_1", "Spine_10", "Tail_1", "Extra_1"
        };

        /// <summary>
        /// Per-character one-frame decision cache. Animate is called once per part per physics step,
        /// so without this the fallback list would be re-evaluated dozens of times per character per
        /// frame. Never references its ConditionalWeakTable key.
        /// </summary>
        private sealed class State
        {
            internal int LastFrame = int.MinValue;
            internal bool LastResult;
        }

        /// <summary>
        /// Throttle lookup indexed by (int)BodypartType, built once in Initialize. A plain bool[]
        /// read replaces any per-call switch or HashSet lookup.
        /// </summary>
        private static bool[] _detailParts;
        private static AccessTools.FieldRef<Bodypart, BodypartType> _partType;
        private static AccessTools.FieldRef<Bodypart, Character> _bodypartCharacter;
        private static Func<CharacterData, UnityEngine.Object> _grabJoint;
        private static ConditionalWeakTable<Character, State> _states;

        internal static void Initialize()
        {
            ResolveAndValidateTarget();
            ValidateStructure();

            FieldInfo partTypeField = ScaleValidation.RequireInstanceField(
                typeof(Bodypart), "partType", typeof(BodypartType));
            FieldInfo bodypartCharacterField = ScaleValidation.RequireInstanceField(
                typeof(Bodypart), "character", typeof(Character));
            FieldInfo grabJointField = ScaleValidation.RequireInstanceFieldByTypeName(
                typeof(CharacterData), "grabJoint", FixedJointTypeName);

            bool[] table = BuildDetailTable();
            AccessTools.FieldRef<Bodypart, BodypartType> partType =
                AccessTools.FieldRefAccess<Bodypart, BodypartType>(partTypeField);
            AccessTools.FieldRef<Bodypart, Character> bodypartCharacter =
                AccessTools.FieldRefAccess<Bodypart, Character>(bodypartCharacterField);
            Func<CharacterData, UnityEngine.Object> grabJoint =
                ScaleEmit.CreateUnityObjectFieldGetter<CharacterData>(grabJointField);
            if (partType == null || bodypartCharacter == null || grabJoint == null)
                throw new InvalidOperationException("DetailBodypartThrottle accessor creation failed");

            _states = new ConditionalWeakTable<Character, State>();
            _partType = partType;
            _bodypartCharacter = bodypartCharacter;
            _grabJoint = grabJoint;
            // Published last: the Prefix treats a null table as "run the original", so it stays a
            // no-op until every accessor above exists.
            _detailParts = table;
        }

        internal static MethodInfo ResolveAndValidateTarget()
        {
            return ScaleValidation.RequireInstanceMethod(
                typeof(Bodypart),
                "Animate",
                typeof(void),
                new[] { typeof(float), typeof(float) },
                requirePublic: false);
        }

        internal static void Reset()
        {
            _detailParts = null;
            _partType = null;
            _bodypartCharacter = null;
            _grabJoint = null;
            _states = null;
        }

        /// <summary>
        /// Hot path: called once per bodypart per physics step for every character in the level, so
        /// it must stay allocation free and must not log. RateLimitedDiagnostics is deliberately not
        /// used anywhere in here - every early-out below (missing accessor, unresolved owner,
        /// non-detail part) is reachable on every single call, so a Hit would cost more than the work
        /// this patch removes. Initialize/Reset failures are already reported by the installer.
        /// </summary>
        internal static bool Prefix(Bodypart __instance)
        {
            bool[] table = _detailParts;
            AccessTools.FieldRef<Bodypart, BodypartType> partTypeRef = _partType;
            if (table == null || partTypeRef == null || __instance == null)
                return true;

            int interval = ResolveInterval();
            if (interval <= 1)
                return true;

            int index = (int)partTypeRef(__instance);
            if ((uint)index >= (uint)table.Length || !table[index])
                return true;

            // Stable per-part phase so the detail parts of a character do not all execute on the
            // same frame, which would trade a flat cost for a periodic spike. GetInstanceID is
            // stable for the lifetime of the part; the modulo is taken before Math.Abs so
            // int.MinValue cannot overflow it.
            int phase = Math.Abs(__instance.GetInstanceID() % interval);
            // Time.frameCount, not a physics-step counter: there is no per-physics-step anchor this
            // patch could hook without adding exactly the kind of per-part work it is removing. The
            // consequence is intentional - when FixedUpdate runs several physics steps inside one
            // rendered frame, all of those steps share this decision, so the throttle is "every Nth
            // rendered frame" rather than "every Nth physics step". That is the more stable of the
            // two: the executed steps stay evenly spread over wall-clock time even while the physics
            // step count per frame fluctuates.
            if ((Time.frameCount + phase) % interval == 0)
                return true;

            AccessTools.FieldRef<Bodypart, Character> characterRef = _bodypartCharacter;
            if (characterRef == null)
                return true;
            // Bodypart.character is assigned in Bodypart.Start (Bodypart.cs:97); before that runs,
            // or on a stray part with no Character parent, the original must run unchanged.
            Character owner = characterRef(__instance);
            if (owner == null)
                return true;
            return !ShouldThrottle(owner);
        }

        /// <summary>
        /// Runtime robustness for a hand-edited config: 1 or less disables the throttle entirely and
        /// anything above <see cref="MaxInterval"/> is clamped, so no value can freeze a detail part.
        /// </summary>
        private static int ResolveInterval()
        {
            ConfigEntry<int> entry = OptimizerConfig.DetailBodypartThrottleInterval;
            int interval = entry != null ? entry.Value : DefaultInterval;
            if (interval <= 1)
                return 1;
            return interval > MaxInterval ? MaxInterval : interval;
        }

        private static bool ShouldThrottle(Character c)
        {
            ConditionalWeakTable<Character, State> states = _states;
            Func<CharacterData, UnityEngine.Object> grabJoint = _grabJoint;
            if (states == null || grabJoint == null)
                return false;

            int frame = Time.frameCount;
            bool hasState = states.TryGetValue(c, out State state);
            if (hasState && state.LastFrame == frame)
                return state.LastResult;

            bool result = Evaluate(c, grabJoint);
            if (!hasState)
                state = states.GetOrCreateValue(c);
            state.LastFrame = frame;
            state.LastResult = result;
            return result;
        }

        /// <summary>
        /// The RemoteRagdollLod fallback list with the distance term removed. Any hit means "never
        /// throttle": that character keeps full vanilla behaviour on every part. Ordered cheapest
        /// first. Every member read here is proven to exist by <see cref="ValidateStructure"/>.
        /// </summary>
        private static bool Evaluate(Character c, Func<CharacterData, UnityEngine.Object> grabJoint)
        {
            CharacterData data = c.data;
            Character local = Character.localCharacter;
            if (data == null || local == null)
                return false;
            if (c == local)
                return false;
            PhotonView view = c.photonView;
            if (view == null || view.IsMine)
                return false;
            // Character.IsPlayerControlled is !isBot && !isZombie && !isScoutmaster
            // (Character.cs:187-197): one read excludes bots, mushroom zombies and the scoutmaster,
            // whose AI-driven animation state is deliberately left fully intact.
            if (!c.IsPlayerControlled)
                return false;
            if (c.warping)
                return false;
            if (data.carrier != null || data.carriedPlayer != null)
                return false;
            if (data.dead || data.fullyPassedOut)
                return false;
            if (grabJoint(data) != null)
                return false;
            if (data.isClimbing || data.isRopeClimbing || data.isVineClimbing)
                return false;
            // The kinematic branch of Animate is SnapToAnim (Bodypart.cs:145-149), and
            // CharacterRagdoll.ToggleKinematic sets data.isKinecmatic together with every part's
            // Rigidbody.isKinematic (CharacterRagdoll.cs:80-87), so excluding it here means a
            // throttled step can never be a skipped snap.
            if (data.isKinecmatic)
                return false;
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
        /// Builds the (int)BodypartType indexed throttle table and proves the audited enum is still
        /// shaped the way the table assumes: every detail name defined exactly once, and every
        /// protected name defined and absent from the table.
        /// </summary>
        private static bool[] BuildDetailTable()
        {
            Type enumType = typeof(BodypartType);
            if (!enumType.IsEnum || Enum.GetUnderlyingType(enumType) != typeof(int))
                throw new InvalidOperationException("BodypartType is not an int-backed enum");

            Array values = Enum.GetValues(enumType);
            int max = -1;
            for (int i = 0; i < values.Length; i++)
            {
                int value = (int)values.GetValue(i);
                if (value > max)
                    max = value;
            }
            if (max < 0)
                throw new InvalidOperationException("BodypartType has no usable values");

            var table = new bool[max + 1];
            for (int i = 0; i < DetailPartNames.Length; i++)
            {
                int index = RequireDefined(enumType, DetailPartNames[i]);
                if ((uint)index >= (uint)table.Length)
                    throw new InvalidOperationException($"BodypartType.{DetailPartNames[i]} is out of table range");
                if (table[index])
                    throw new InvalidOperationException($"BodypartType.{DetailPartNames[i]} is listed twice");
                table[index] = true;
            }
            for (int i = 0; i < ProtectedPartNames.Length; i++)
            {
                int index = RequireDefined(enumType, ProtectedPartNames[i]);
                if ((uint)index < (uint)table.Length && table[index])
                    throw new InvalidOperationException($"BodypartType.{ProtectedPartNames[i]} must never be throttled");
            }
            return table;
        }

        private static int RequireDefined(Type enumType, string name)
        {
            if (!Enum.IsDefined(enumType, name))
                throw new InvalidOperationException($"BodypartType.{name} is no longer defined");
            return (int)(BodypartType)Enum.Parse(enumType, name);
        }

        /// <summary>
        /// Exact structure check for every member the decision reads, plus the calls the safety
        /// argument depends on: the four Bodypart calls that must stay in the physics loop, and the
        /// SaveAnimationData producer that must keep running for a throttled part to be driven
        /// towards a fresh pose on its execute frames. Anything that no longer matches throws and the
        /// installer leaves the patch uninstalled.
        /// </summary>
        private static void ValidateStructure()
        {
            ScaleValidation.RequireStaticField(typeof(Character), "localCharacter", typeof(Character));
            ScaleValidation.RequireStaticProperty(typeof(Character), "observedCharacter", typeof(Character));
            ScaleValidation.RequireInstanceField(typeof(Character), "data", typeof(CharacterData));
            ScaleValidation.RequireInstanceField(typeof(Character), "isBot", typeof(bool));
            ScaleValidation.RequireInstanceField(typeof(Character), "isZombie", typeof(bool));
            ScaleValidation.RequireInstanceField(typeof(Character), "isScoutmaster", typeof(bool));
            ScaleValidation.RequireInstanceProperty(typeof(Character), "IsPlayerControlled", typeof(bool));
            ScaleValidation.RequireInstanceProperty(typeof(Character), "warping", typeof(bool));
            ScaleValidation.RequireInheritedInstanceProperty(typeof(Character), "photonView", typeof(PhotonView));

            ScaleValidation.RequireInstanceField(typeof(CharacterData), "carrier", typeof(Character));
            ScaleValidation.RequireInstanceField(typeof(CharacterData), "carriedPlayer", typeof(Character));
            ScaleValidation.RequireInstanceField(typeof(CharacterData), "fullyPassedOut", typeof(bool));
            ScaleValidation.RequireInstanceField(typeof(CharacterData), "isClimbing", typeof(bool));
            ScaleValidation.RequireInstanceField(typeof(CharacterData), "isRopeClimbing", typeof(bool));
            ScaleValidation.RequireInstanceField(typeof(CharacterData), "isVineClimbing", typeof(bool));
            ScaleValidation.RequireInstanceField(typeof(CharacterData), "isKinecmatic", typeof(bool));
            ScaleValidation.RequireInstanceField(typeof(CharacterData), "sinceItemAttach", typeof(float));
            ScaleValidation.RequireInstanceProperty(typeof(CharacterData), "dead", typeof(bool));
            ScaleValidation.RequireInstanceProperty(
                typeof(CharacterData), "currentRagdollControll", typeof(float));

            // Proof that the load-bearing part of the physics loop is still where the safety argument
            // assumes it is (CharacterMovement.cs:304-315). None of these is patched or called here.
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

            // Proof that the animation targets a throttled part consumes on its execute frames keep
            // being produced every physics step, which is why this patch needs no warm-up.
            ScaleValidation.RequireInstanceMethod(
                typeof(CharacterRagdoll), "FixedUpdate", typeof(void), Type.EmptyTypes, true);
            ScaleValidation.RequireInstanceMethod(
                typeof(Bodypart), "SaveAnimationData", typeof(void), Type.EmptyTypes, true);
            ScaleValidation.RequireInstanceMethod(
                typeof(Bodypart), "SnapToAnim", typeof(void), Type.EmptyTypes, true);
        }
    }
}
