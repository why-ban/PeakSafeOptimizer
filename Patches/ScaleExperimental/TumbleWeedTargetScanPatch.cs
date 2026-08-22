using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using PeakSafeOptimizer.Config;
using PeakSafeOptimizer.Diagnostics;
using PeakSafeOptimizer.Patches.Scale;
using Photon.Pun;
using UnityEngine;

namespace PeakSafeOptimizer.Patches.ScaleExperimental
{
    /// <summary>
    /// Chase-direction reuse for TumbleWeed.FixedUpdate (audited source TumbleWeed.cs:34-46):
    ///
    ///   if (photonView.IsMine) {
    ///       Vector3 dir = -Vector3.right;
    ///       Character target = GetTarget();          // O(P) angle + distance scan
    ///       if (target) dir = (target.Center - transform.position).normalized;
    ///       rig.AddForce(dir * rollForce, ForceMode.Acceleration);
    ///   }
    ///
    /// GetTarget (TumbleWeed.cs:48-66) walks Character.AllCharacters every physics step and runs one
    /// Vector3.Angle plus one Vector3.Distance per character, so the desert cost is
    /// O(tumbleweeds x players) at 50Hz. This is the only Mesa-specific per-tick scan the audit
    /// found, and the user confirmed tumbleweeds measurably affect the framerate there.
    ///
    /// What this Prefix changes: the *target solve* runs once every N physics ticks per tumbleweed
    /// (default 5, ~0.1s) and the resolved target is reused in between. What it does not change: the
    /// AddForce call still happens every tick with the same magnitude, using the direction recomputed
    /// from the cached target's *current* Center, so a cached target that keeps moving still steers
    /// the tumbleweed continuously. A cached target that died or was destroyed forces an immediate
    /// rescan, and the no-target fallback keeps the audited -Vector3.right direction.
    ///
    /// OnCollisionEnter, the Fall/AddForceAtPosition/AddThorn chain and the IgnoreTarget coroutine are
    /// untouched, and a non-owned tumbleweed still does nothing (the audited IsMine gate is
    /// reproduced). The scan itself also switches to squared distance, which is exact for the audited
    /// `num2 &lt; num` comparison over non-negative distances.
    /// </summary>
    internal static class TumbleWeedTargetScanPatch
    {
        /// <summary>
        /// A tumbleweed re-solves its chase target once every this many physics ticks and reuses the
        /// previous one in between. Force is still applied every tick, and its direction is recomputed
        /// from the cached target's current Center, so 5 ticks (0.1s) costs no visible responsiveness.
        /// </summary>
        private const int Interval = 5;
        private const float MaxScanDistance = 300f;
        private const string RigidbodyTypeName = "UnityEngine.Rigidbody";

        private sealed class TargetState
        {
            internal int LastScanTick = int.MinValue;
            internal bool HasTarget;
            internal Character Target;
        }

        private static AccessTools.FieldRef<TumbleWeed, PhotonView> _photonView;
        private static Func<TumbleWeed, UnityEngine.Object> _rig;
        private static AccessTools.FieldRef<TumbleWeed, float> _rollForce;
        private static AccessTools.FieldRef<TumbleWeed, float> _maxAngle;
        private static Action<UnityEngine.Object, Vector3, int> _addForce;
        private static ConditionalWeakTable<TumbleWeed, TargetState> _states;

        internal static void Initialize()
        {
            ResolveAndValidateTarget();

            FieldInfo photonViewField = ScaleValidation.RequireInstanceField(
                typeof(TumbleWeed), "photonView", typeof(PhotonView));
            FieldInfo rigField = ScaleValidation.RequireInstanceFieldByTypeName(
                typeof(TumbleWeed), "rig", RigidbodyTypeName);
            FieldInfo rollForceField = ScaleValidation.RequireInstanceField(
                typeof(TumbleWeed), "rollForce", typeof(float));
            FieldInfo maxAngleField = ScaleValidation.RequireInstanceField(
                typeof(TumbleWeed), "maxAngle", typeof(float));

            // Proof that the audited scan is still the one being replaced.
            ScaleValidation.RequireInstanceMethodByTypeNames(
                typeof(TumbleWeed), "GetTarget", "Character", Array.Empty<string>());
            ScaleValidation.RequireStaticField(typeof(Character), "AllCharacters", typeof(List<Character>));
            ScaleValidation.RequireInstanceProperty(typeof(Character), "Center", typeof(Vector3));
            ScaleValidation.RequireInstanceField(typeof(Character), "data", typeof(CharacterData));
            ScaleValidation.RequireInstanceProperty(typeof(CharacterData), "dead", typeof(bool));
            ScaleValidation.RequireInheritedInstanceProperty(
                typeof(TumbleWeed), "transform", typeof(Transform));
            ScaleValidation.RequireStaticMethod(
                typeof(Vector3), "Angle", typeof(float), new[] { typeof(Vector3), typeof(Vector3) }, true);
            ScaleValidation.RequireStaticMethod(
                typeof(Vector3), "Distance", typeof(float), new[] { typeof(Vector3), typeof(Vector3) }, true);

            Type rigidbodyType = rigField.FieldType;
            MethodInfo addForce = rigidbodyType.GetMethod(
                "AddForce",
                BindingFlags.Public | BindingFlags.Instance,
                null,
                new[] { typeof(Vector3), ResolveForceModeType(rigidbodyType) },
                null);
            if (addForce == null || addForce.IsStatic || addForce.ReturnType != typeof(void))
                throw new MissingMethodException(rigidbodyType.FullName, "AddForce(Vector3, ForceMode)");

            AccessTools.FieldRef<TumbleWeed, PhotonView> photonView =
                AccessTools.FieldRefAccess<TumbleWeed, PhotonView>(photonViewField);
            Func<TumbleWeed, UnityEngine.Object> rig =
                ScaleEmit.CreateUnityObjectFieldGetter<TumbleWeed>(rigField);
            AccessTools.FieldRef<TumbleWeed, float> rollForce =
                AccessTools.FieldRefAccess<TumbleWeed, float>(rollForceField);
            AccessTools.FieldRef<TumbleWeed, float> maxAngle =
                AccessTools.FieldRefAccess<TumbleWeed, float>(maxAngleField);
            Action<UnityEngine.Object, Vector3, int> addForceDelegate =
                ScaleExperimentalEmit.CreateVector3EnumAction(rigidbodyType, addForce);
            if (photonView == null || rig == null || rollForce == null || maxAngle == null ||
                addForceDelegate == null)
            {
                throw new InvalidOperationException("TumbleWeedTargetScan accessor creation failed");
            }

            _photonView = photonView;
            _rig = rig;
            _rollForce = rollForce;
            _maxAngle = maxAngle;
            _addForce = addForceDelegate;
            _states = new ConditionalWeakTable<TumbleWeed, TargetState>();
        }

        internal static MethodInfo ResolveAndValidateTarget()
        {
            return ScaleValidation.RequireInstanceMethodByTypeNames(
                typeof(TumbleWeed), "FixedUpdate", typeof(void).FullName, Array.Empty<string>());
        }

        internal static void Reset()
        {
            _photonView = null;
            _rig = null;
            _rollForce = null;
            _maxAngle = null;
            _addForce = null;
            _states = null;
        }

        internal static bool Prefix(TumbleWeed __instance)
        {
            AccessTools.FieldRef<TumbleWeed, PhotonView> photonViewRef = _photonView;
            Func<TumbleWeed, UnityEngine.Object> rigRef = _rig;
            AccessTools.FieldRef<TumbleWeed, float> rollForceRef = _rollForce;
            AccessTools.FieldRef<TumbleWeed, float> maxAngleRef = _maxAngle;
            Action<UnityEngine.Object, Vector3, int> addForce = _addForce;
            ConditionalWeakTable<TumbleWeed, TargetState> states = _states;
            if (photonViewRef == null || rigRef == null || rollForceRef == null || maxAngleRef == null ||
                addForce == null || states == null || __instance == null)
            {
                return true;
            }

            PhotonView view = photonViewRef(__instance);
            if (view == null)
            {
                // The original dereferences photonView here; keep its behavior.
                return true;
            }
            if (!view.IsMine)
            {
                // Audited IsMine gate: a non-owned tumbleweed does nothing at all.
                return false;
            }

            UnityEngine.Object rig = rigRef(__instance);
            Transform self = __instance.transform;
            if (rig == null || self == null)
                return true;

            List<Character> all = Character.AllCharacters;
            if (all == null)
                return true;

            Vector3 position = self.position;
            TargetState state = states.GetOrCreateValue(__instance);
            int tick = Mathf.FloorToInt(Time.fixedTime / Time.fixedDeltaTime);
            // Rescan on: first sight, interval elapsed, a fixedTime reset (scene reload), or a cached
            // target that is gone / destroyed / dead. Everything else reuses the cached target and
            // still recomputes the direction from its current Center, so steering stays continuous.
            bool rescan = state.LastScanTick == int.MinValue ||
                tick - state.LastScanTick >= Interval ||
                tick < state.LastScanTick;
            if (!rescan && state.HasTarget)
            {
                Character cached = state.Target;
                if (cached == null || cached.data == null || cached.data.dead)
                    rescan = true;
            }

            if (rescan)
            {
                if (!TryFindTarget(all, position, maxAngleRef(__instance), out Character scanned))
                {
                    // A null roster entry is a state the original would dereference; hand the whole
                    // tick back before this Prefix has applied any force.
                    RateLimitedDiagnostics.Hit("TumbleWeedTargetScan.candidate-null");
                    return true;
                }
                state.Target = scanned;
                state.HasTarget = state.Target != null;
                state.LastScanTick = tick;
            }

            Character resolved = state.Target;
            Vector3 direction = -Vector3.right;
            if (resolved != null)
                direction = (resolved.Center - position).normalized;

            // ForceMode.Acceleration == 5 in UnityEngine.ForceMode; the audited call uses it.
            addForce(rig, direction * rollForceRef(__instance), 5);
            return false;
        }

        private static bool TryFindTarget(
            List<Character> all, Vector3 position, float maxAngle, out Character target)
        {
            float bestSqr = MaxScanDistance * MaxScanDistance;
            Character best = null;
            for (int i = 0; i < all.Count; i++)
            {
                Character candidate = all[i];
                if (candidate == null)
                {
                    target = null;
                    return false;
                }

                Vector3 delta = candidate.Center - position;
                if (Vector3.Angle(-Vector3.right, delta) > maxAngle)
                    continue;
                // Squared comparison is exact for the audited `distance < best` over non-negative
                // distances, and the 300f initial bound squares without overflow.
                float distanceSqr = delta.sqrMagnitude;
                if (distanceSqr < bestSqr)
                {
                    bestSqr = distanceSqr;
                    best = candidate;
                }
            }
            target = best;
            return true;
        }

        private static Type ResolveForceModeType(Type rigidbodyType)
        {
            Type forceMode = rigidbodyType.Assembly.GetType("UnityEngine.ForceMode", throwOnError: false);
            if (forceMode == null || !forceMode.IsEnum ||
                Enum.GetUnderlyingType(forceMode) != typeof(int) ||
                !Enum.IsDefined(forceMode, "Acceleration") ||
                Convert.ToInt32(Enum.Parse(forceMode, "Acceleration")) != 5)
            {
                throw new InvalidOperationException("UnityEngine.ForceMode.Acceleration structure mismatch");
            }
            return forceMode;
        }
    }
}
