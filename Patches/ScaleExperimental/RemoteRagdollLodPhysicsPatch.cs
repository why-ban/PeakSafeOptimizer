using System;
using System.Reflection;
using HarmonyLib;
using PeakSafeOptimizer.Patches.Scale;

namespace PeakSafeOptimizer.Patches.ScaleExperimental
{
    /// <summary>
    /// Opt-in: skips CharacterRagdoll.FixedUpdate() (audited source CharacterRagdoll.cs:151-177)
    /// for a distant remote character selected by <see cref="RemoteRagdollLod.ShouldDowngrade"/>.
    ///
    /// What is skipped: RotateCharacter, the IK rig SyncLayers/Evaluate, the animator
    /// PlayableGraph.Evaluate, ConfigureIK, per-part SaveAnimationData,
    /// SaveAdditionalTransformPositions, ResetRotation and per-part ResetTransform. This is the
    /// single most expensive per-character physics-step cost in the game and it is purely
    /// animation-pose work: none of it applies a force, and none of it moves a Rigidbody. The
    /// visible effect is that the distant character stops playing its animation pose and hangs in
    /// its last pose while physics keeps carrying it.
    ///
    /// SetPhysicsMats is deliberately absent from that list, and it is absent for a reason that did
    /// not hold in the previously audited build. The one call site is now gated:
    /// `if (character.IsLocal) { SetPhysicsMats(); }` (CharacterRagdoll.cs:153-156), and it is the
    /// only call site in the whole assembly. Character.IsLocal is `this == localCharacter`
    /// (Character.cs:235) - a reference compare against the static assigned in Character.Awake, so
    /// no native call and no per-frame cost. A downgraded character is by construction never the
    /// local one: `c == local` is the first entry of the shared fallback list
    /// (RemoteRagdollLod.cs:717). Therefore vanilla itself performs *zero* SetPhysicsMats calls for
    /// every character this patch can possibly act on, and skipping the method body removes zero
    /// material writes. There is no friction staleness to bound and no audit to stall. The long
    /// argument this docstring used to carry - a bounded-staleness case plus three reasons for not
    /// re-adding a periodic SetPhysicsMats call from here - answered a defect that the gate has
    /// removed outright, so it is gone rather than updated. Two facts from it are worth keeping
    /// because they are what make the new argument tight rather than merely convenient:
    ///   * The material actually on a collider is not a function of currentRagdollControll.
    ///     Bodypart.SetPhysicsMaterial (Bodypart.cs:300-321) lets a per-part frictionType override
    ///     the passed-in setFrictionType entirely - `frictionType == Grippy` writes normalMat
    ///     (:304-307), `frictionType == Slippery` writes slipperyMat (:308-311) - and
    ///     col.sharedMaterial is a plain settable collider property. So "the friction selection
    ///     follows a value in the fallback list" was never sufficient on its own; "vanilla does not
    ///     write it either" is.
    ///   * Nothing in the game writes a bodypart collider's sharedMaterial from outside
    ///     SetPhysicsMaterial. A whole-assembly search over decompiled-latest\Assembly-CSharp finds
    ///     exactly four writes to a *Collider's* sharedMaterial and all four are the four branches
    ///     inside that method (Bodypart.cs:306, :310, :314, :318); every other hit is a
    ///     Renderer.sharedMaterial or a read (Snowball.cs:134 caches its own SphereCollider's
    ///     material and only reads .dynamicFriction off the cache at :181 and :191). So a remote
    ///     character keeps the material its prefab serialised, which is now vanilla behaviour rather
    ///     than something this patch causes.
    ///
    /// Why it is safe: the rig transforms only stop being re-synced from the animation graph, the
    /// Rigidbodies themselves are untouched, and CharacterMovement.FixedUpdate keeps calling
    /// Gravity/ToggleUseGravity/Drag/ApplyForces on every part, so nothing floats, velocity keeps
    /// converging and forcesToAdd keeps being flushed. Collider state, kinematic state and the
    /// CharacterSyncer network interpolation are not involved at all.
    ///
    /// Why SaveAdditionalTransformPositions must NOT be called here, even though
    /// CharacterMovement.FixedUpdate reads its outputs: it reads the *animated* pose off the
    /// bodypart transforms (CharacterRagdoll.cs:209-217), and in the audited order it runs after
    /// the graph evaluation and before ResetTransform (CharacterRagdoll.cs:165-176). With the
    /// evaluation skipped, the transforms still hold the physics pose that the previous tick's
    /// ResetTransform wrote, so calling it would assign the character's *current sagging* height to
    /// data.targetHeadHeight / targetHipHeight - the very values Stand() uses as its target
    /// (CharacterMovement.cs:709-718) and RaycastGroundCheck uses as its ray length (:894).
    /// Target converging on current makes both InverseLerps zero, the standing force goes to zero,
    /// and the character sags further, which lowers the target again. Earlier versions of this
    /// patch did call it and were wrong. Leaving both heights at the value from the last real
    /// evaluation keeps them stale but semantically correct, which is the same choice
    /// RemoteClusterAnimationThrottle makes for the same reason.
    ///
    /// The staleness that follows from that choice is bounded by the shared helper rather than
    /// ignored. Verified consequences of freezing the two heights:
    ///   * Both consumers do run for remote characters. CharacterMovement.FixedUpdate
    ///     (CharacterMovement.cs:285-325) has no IsLocal / photonView.IsMine gate at all, and no
    ///     such gate anywhere in that class stands between it and either height consumer. The
    ///     complete list of them: Update :183, SetMovementState :226, TryJumpOffVineOrRope :419,
    ///     TryJetpack :427 and JetpackRPC :586 sit in methods FixedUpdate never calls; OnCollision
    ///     :960 and CheckForPalJump :1022 are reached from the collision callback and from Update
    ///     respectively; and the three that FixedUpdate can reach - :815 and :834 via
    ///     EvaluateGroundChecks -> Land -> CheckFallDamage and :918 inside RaycastGroundCheck -
    ///     guard only local-only side effects (reticle, fall damage, camera shake), never the height
    ///     reads. So RaycastGroundCheck (:288, ray length targetHipHeight + 0.3f at :894) and Stand
    ///     (:290-293, InverseLerp around targetHeadHeight at :709-718) both execute for every
    ///     character on this client. This defect is therefore real, not theoretical.
    ///   * The only writer of either field in the whole assembly is
    ///     CharacterRagdoll.SaveAdditionalTransformPositions (CharacterRagdoll.cs:211 and :216), which
    ///     is exactly what this patch skips - nothing else can refresh them while downgraded.
    ///   * The pose inputs that change those heights are data.isCrouching and data.isSprinting
    ///     (CharacterData.cs:157 and :87), neither of which was in the fallback list.
    /// The fix lives in RemoteRagdollLod.PoseFlags: a change in either flag forces the character back
    /// to TierFull and re-arms the warm-up, so the heights are re-derived at full fidelity across the
    /// whole crouch/sprint blend before any further downgrade. Outside such a transition the two
    /// heights are genuinely constant for a standing or walking character, so freezing them is
    /// invisible.
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
            // Character.IsLocal is the gate in front of the SetPhysicsMats call. It is what makes
            // "this patch removes no material write" true, so its shape is asserted even though the
            // patch never reads it: were the gate to disappear, this Prefix would silently start
            // suppressing per-step friction writes again and the docstring's argument would be false.
            ScaleValidation.RequireInstanceProperty(typeof(Character), "IsLocal", typeof(bool));
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
