using System;
using System.Reflection;
using HarmonyLib;
using PeakSafeOptimizer.Patches.Scale;

namespace PeakSafeOptimizer.Patches.ScaleExperimental
{
    /// <summary>
    /// Opt-in: skips CharacterRagdoll.FixedUpdate() (audited source CharacterRagdoll.cs:151-174)
    /// for a distant remote character selected by <see cref="RemoteRagdollLod.ShouldDowngrade"/>.
    ///
    /// What is skipped: SetPhysicsMats, RotateCharacter, the IK rig SyncLayers/Evaluate, the
    /// animator PlayableGraph.Evaluate, ConfigureIK, per-part SaveAnimationData,
    /// SaveAdditionalTransformPositions, ResetRotation and per-part ResetTransform. This is the
    /// single most expensive per-character physics-step cost in the game and it is purely
    /// animation-pose work: none of it applies a force, and none of it moves a Rigidbody. The
    /// visible effect is that the distant character stops playing its animation pose and hangs in
    /// its last pose while physics keeps carrying it.
    ///
    /// Why it is safe: the rig transforms only stop being re-synced from the animation graph, the
    /// Rigidbodies themselves are untouched, and CharacterMovement.FixedUpdate keeps calling
    /// Gravity/ToggleUseGravity/Drag/ApplyForces on every part, so nothing floats, velocity keeps
    /// converging and forcesToAdd keeps being flushed. Collider state, kinematic state and the
    /// CharacterSyncer network interpolation are not involved at all.
    ///
    /// Why SaveAdditionalTransformPositions must NOT be called here, even though
    /// CharacterMovement.FixedUpdate reads its outputs: it reads the *animated* pose off the
    /// bodypart transforms (CharacterRagdoll.cs:204-215), and in the audited order it runs after
    /// the graph evaluation and before ResetTransform (CharacterRagdoll.cs:162-173). With the
    /// evaluation skipped, the transforms still hold the physics pose that the previous tick's
    /// ResetTransform wrote, so calling it would assign the character's *current sagging* height to
    /// data.targetHeadHeight / targetHipHeight - the very values Stand() uses as its target
    /// (CharacterMovement.cs:690-699) and RaycastGroundCheck uses as its ray length (:877).
    /// Target converging on current makes both InverseLerps zero, the standing force goes to zero,
    /// and the character sags further, which lowers the target again. Earlier versions of this
    /// patch did call it and were wrong. Leaving both heights at the value from the last real
    /// evaluation keeps them stale but semantically correct, which is the same choice
    /// RemoteClusterAnimationThrottle makes for the same reason.
    ///
    /// The staleness that follows from that choice is bounded by the shared helper rather than
    /// ignored. Verified consequences of freezing the two heights:
    ///   * Both consumers do run for remote characters. CharacterMovement.FixedUpdate
    ///     (CharacterMovement.cs:283-317) has no IsLocal / photonView.IsMine gate at all, and no
    ///     such gate anywhere in that class stands between it and either height consumer. The
    ///     complete list of them: Update :181, SetMovementState :224, TryJumpOffVineOrRope :402,
    ///     TryJetpack :410 and JetpackRPC :569 sit in methods FixedUpdate never calls; OnCollision
    ///     :943 and CheckForPalJump :1005 are reached from the collision callback and from Update
    ///     respectively; and the two that FixedUpdate can reach - :796/:817 via
    ///     EvaluateGroundChecks -> Land -> CheckFallDamage and :901 inside RaycastGroundCheck -
    ///     guard only local-only side effects (reticle, fall damage, camera shake), never the height
    ///     reads. So RaycastGroundCheck (:286, ray length targetHipHeight + 0.3f at :877) and Stand
    ///     (:288-291, InverseLerp around targetHeadHeight at :690-699) both execute for every
    ///     character on this client. This defect is therefore real, not theoretical.
    ///   * The only writer of either field in the whole assembly is
    ///     CharacterRagdoll.SaveAdditionalTransformPositions (CharacterRagdoll.cs:208 and :215), which
    ///     is exactly what this patch skips - nothing else can refresh them while downgraded.
    ///   * The pose inputs that change those heights are data.isCrouching and data.isSprinting
    ///     (CharacterData.cs:156 and :86), neither of which was in the fallback list.
    /// The fix lives in RemoteRagdollLod.PoseFlags: a change in either flag forces the character back
    /// to TierFull and re-arms the warm-up, so the heights are re-derived at full fidelity across the
    /// whole crouch/sprint blend before any further downgrade. Outside such a transition the two
    /// heights are genuinely constant for a standing or walking character, so freezing them is
    /// invisible.
    ///
    /// Likewise SetPhysicsMats stops re-asserting the collider friction material while downgraded.
    /// The friction *selection* is driven by currentRagdollControll (CharacterRagdoll.cs:89-96) and a
    /// value below 0.9 is one of the shared fallback conditions, so the moment a downgraded character
    /// stops being fully controlled this Prefix lets the original run again and the material is
    /// re-applied on that same physics step. That argument covers the selection only, not the applied
    /// state, and the difference matters:
    ///   * Bodypart.SetPhysicsMaterial (Bodypart.cs:299-320) lets a per-part frictionType override
    ///     the passed-in setFrictionType entirely: `frictionType == Grippy` writes normalMat
    ///     (:303-306) and `frictionType == Slippery` writes slipperyMat (:307-310) before the
    ///     argument is even looked at, so only a part still at FrictionType.Unspecified
    ///     (Bodypart.cs:8-13) reaches the setFrictionType branches (:311-318). And col.sharedMaterial
    ///     is a plain settable collider property that any third party can write at any time. So the
    ///     material actually on a collider is not a function of currentRagdollControll.
    ///   * That is precisely why RagdollPhysicsMats needs a periodic audit rather than a pure
    ///     signature compare (Patches\Scale\RagdollPhysicsMatsPatch.cs:37-38,
    ///     ForceRefreshFrameInterval = 300 / ForceRefreshSecondsInterval = 5f, consumed at :212-216).
    ///     But that audit only advances when its own Prefix runs, and its Prefix hangs off
    ///     SetPhysicsMats - which is the first call inside the CharacterRagdoll.FixedUpdate body this
    ///     patch skips (CharacterRagdoll.cs:153). While a character is downgraded, SetPhysicsMats is
    ///     never called, so the audit never ticks for it.
    ///   * Consequence: once a remote character passes the enter distance, an external
    ///     sharedMaterial write on it is not corrected until it comes back inside the exit distance.
    ///     That window has no upper bound, where vanilla corrects within one physics step and
    ///     RagdollPhysicsMats alone corrects within 5s. What that costs in practice is bounded by
    ///     rejection 3 below: in an unmodded game there is no such external write to correct.
    ///   * RemoteClusterAnimationThrottle does not fill the gap, even though it holds a
    ///     SetPhysicsMats accessor and calls it on every tick it handles. The two patches share this
    ///     target, this Prefix carries no [HarmonyPriority] and therefore runs at Priority.Normal
    ///     while that one is [HarmonyPriority(Priority.Low)], so this one runs first - HarmonyLib
    ///     orders Prefixes by descending priority and, in the 0Harmony.dll (2.9.0) shipped in the
    ///     game's BepInEx\core, HarmonyLib.Priority declares Last = 0, VeryLow = 100, Low = 200,
    ///     LowerThanNormal = 300, Normal = 400, HigherThanNormal = 500, High = 600, VeryHigh = 700,
    ///     First = 800, so Normal (400) &gt; Low (200) is a checked fact and not an assumption about
    ///     the names. HarmonyX runs every Prefix and combines the verdicts afterwards, which is why
    ///     that Prefix opens with `if (!__runOriginal) return false;`. On a tick where this patch has
    ///     returned false the cluster Prefix therefore returns immediately and its SetPhysicsMats
    ///     call never happens either. With both switches on, a downgraded character gets *zero*
    ///     SetPhysicsMats calls per tick, not one - so the audit is fully stalled for it rather than
    ///     merely slowed. Only the exact count depends on that ordering: were the two ever to run the
    ///     other way round, the cluster Prefix would land one call before this one suppressed the
    ///     original, which would slow the audit rather than stall it, but would still not restore an
    ///     upper bound on the window.
    /// The conclusion is unchanged - only friction is affected, no force or Rigidbody state - but the
    /// staleness bound is "until it comes back", not "one step". Recovery is still immediate and is
    /// in fact sharpened by the stall: RagdollPhysicsMats forces a refresh when
    /// `frame - LastForcedFrame >= 300 || now - LastForcedTime >= 5f`
    /// (Patches\Scale\RagdollPhysicsMatsPatch.cs:212-216), and a clock that has not advanced for the
    /// whole downgraded stretch satisfies that by a wide margin, so the first SetPhysicsMats after
    /// the character re-enters the exit distance is guaranteed to be a full re-apply plus a full
    /// collider-material audit rather than a cheap signature compare.
    ///
    /// Considered and rejected: keeping a SetPhysicsMats accessor here and calling it every Nth
    /// physics step while downgraded, to re-bound the window. Rejected on three static grounds.
    ///   1. It would make this patch write physics state. Today its entire correctness argument is
    ///      "the Prefix only ever returns true or false" - it holds one FieldRef, performs no call
    ///      and no write, which is what makes the lane's "no new force, torque or transform write is
    ///      introduced anywhere" claim (see RemoteRagdollLod) hold trivially for it.
    ///      col.sharedMaterial is not a force, but it is a live collider property, and the claim
    ///      would have to be weakened for a defect that only affects friction.
    ///   2. The machinery is disproportionate. SetPhysicsMats is private, so it needs a new
    ///      delegate accessor and its own structural assertion; Bodypart.SetPhysicsMaterial iterates
    ///      the internal List&lt;RigCreatorCollider&gt; Bodypart.colliders (Bodypart.cs:41) and writes
    ///      collider.col.sharedMaterial with no null check on either the element or its col field
    ///      (Bodypart.cs:301-319), so the call needs a try/catch and a per-instance failure latch;
    ///      and "every Nth step" needs a per-CharacterRagdoll tick state, i.e. a
    ///      ConditionalWeakTable. That is the whole of RemoteClusterAnimationThrottle's apparatus
    ///      imported into a fifteen-line Prefix.
    ///   3. In vanilla there is nothing for the skipped audits to correct. The 300-frame / 5-second
    ///      audit exists only because col.sharedMaterial can be written from outside SetPhysicsMats -
    ///      but in this game nothing does. A whole-assembly search for `sharedMaterial` over
    ///      decompiled-latest\Assembly-CSharp finds exactly four writes to a *Collider's*
    ///      sharedMaterial, and all four are the four branches inside Bodypart.SetPhysicsMaterial
    ///      itself (Bodypart.cs:305, :309, :313, :317). Every other hit is Renderer.sharedMaterial /
    ///      sharedMaterials - visual materials, unrelated to physics - or a read: Snowball.cs:134
    ///      caches `_defaultPhysicsMaterial = physicalCollider.sharedMaterial` off the snowball's own
    ///      SphereCollider (Snowball.cs:90), never a bodypart's, and only ever reads
    ///      .dynamicFriction off that cache afterwards (Snowball.cs:181, :191); Snowball.cs:313 reads
    ///      a Renderer's material name. So the event the missing audit would catch - a third party
    ///      changing the material on a bodypart collider - does not occur in an unmodded game. It is
    ///      meaningful only when another mod writes bodypart collider materials, and faults caused by
    ///      other mods are explicitly out of scope for this plugin. That turns "add a periodic call"
    ///      from an uncertain benefit into a demonstrably empty one: it would cost a delegate, a CWT
    ///      and a try/catch to re-audit a value that nothing in the game can have changed.
    /// One fact that the earlier version of this note treated as unknowable, recorded here because it
    /// also settles the corresponding note in RemoteClusterAnimationThrottle: an open delegate built
    /// with MethodInfo.CreateDelegate *does* observe a MonoMod entry-point detour of its target.
    /// MonoMod proves this itself at startup. In MonoMod.RuntimeDetour.dll, shipped in the game's
    /// BepInEx\core and decompiled for this note (it is not part of decompiled-latest, so there is no
    /// file.cs:line to cite), the instance constructor of
    /// MonoMod.RuntimeDetour.Platforms.DetourRuntimeILPlatform runs three selftests: it applies a
    /// native detour to a private instance method, then binds that same MethodInfo with
    /// Delegate.CreateDelegate, invokes the delegate, and requires the hook's return value - the
    /// original bodies only write an error line and throw ("This method should've been detoured!",
    /// and an explicit `throw new Exception("_SelftestGetInStruct failed!")` on the third). That
    /// constructor runs while MonoMod initialises, i.e. on BepInEx's own startup path, so if
    /// CreateDelegate did not see the detour BepInEx would not boot at all. Consequently
    /// RemoteClusterAnimationThrottle's per-tick _setPhysicsMats call - bound through
    /// ScaleExperimentalEmit.CreateDelegate, which is a plain
    /// `method.CreateDelegate(typeof(TDelegate))` (Patches\ScaleExperimental\ScaleExperimentalEmit.cs:27-33),
    /// not an emitted DynamicMethod - does go through RagdollPhysicsMats' Prefix when that switch is
    /// also on: it gets the redundancy filter and it advances the audit clock, exactly as the
    /// original call site does. This does not revive rejection 3, because rejection 3 no longer turns
    /// on whether the call would be intercepted.
    /// </summary>
    internal static class RemoteRagdollLodPhysicsPatch
    {
        private static AccessTools.FieldRef<CharacterRagdoll, Character> _character;
        private static bool _acquired;

        internal static void Initialize()
        {
            ResolveAndValidateTarget();

            FieldInfo characterField = ScaleValidation.RequireInstanceField(
                typeof(CharacterRagdoll), "character", typeof(Character));
            // Proof that the two members the safety argument reasons about still exist. Neither is
            // called by this patch: the whole method body is skipped.
            ScaleValidation.RequireInstanceMethod(
                typeof(CharacterRagdoll), "SaveAdditionalTransformPositions", typeof(void),
                Type.EmptyTypes, requirePublic: false);
            ScaleValidation.RequireInstanceMethod(
                typeof(CharacterRagdoll), "SetPhysicsMats", typeof(void), Type.EmptyTypes, requirePublic: false);
            ScaleValidation.RequireInstanceField(typeof(CharacterData), "targetHeadHeight", typeof(float));
            ScaleValidation.RequireInstanceField(typeof(CharacterData), "targetHipHeight", typeof(float));

            AccessTools.FieldRef<CharacterRagdoll, Character> character =
                AccessTools.FieldRefAccess<CharacterRagdoll, Character>(characterField);
            if (character == null)
                throw new InvalidOperationException("RemoteRagdollLodPhysics FieldRef creation failed");

            // Acquired last: everything that can still throw has already run - the assignments below
            // are a plain reference store and a bool - so a failed Initialize never leaves a
            // reference behind on the shared helper.
            RemoteRagdollLod.Acquire();
            _acquired = true;
            _character = character;
        }

        internal static MethodInfo ResolveAndValidateTarget()
        {
            return ScaleValidation.RequireInstanceMethod(
                typeof(CharacterRagdoll), "FixedUpdate", typeof(void), Type.EmptyTypes, requirePublic: true);
        }

        internal static void Reset()
        {
            _character = null;
            if (_acquired)
            {
                _acquired = false;
                RemoteRagdollLod.Release();
            }
        }

        internal static bool Prefix(CharacterRagdoll __instance, bool __runOriginal)
        {
            // Another Prefix on this method may already have decided to skip it.
            if (!__runOriginal)
                return false;

            AccessTools.FieldRef<CharacterRagdoll, Character> characterRef = _character;
            if (characterRef == null)
                return true;

            Character owner = characterRef(__instance);
            if (owner == null)
                return true;
            return !RemoteRagdollLod.ShouldDowngrade(owner);
        }
    }
}
