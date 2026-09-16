using System;
using System.Reflection;
using HarmonyLib;
using PeakSafeOptimizer.Diagnostics;
using PeakSafeOptimizer.Patches.Scale;
using UnityEngine;

namespace PeakSafeOptimizer.Patches.ScaleExperimental
{
    /// <summary>
    /// Opt-in: replaces CharacterAnimations.Update() (audited source CharacterAnimations.cs:160-286)
    /// with a reduced body for a distant remote character selected by
    /// <see cref="RemoteRagdollLod.ShouldDowngrade"/>. Not a skip, which is what this said before and
    /// what the other three patches of the lane really do: the tail of the Prefix below, after the
    /// ShouldDowngrade test, *is* the reduced body. What it keeps is everything that is not a pose
    /// parameter - the animation helper transform write, SetAnimSpeed, the throwTime and
    /// sinceEmoteStart timers, the emote-cancel condition, the InWater parameter and the
    /// myersDistance / inWater resets. What it drops is the ~30 other Animator parameter writes and
    /// the Matrix4x4 look computation (CharacterAnimations.cs:201-209), all of which only feed the
    /// animation pose. Keeping the two timers is not cosmetic bookkeeping: they are what make the
    /// Prefix have to honour __runOriginal, because running the body twice in one frame would
    /// double-advance them.
    ///
    /// Why no physics can break: this method applies no force and moves no Rigidbody. Its only
    /// non-Animator writes are helper-transform and bookkeeping values:
    ///   refs.animationPositionTransform.position (a helper transform; its only other consumer is
    ///     Action_Torch's particle simulation space)
    ///   throwTime countdown, data.myersDistance reset to 1000f, data.inWater reset to 1f
    /// None of these is read by a force path. data.myersDistance and data.inWater are only consumed
    /// as Animator parameters - myersDistance at CharacterAnimations.cs:249, inWater at :254
    /// (AN_FLOOR_LEAN_FORWARD, as 1f - inWater) and at :284 (AN_INWATER) - so leaving them stale for
    /// a distant character has no gameplay effect on this client.
    ///
    /// The accepted cost is cosmetic and only on a character that is at least the configured enter
    /// distance away: its Animator parameters freeze at their last values. data.isInWater is
    /// explicitly *not* affected. It reads the InWater parameter back off the Animator
    /// (CharacterData.cs:286, animator.GetFloat(AN_INWATER) &lt; 0.9f) and the replacement below still
    /// writes that one parameter, so both of its consumers - FootStepPlayer.PlayStep
    /// (FootStepPlayer.cs:96) and Peak.WaterSFX.OnEnable (Peak\WaterSFX.cs:13) - keep seeing the
    /// correct value and the footstep/water SFX variant stays right. HandleIK is additionally called
    /// every frame from CharacterAnimations.LateUpdate (CharacterAnimations.cs:335-338), which is not
    /// patched, so IK weights keep being maintained even though the reduced body does not call it
    /// (the original does, at CharacterAnimations.cs:269).
    ///
    /// Beyond the parameter writes, two control-flow blocks of the original are also dropped:
    ///   * The headBob check (CharacterAnimations.cs:163-168). Omitting it is strictly equivalent,
    ///     not merely tolerable: the only thing it can reach is UpdateHeadBob, whose entire body
    ///     sits behind `if (character == Character.localCharacter)` (CharacterAnimations.cs:152-158),
    ///     and a downgraded character is by construction never the local one (RemoteRagdollLod.cs:717
    ///     is the first entry of the fallback list). The flag it maintains, _headBobRemoved, has no
    ///     consumer outside that same block (CharacterAnimations.cs:164), so leaving it stale is not
    ///     observable either.
    ///   * The runtimeAnimatorController change detection plus CacheAnimatorHashes()
    ///     (CharacterAnimations.cs:169-172). While downgraded, a controller swapped in without
    ///     SetAnimatorController is not noticed, so _animatorHashes can go stale. (The 1.20-through-
    ///     c88cc526 builds also logged a "Somebody changed animator controllers" warning from that
    ///     block; the current build dropped the log line and kept the check, so there is no longer a
    ///     log to miss.) This is self-healing - the check resumes as soon as the character
    ///     comes back inside the exit distance - and harmless in the meantime, because the only
    ///     consumer of _animatorHashes is the AN_VINE_SLIDE guard at CharacterAnimations.cs:234,
    ///     which lives inside the very run of parameter writes the reduced body drops.
    ///
    /// One frozen parameter deserves naming separately. The replacement consumes and discards
    /// data.myersDistance: it resets the field to 1000f exactly as the original does at
    /// CharacterAnimations.cs:250, but it does not first write AN_MYERS_DISTANCE (the original's
    /// write-then-reset pair is :249 followed by :250). So a downgraded remote player that the
    /// Scoutmaster is currently targeting will not play the corresponding animation. That is
    /// ordinary parameter freezing, but it is the one frozen parameter whose input is written every
    /// frame by a third party rather than derived from the character's own state: Scoutmaster
    /// .DoVisuals assigns currentTarget.data.myersDistance at Scoutmaster.cs:239, reached from
    /// Scoutmaster.Update at :272 on every client.
    ///
    /// Accessors: every Animator member the replacement needs (the CharacterRefs.animator field,
    /// StringToHash, SetBool(int, bool), SetFloat(int, float)) is public and lives in
    /// UnityEngine.AnimationModule, which this project references, so they are all called directly
    /// at compile time. Initialize still asserts their exact runtime shape - including the parameter
    /// types that pin down which SetFloat/SetBool overload is meant - so a game update that moves,
    /// renames or re-overloads them fails this patch closed instead of throwing in the hot path.
    /// </summary>
    internal static class RemoteRagdollLodAnimatorPatch
    {
        private static AccessTools.FieldRef<CharacterAnimations, Character> _character;
        private static AccessTools.FieldRef<CharacterAnimations, bool> _emoting;
        private static AccessTools.FieldRef<CharacterAnimations, float> _sinceEmoteStart;
        private static Action<CharacterAnimations> _setAnimSpeed;
        private static int _emoteHash;
        private static int _inWaterHash;
        private static bool _acquired;

        internal static void Initialize()
        {
            ResolveAndValidateTarget();

            FieldInfo characterField = ScaleValidation.RequireInstanceField(
                typeof(CharacterAnimations), "character", typeof(Character));
            FieldInfo emotingField = ScaleValidation.RequireInstanceField(
                typeof(CharacterAnimations), "emoting", typeof(bool));
            FieldInfo sinceEmoteStartField = ScaleValidation.RequireInstanceField(
                typeof(CharacterAnimations), "sinceEmoteStart", typeof(float));
            ScaleValidation.RequireInstanceField(
                typeof(Character.CharacterRefs), "animator", typeof(Animator));
            // The three Animator entry points the replacement calls directly. The parameter type
            // arrays are what pin the overload: Animator declares SetFloat(int, float),
            // SetFloat(int, float, float, float), SetFloat(string, float) and
            // SetFloat(string, float, float, float) (Animator.cs:933-951) and the matching pair of
            // SetBool overloads (:963-971), and the replacement must keep using the id/value ones.
            ScaleValidation.RequireStaticMethod(
                typeof(Animator), "StringToHash", typeof(int), new[] { typeof(string) }, requirePublic: true);
            ScaleValidation.RequireInstanceMethod(
                typeof(Animator), "SetBool", typeof(void), new[] { typeof(int), typeof(bool) },
                requirePublic: true);
            ScaleValidation.RequireInstanceMethod(
                typeof(Animator), "SetFloat", typeof(void), new[] { typeof(int), typeof(float) },
                requirePublic: true);
            ScaleValidation.RequireInstanceField(typeof(Character), "data", typeof(CharacterData));
            ScaleValidation.RequireInstanceField(typeof(Character), "input", typeof(CharacterInput));
            ScaleValidation.RequireInstanceField(typeof(Character), "refs", typeof(Character.CharacterRefs));
            ScaleValidation.RequireInstanceField(
                typeof(Character.CharacterRefs), "animationPositionTransform", typeof(UnityEngine.Transform));
            ScaleValidation.RequireInstanceField(typeof(CharacterData), "myersDistance", typeof(float));
            ScaleValidation.RequireInstanceField(typeof(CharacterData), "inWater", typeof(float));
            // Also read by the replacement body: the throw countdown it advances and the grounded
            // timer its emote-cancel condition tests. Both are direct compile-time field references,
            // so with requireCurrentMvid these can only fail together with the whole patch - asserted
            // anyway so the validation list matches the set of members the replacement touches.
            ScaleValidation.RequireInstanceField(typeof(CharacterAnimations), "throwTime", typeof(float));
            ScaleValidation.RequireInstanceField(typeof(CharacterData), "sinceGrounded", typeof(float));
            ScaleValidation.RequireInstanceField(typeof(CharacterInput), "movementInput", typeof(UnityEngine.Vector2));
            ScaleValidation.RequireInstanceField(typeof(CharacterInput), "jumpWasPressed", typeof(bool));
            // Proof that HandleIK is still maintained outside Update, which the safety argument
            // above relies on.
            ScaleValidation.RequireInstanceMethod(
                typeof(CharacterAnimations), "LateUpdate", typeof(void), Type.EmptyTypes, false);
            ScaleValidation.RequireInstanceMethod(
                typeof(CharacterAnimations), "HandleIK", typeof(void), Type.EmptyTypes, false);
            MethodInfo setAnimSpeedMethod = ScaleValidation.RequireInstanceMethod(
                typeof(CharacterAnimations), "SetAnimSpeed", typeof(void), Type.EmptyTypes, false);

            AccessTools.FieldRef<CharacterAnimations, Character> character =
                AccessTools.FieldRefAccess<CharacterAnimations, Character>(characterField);
            AccessTools.FieldRef<CharacterAnimations, bool> emoting =
                AccessTools.FieldRefAccess<CharacterAnimations, bool>(emotingField);
            AccessTools.FieldRef<CharacterAnimations, float> sinceEmoteStart =
                AccessTools.FieldRefAccess<CharacterAnimations, float>(sinceEmoteStartField);
            Action<CharacterAnimations> setAnimSpeed =
                ScaleExperimentalEmit.CreateDelegate<Action<CharacterAnimations>>(setAnimSpeedMethod);
            if (character == null || emoting == null || sinceEmoteStart == null || setAnimSpeed == null)
                throw new InvalidOperationException("RemoteRagdollLodAnimator FieldRef creation failed");

            // Hashed before Acquire, not after. Animator.StringToHash marshals the string and calls
            // straight into native code (Animator.cs:1981-2002, forwarding to the extern
            // StringToHash_Injected at :2938), so it is a statement that can throw and therefore has
            // to sit on the near side of the shared helper's reference count. The previous order put
            // these two lines after Acquire under a comment claiming nothing below it could throw,
            // which was wrong; it only happened to be harmless because this patch registers a Reset
            // and that Reset releases whenever _acquired is set.
            int emoteHash = Animator.StringToHash("Emote");
            int inWaterHash = Animator.StringToHash("InWater");

            // Acquired last: it is now genuinely the final statement here that can throw (it runs
            // RemoteRagdollLod.ValidateStructure and builds the shared accessors), so a failed
            // Initialize never leaves a reference on the shared helper. Everything below is a plain
            // field assignment.
            RemoteRagdollLod.Acquire();
            _acquired = true;
            _character = character;
            _emoting = emoting;
            _sinceEmoteStart = sinceEmoteStart;
            _setAnimSpeed = setAnimSpeed;
            _emoteHash = emoteHash;
            _inWaterHash = inWaterHash;
        }

        internal static MethodInfo ResolveAndValidateTarget()
        {
            return ScaleValidation.RequireInstanceMethod(
                typeof(CharacterAnimations), "Update", typeof(void), Type.EmptyTypes, requirePublic: false);
        }

        internal static void Reset()
        {
            _character = null;
            _emoting = null;
            _sinceEmoteStart = null;
            _setAnimSpeed = null;
            _emoteHash = 0;
            _inWaterHash = 0;
            if (_acquired)
            {
                _acquired = false;
                RemoteRagdollLod.Release();
            }
        }

        internal static bool Prefix(CharacterAnimations __instance, bool __runOriginal)
        {
            // Another Prefix on CharacterAnimations.Update may already have decided to skip it.
            // HarmonyX calls every Prefix and only combines their verdicts afterwards, so reading
            // __runOriginal is what keeps this lane exclusive with any other patch on the same target.
            // No such patch exists in this plugin today, but the replacement below writes Animator
            // parameters and advances throwTime/sinceEmoteStart, so running it after someone else has
            // already suppressed the original would double-advance those timers.
            if (!__runOriginal)
                return false;

            AccessTools.FieldRef<CharacterAnimations, Character> characterRef = _character;
            if (characterRef == null)
            {
                // Still reachable, and still means the same thing as before: the one remaining
                // reflection-built accessor (the private CharacterAnimations.character FieldRef) is
                // missing, so the Prefix cannot even identify the owner and hands the frame back to
                // the original. The Animator accessors are no longer part of this condition because
                // they are compile-time calls now.
                RateLimitedDiagnostics.Hit("RemoteRagdollLodAnimator.accessor-unavailable");
                return true;
            }

            Character owner = characterRef(__instance);
            if (owner == null)
            {
                RateLimitedDiagnostics.Hit("RemoteRagdollLodAnimator.character-null");
                return true;
            }
            if (!RemoteRagdollLod.ShouldDowngrade(owner))
                return true;

            CharacterData data = owner.data;
            Character.CharacterRefs refs = owner.refs;
            Action<CharacterAnimations> setAnimSpeed = _setAnimSpeed;
            AccessTools.FieldRef<CharacterAnimations, bool> emotingRef = _emoting;
            AccessTools.FieldRef<CharacterAnimations, float> sinceEmoteStartRef = _sinceEmoteStart;
            // refs is a plain [Serializable] managed class (Character.cs:17-18), not a
            // UnityEngine.Object, and everything the original reads off it (Character.cs:44 etc.) is
            // an ordinary field load, so a plain reference null check is both necessary and
            // sufficient here.
            if (data == null || refs == null || refs.animationPositionTransform == null ||
                setAnimSpeed == null || emotingRef == null || sinceEmoteStartRef == null)
                return true;

            Bodypart hip = refs.hip;
            // animator derives from UnityEngine.Object, and every use of it - both the original's
            // (animator.runtimeAnimatorController at CharacterAnimations.cs:169, the parameter writes
            // at :176-284) and this replacement's SetBool/SetFloat - crosses into native code, which
            // throws once the native side is gone. So this must use Unity's == semantics, which also
            // rejects a destroyed-but-non-null wrapper, rather than a reference comparison.
            Animator animator = refs.animator;
            if (hip == null || hip.transform == null || animator == null || owner.input == null)
                return true;

            refs.animationPositionTransform.position = hip.transform.position;
            setAnimSpeed(__instance);
            float dt = Time.deltaTime;
            __instance.throwTime -= dt;
            if (__instance.throwTime <= 0f)
                __instance.throwTime = 0f;
            sinceEmoteStartRef(__instance) += dt;

            if (emotingRef(__instance) &&
                (sinceEmoteStartRef(__instance) > 2f ||
                 (sinceEmoteStartRef(__instance) > 0.7f &&
                  (owner.input.movementInput.magnitude > 0.1f || owner.input.jumpWasPressed ||
                   data.sinceGrounded > 0.2f))))
            {
                animator.SetBool(_emoteHash, false);
                emotingRef(__instance) = false;
            }

            animator.SetFloat(_inWaterHash, data.inWater);
            data.myersDistance = 1000f;
            data.inWater = 1f;
            return false;
        }
    }
}
