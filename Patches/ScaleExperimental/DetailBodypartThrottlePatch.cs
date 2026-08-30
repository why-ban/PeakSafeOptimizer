using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using PeakSafeOptimizer.Patches.Scale;
using UnityEngine;

namespace PeakSafeOptimizer.Patches.ScaleExperimental
{
    /// <summary>
    /// Opt-in: runs Bodypart.Animate(float, float) (audited source Bodypart.cs:143-153) only every
    /// Nth physics tick for the *detail* bodyparts (fingers, jiggle bones, toes, mouth/jaw) of a
    /// remote, player-controlled, fully controlled and non-interacting character. On the other ticks
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
    /// execute tick the part is driven towards a fresh pose, and any Bodypart.SnapToAnim triggered
    /// elsewhere reads targets that were written on that same step.
    ///
    /// Eligibility is <see cref="RemoteRagdollLod.PassesFallbacks(Character)"/> - the lane's one
    /// fallback list, without the distance term, the tier state machine or the pose gate that
    /// <see cref="RemoteRagdollLod.ShouldDowngrade"/> adds on top of it. This patch therefore calls
    /// <see cref="RemoteRagdollLod.Acquire"/> in <see cref="Initialize"/> even though it is a separate
    /// switch and can be the only member of the lane that is enabled: that list and
    /// <see cref="RemoteRagdollLod.ResolveOwner"/> both run on accessors Acquire creates. Only
    /// <see cref="RemoteRagdollLod.PhysicsTick"/> and <see cref="RemoteRagdollLod.Phase"/> are pure
    /// computation and would work without it.
    /// </summary>
    internal static class DetailBodypartThrottlePatch
    {
        /// <summary>
        /// Animate runs once every this many physics ticks for a detail part. 3 keeps fingers and jaws
        /// moving at ~16Hz, which reads as slightly soft rather than frozen; higher values start to
        /// look broken, which is not a trade this patch offers.
        /// </summary>
        private const int Interval = 3;

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
        /// Per-character one-physics-tick decision cache. Animate is called once per part per physics
        /// step, so without this the fallback list would be re-evaluated dozens of times per character
        /// per step. Never references its ConditionalWeakTable key.
        /// </summary>
        private sealed class State
        {
            internal int LastPhysicsTick = int.MinValue;
            internal bool LastResult;
        }

        /// <summary>
        /// Throttle lookup indexed by (int)BodypartType, built once in Initialize. A plain bool[]
        /// read replaces any per-call switch or HashSet lookup.
        /// </summary>
        private static bool[] _detailParts;
        private static AccessTools.FieldRef<Bodypart, BodypartType> _partType;
        private static ConditionalWeakTable<Character, State> _states;
        /// <summary>
        /// Set once <see cref="RemoteRagdollLod.Acquire"/> has succeeded for this patch, so
        /// <see cref="Reset"/> releases exactly one reference and only if one was taken. This patch
        /// has its own configuration switch and can therefore be the *only* holder of that reference:
        /// the shared accessors it reads (the Bodypart.character back reference behind
        /// <see cref="RemoteRagdollLod.ResolveOwner"/>, and the CharacterData accessors behind
        /// <see cref="RemoteRagdollLod.PassesFallbacks(Character)"/>) are created by Acquire and
        /// dropped by the matching Release, so acquiring is not optional here.
        /// </summary>
        private static bool _acquired;

        internal static void Initialize()
        {
            ResolveAndValidateTarget();
            ValidateStructure();

            FieldInfo partTypeField = ScaleValidation.RequireInstanceField(
                typeof(Bodypart), "partType", typeof(BodypartType));

            bool[] table = BuildDetailTable();
            AccessTools.FieldRef<Bodypart, BodypartType> partType =
                AccessTools.FieldRefAccess<Bodypart, BodypartType>(partTypeField);
            if (partType == null)
                throw new InvalidOperationException("DetailBodypartThrottle accessor creation failed");
            var states = new ConditionalWeakTable<Character, State>();

            // Acquired last, and after the allocation above, because it is the last statement here
            // that can throw: it runs RemoteRagdollLod.ValidateStructure and creates the shared
            // accessors. Everything below is a plain field assignment.
            RemoteRagdollLod.Acquire();
            _acquired = true;
            _states = states;
            _partType = partType;
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
            _states = null;
            // Released after the fields above are cleared, so no hot-path read can observe a
            // published table while the shared accessors are already gone. The installer only calls
            // Reset once every Harmony method of this patch is confirmed detached.
            if (_acquired)
            {
                _acquired = false;
                RemoteRagdollLod.Release();
            }
        }

        /// <summary>
        /// Hot path: called once per bodypart per physics step for every character in the level, so
        /// it must stay allocation free and must not log. RateLimitedDiagnostics is deliberately not
        /// used anywhere in here - every early-out below (missing accessor, unresolved owner,
        /// non-detail part) is reachable on every single call, so a Hit would cost more than the work
        /// this patch removes. Initialize/Reset failures are already reported by the installer.
        /// </summary>
        [HarmonyPriority(Priority.Low)]
        internal static bool Prefix(Bodypart __instance, bool __runOriginal)
        {
            // RemoteRagdollLodAnimate shares this target and runs first (Priority.High). When it has
            // already decided to skip, there is nothing left to decide and no reason to pay for the
            // per-part tick math, the instance-id modulo and the per-character table lookup below.
            if (!__runOriginal)
                return false;

            bool[] table = _detailParts;
            AccessTools.FieldRef<Bodypart, BodypartType> partTypeRef = _partType;
            if (table == null || partTypeRef == null || __instance == null)
                return true;

            int index = (int)partTypeRef(__instance);
            if ((uint)index >= (uint)table.Length || !table[index])
                return true;

            // Fail-safe on the tick source, and the only reason this patch needs one at all: the
            // throttle verdict is `(tick + phase) % Interval != 0`, so a *frozen* tick index does not
            // cost a few extra skipped steps, it skips Animate for every phase != 0 forever.
            // RemoteRagdollLod.PhysicsTick is Time.fixedTimeAsDouble / Time.fixedDeltaTime floored and
            // narrowed, and a zero, negative, NaN or infinite fixed step makes that quotient
            // non-finite; narrowing it is implementation defined and yields int.MinValue on x64, i.e.
            // exactly the frozen index. Judged unreachable in practice rather than impossible:
            // Time.fixedDeltaTime is > 0 whenever physics is stepping, and although
            // UnityEngine.AdaptivePerformanceModule.dll does ship in PEAK_Data\Managed and its
            // AdaptivePhysics scaler writes Time.fixedDeltaTime directly (AdaptivePhysics.cs:18, :30),
            // it is a MonoBehaviour scaler that only runs when an Adaptive Performance provider is
            // active, and nothing in the game or in this plugin references that assembly. Another mod
            // could, though, and the value is not this plugin's to assume. Same predicate as the
            // cluster lane (RemoteClusterAnimationThrottlePatch.cs:296-298) so the two
            // cannot disagree about what a usable step is: !(dt > 0f) rejects 0, negatives and NaN
            // (every comparison against NaN is false), float.IsInfinity rejects +/-Inf. One native
            // float read and two compares, and it returns true - vanilla - which is the safe
            // direction. No diagnostics here, per the ban above.
            float fixedDeltaTime = Time.fixedDeltaTime;
            if (!(fixedDeltaTime > 0f) || float.IsInfinity(fixedDeltaTime))
                return true;

            // Stable per-part phase so the detail parts of a character do not all execute on the
            // same tick, which would trade a flat cost for a periodic spike. RemoteRagdollLod.Phase
            // mixes the instance id before the modulo: raw `id % Interval` collapses to one phase for
            // objects created in the same batch (which a player rig is), which would invert the whole
            // point of the phase. It is a pure function of the id, so the phase is stable for the
            // lifetime of the part, and it is overflow safe for int.MinValue.
            int phase = RemoteRagdollLod.Phase(__instance.GetInstanceID(), Interval);
            // Use the physics tick rather than Time.frameCount. When Unity runs multiple physics
            // steps inside one rendered frame, each step gets its own deterministic decision instead
            // of creating consecutive runs and consecutive gaps.
            int physicsTick = PhysicsTick();
            if ((physicsTick + phase) % Interval == 0)
                return true;

            // Bodypart.character is assigned in Bodypart.Start (Bodypart.cs:97); before that runs,
            // or on a stray part with no Character parent, the original must run unchanged.
            // RemoteRagdollLod.ResolveOwner reads that same back reference through the shared
            // accessor and reports null when the accessor itself is missing, which covers what a
            // local FieldRef null check used to do here.
            Character owner = RemoteRagdollLod.ResolveOwner(__instance);
            if (owner == null)
                return true;
            // The tick is handed over rather than re-read: it used to be computed a second time
            // inside ShouldThrottle, which cost every detail part two more native Time property reads
            // per physics step and left two computations of one value that must agree.
            return !ShouldThrottle(owner, physicsTick);
        }

        /// <summary>
        /// Physics-step index. Delegates to <see cref="RemoteRagdollLod.PhysicsTick"/> so the two
        /// lanes can never disagree about which step they are on, and so the double-precision
        /// derivation lives in one place. PhysicsTick reads only Time.fixedTimeAsDouble and
        /// Time.fixedDeltaTime and touches none of that class's fields, so unlike
        /// <see cref="RemoteRagdollLod.PassesFallbacks(Character)"/> it does not depend on
        /// <see cref="RemoteRagdollLod.Acquire"/> having run - which is why it may be called from the
        /// early part of the Prefix, before the owner is even resolved. (It used to be an inline
        /// Mathf.FloorToInt(Time.fixedTime / Time.fixedDeltaTime) duplicated in two places here;
        /// Time.fixedTime is a float accumulated from process start, which loses the resolution to
        /// separate adjacent steps in a long session and shifts if anything writes
        /// Time.fixedDeltaTime.) It is called exactly once per Prefix invocation; the value is passed
        /// down instead of being derived again.
        /// </summary>
        private static int PhysicsTick() => RemoteRagdollLod.PhysicsTick();

        /// <summary>
        /// Per-character throttle verdict, cached for the physics tick the caller is already on.
        /// <paramref name="physicsTick"/> comes from the Prefix so that the cache key and the
        /// `(tick + phase) % Interval` decision are provably the same step.
        ///
        /// The verdict itself is <see cref="RemoteRagdollLod.PassesFallbacks(Character)"/>, i.e. the
        /// lane's single fallback list, so a condition added there reaches this patch too. This method
        /// used to hold a line-by-line copy of that list, and the copy had already fallen behind once:
        /// the crouch/sprint pose gate added to RemoteRagdollLod never appeared here. Only the cache
        /// and the distance-free framing are local.
        ///
        /// Two conditions in that list are load-bearing for *this* patch specifically and are called
        /// out here so they are not mistaken for generic ones:
        ///   * data.isKinecmatic. The kinematic branch of Animate is SnapToAnim (Bodypart.cs:145-149),
        ///     and CharacterRagdoll.ToggleKinematic sets data.isKinecmatic together with every part's
        ///     Rigidbody.isKinematic (CharacterRagdoll.cs:80-87), so excluding it means a throttled
        ///     step can never be a skipped snap.
        ///   * data.sinceItemAttach. The equip coroutine drives CharacterRagdoll.FixedUpdate and
        ///     SnapToAnimation manually (CharacterItems.cs:607-650, the resets at :625 inside the
        ///     three-iteration loop), so a throttled Animate must not overlap that window.
        /// No pose gate is needed here, unlike in <see cref="RemoteRagdollLod.ShouldDowngrade"/>: this
        /// patch leaves CharacterRagdoll.FixedUpdate running, so data.targetHeadHeight and
        /// data.targetHipHeight are refreshed on every physics step and cannot go stale.
        /// </summary>
        private static bool ShouldThrottle(Character c, int physicsTick)
        {
            ConditionalWeakTable<Character, State> states = _states;
            if (states == null)
                return false;

            bool hasState = states.TryGetValue(c, out State state);
            if (hasState && state.LastPhysicsTick == physicsTick)
                return state.LastResult;

            // Returns false when the shared accessors are absent, which is the same "do not throttle"
            // answer the local accessor null check used to give. No diagnostics anywhere on this path,
            // per the ban in the Prefix docstring.
            bool result = RemoteRagdollLod.PassesFallbacks(c);
            if (!hasState)
                state = states.GetOrCreateValue(c);
            state.LastPhysicsTick = physicsTick;
            state.LastResult = result;
            return result;
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
        /// Exact structure check for what is specific to *this* patch: the calls its safety argument
        /// depends on. The members the fallback list reads are deliberately not re-asserted here -
        /// that list now lives in <see cref="RemoteRagdollLod.PassesFallbacks(Character)"/> and every
        /// member it touches is asserted by RemoteRagdollLod.ValidateStructure, which runs inside the
        /// <see cref="RemoteRagdollLod.Acquire"/> call in <see cref="Initialize"/> - unconditionally,
        /// including when this patch is the only member of the lane that is enabled. Duplicating those
        /// ~25 assertions here is what let the copy of the fallback list drift in the first place: a
        /// condition added to the shared list would have had to be mirrored in two validators as well
        /// as two decision bodies. Time.fixedTimeAsDouble is asserted there too, for the same reason.
        ///
        /// What is still checked locally: Bodypart.partType (in <see cref="Initialize"/>), the
        /// BodypartType enum shape (in <see cref="BuildDetailTable"/>), and the calls below. Anything
        /// that no longer matches throws and the installer leaves the patch uninstalled.
        /// </summary>
        private static void ValidateStructure()
        {
            // Proof that the load-bearing part of the physics loop is still where the safety argument
            // assumes it is (CharacterMovement.cs:304-315). None of these is patched or called here.
            // Asserted here rather than only through RemoteRagdollLod because they are this patch's
            // own argument for why a skipped Animate step is passive rather than broken.
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
            // being produced every physics step, which is why this patch needs no warm-up. The
            // CharacterRagdoll.FixedUpdate assertion is unique to this patch: the distance lane skips
            // that method, this one relies on it still running.
            ScaleValidation.RequireInstanceMethod(
                typeof(CharacterRagdoll), "FixedUpdate", typeof(void), Type.EmptyTypes, true);
            ScaleValidation.RequireInstanceMethod(
                typeof(Bodypart), "SaveAnimationData", typeof(void), Type.EmptyTypes, true);
            ScaleValidation.RequireInstanceMethod(
                typeof(Bodypart), "SnapToAnim", typeof(void), Type.EmptyTypes, true);
        }
    }
}
