using System;
using System.Collections.Generic;
using System.Reflection;
using PeakSafeOptimizer.Diagnostics;
using PeakSafeOptimizer.Patches.Scale;
using UnityEngine;

namespace PeakSafeOptimizer.Patches.ScaleExperimental
{
    /// <summary>
    /// Lowers the two serialized counts and baseScale before GetPoints runs, and always lets GetPoints
    /// run: skipping it would leave explosionPoints null and Start's foreach would throw. The shipped
    /// prefabs carry 3/2 and 3/0, not the 4/2 field defaults, so 10 and 4 orbs become 5 and 3; every tier
    /// is a Math.Min clamped to [0, prefab value], and a negative prefab count is passed through because
    /// Clamp with min > max inverts. Cost: fewer orbs, smaller orbs in a cluster, and two AddScreenshake
    /// children per orb, so the kick weakens with the count. Only the sphere shrinks; the orb particles
    /// are scalingMode Local. Cluster radius is this explosion's own fireball, baseScale * 6.0962m, else
    /// 3.5m; the 2.5s window needs no scene tracking because entries expire long before any scene load.
    /// The distance tier measures from MainCamera, not observedCharacter: screen coverage is a camera
    /// property. Counts, orb hierarchy and particle scalingMode are prefab facts no assertion reaches.
    /// </summary>
    internal static class ExplosionScalePatch
    {
        private const string ExplosionOrbArrayTypeName = "ExplosionOrb[]";

        private const int RingCapacity = 16;
        private const int ClusterLightNeighbours = 1;
        private const int ClusterHeavyNeighbours = 3;

        private const int ClusterLightPointCap = 2;
        private const int ClusterLightSubPointCap = 1;
        private const int ClusterHeavyPointCap = 1;
        private const int ClusterHeavySubPointCap = 0;

        private const float ClusterLightScale = 0.85f;
        private const float ClusterHeavyScale = 0.6f;

        private const int PointCap = 2;
        private const int SubPointCap = 1;

        private const float OrbRadiusPerBaseScale = 6.0962f;

        private const float ClusterWindowSeconds = 2.5f;
        private const float ClusterRadius = 3.5f;

        private const float NearDistance = 50f;
        private const float FarDistance = 120f;

        private static readonly Vector3[] _clusterPositions = new Vector3[RingCapacity];
        private static readonly float[] _clusterTimes = CreateEmptyTimes();
        private static int _clusterWriteIndex;

        internal static void Initialize()
        {
            ResolveAndValidateTarget();

            FieldInfo pointCountField = ScaleValidation.RequireInstanceField(
                typeof(ExplosionEffect), "explosionPointCount", typeof(int));
            FieldInfo subPointCountField = ScaleValidation.RequireInstanceField(
                typeof(ExplosionEffect), "subExplosionPointCount", typeof(int));
            if (!pointCountField.IsPublic || !subPointCountField.IsPublic)
                throw new InvalidOperationException(
                    "ExplosionEffect explosionPointCount/subExplosionPointCount are no longer public instance fields");

            ScaleValidation.RequireInstanceFieldByTypeName(
                typeof(ExplosionEffect), "explosionPoints", ExplosionOrbArrayTypeName);
            ScaleValidation.RequireInstanceMethod(
                typeof(ExplosionEffect), "Start", typeof(void), Type.EmptyTypes, requirePublic: false);
            ScaleValidation.RequireInheritedInstanceProperty(
                typeof(ExplosionEffect), "transform", typeof(Transform));

            FieldInfo baseScaleField = ScaleValidation.RequireInstanceField(
                typeof(ExplosionEffect), "baseScale", typeof(float));
            if (!baseScaleField.IsPublic)
                throw new InvalidOperationException(
                    "ExplosionEffect.baseScale is no longer a public instance field");

            ScaleValidation.RequireStaticField(typeof(MainCamera), "instance", typeof(MainCamera));
            ScaleValidation.RequireInheritedInstanceProperty(
                typeof(MainCamera), "transform", typeof(Transform));
            ScaleValidation.RequireStaticField(typeof(Character), "localCharacter", typeof(Character));
            ScaleValidation.RequireInstanceField(typeof(Character), "refs", typeof(Character.CharacterRefs));
            ScaleValidation.RequireInstanceProperty(typeof(Character), "Center", typeof(Vector3));
            ScaleValidation.RequireInstanceField(
                typeof(Character.CharacterRefs), "ragdoll", typeof(CharacterRagdoll));
            ScaleValidation.RequireInstanceField(
                typeof(CharacterRagdoll), "partDict", typeof(Dictionary<BodypartType, Bodypart>));
            ScaleValidation.RequireInheritedInstanceProperty(typeof(Bodypart), "transform", typeof(Transform));
            if (!Enum.IsDefined(typeof(BodypartType), BodypartType.Torso))
                throw new InvalidOperationException("BodypartType.Torso is no longer defined");
        }

        internal static MethodInfo ResolveAndValidateTarget()
        {
            return ScaleValidation.RequireInstanceMethod(
                typeof(ExplosionEffect), "GetPoints", typeof(void), Type.EmptyTypes,
                requirePublic: false);
        }

        internal static void Reset()
        {
            for (int i = 0; i < RingCapacity; i++)
                _clusterTimes[i] = float.NegativeInfinity;
            _clusterWriteIndex = 0;
        }

        internal static bool Prefix(ExplosionEffect __instance)
        {
            int currentPointCount = __instance.explosionPointCount;
            int currentSubPointCount = __instance.subExplosionPointCount;
            if (currentPointCount < 0 || currentSubPointCount < 0)
            {
                RateLimitedDiagnostics.Hit("ExplosionScale.negative-prefab-count");
                return true;
            }

            int pointCap = PointCap;
            int subPointCap = SubPointCap;
            float scaleFactor = 1f;

            float currentScale = __instance.baseScale;

            Vector3 position = __instance.transform.position;
            float now = Time.time;
            int neighbours = CountClusterNeighbours(
                position, now, ResolveClusterRadiusSqr(currentScale));
            Record(position, now);

            if (neighbours >= ClusterHeavyNeighbours)
            {
                pointCap = Math.Min(pointCap, ClusterHeavyPointCap);
                subPointCap = Math.Min(subPointCap, ClusterHeavySubPointCap);
                scaleFactor = ClusterHeavyScale;
            }
            else if (neighbours >= ClusterLightNeighbours)
            {
                pointCap = Math.Min(pointCap, ClusterLightPointCap);
                subPointCap = Math.Min(subPointCap, ClusterLightSubPointCap);
                scaleFactor = ClusterLightScale;
            }

            if (TryGetViewpoint(out Vector3 viewpoint))
            {
                float distanceSqr = (position - viewpoint).sqrMagnitude;
                if (distanceSqr >= FarDistance * FarDistance)
                {
                    pointCap = Math.Min(pointCap, 0);
                    subPointCap = Math.Min(subPointCap, 0);
                }
                else if (distanceSqr >= NearDistance * NearDistance)
                {
                    pointCap = Math.Min(pointCap, 1);
                    subPointCap = Math.Min(subPointCap, 0);
                }
            }

            pointCap = Mathf.Clamp(pointCap, 0, currentPointCount);
            subPointCap = Mathf.Clamp(subPointCap, 0, currentSubPointCount);
            if (pointCap != currentPointCount || subPointCap != currentSubPointCount)
            {
                __instance.explosionPointCount = pointCap;
                __instance.subExplosionPointCount = subPointCap;
            }

            if (scaleFactor < 1f)
            {
                float reduced = currentScale * scaleFactor;
                if (reduced < currentScale)
                    __instance.baseScale = reduced;
            }

            return true;
        }

        private static float ResolveClusterRadiusSqr(float baseScale)
        {
            const float fallbackSqr = ClusterRadius * ClusterRadius;
            float radius = baseScale * OrbRadiusPerBaseScale;
            float radiusSqr = radius * radius;
            if (!(radius > 0f) || !(radiusSqr > 0f) || float.IsInfinity(radiusSqr))
                return fallbackSqr;
            return radiusSqr;
        }

        private static int CountClusterNeighbours(Vector3 position, float now, float radiusSqr)
        {
            int count = 0;
            for (int i = 0; i < RingCapacity; i++)
            {
                float stamp = _clusterTimes[i];
                if (stamp > now || now - stamp > ClusterWindowSeconds)
                    continue;
                if ((_clusterPositions[i] - position).sqrMagnitude <= radiusSqr)
                    count++;
            }
            return count;
        }

        private static void Record(Vector3 position, float now)
        {
            int slot = _clusterWriteIndex;
            _clusterPositions[slot] = position;
            _clusterTimes[slot] = now;
            slot++;
            _clusterWriteIndex = slot == RingCapacity ? 0 : slot;
        }

        private static bool TryGetViewpoint(out Vector3 viewpoint)
        {
            viewpoint = Vector3.zero;
            try
            {
                MainCamera camera = MainCamera.instance;
                if (camera != null)
                {
                    Transform cameraTransform = camera.transform;
                    if (cameraTransform != null)
                    {
                        viewpoint = cameraTransform.position;
                        return true;
                    }
                }

                Character local = Character.localCharacter;
                Character.CharacterRefs refs = local != null ? local.refs : null;
                CharacterRagdoll ragdoll = refs != null ? refs.ragdoll : null;
                Dictionary<BodypartType, Bodypart> parts = ragdoll != null ? ragdoll.partDict : null;
                if (parts == null || !parts.TryGetValue(BodypartType.Torso, out Bodypart torso) || torso == null)
                    return false;
                Transform torsoTransform = torso.transform;
                if (torsoTransform == null)
                    return false;
                viewpoint = torsoTransform.position;
                return true;
            }
            catch (Exception)
            {
                RateLimitedDiagnostics.Hit("ExplosionScale.viewpoint-unavailable");
                viewpoint = Vector3.zero;
                return false;
            }
        }

        private static float[] CreateEmptyTimes()
        {
            var times = new float[RingCapacity];
            for (int i = 0; i < times.Length; i++)
                times[i] = float.NegativeInfinity;
            return times;
        }
    }
}
