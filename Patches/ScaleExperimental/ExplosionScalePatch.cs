using System;
using System.Collections.Generic;
using System.Reflection;
using PeakSafeOptimizer.Config;
using PeakSafeOptimizer.Diagnostics;
using PeakSafeOptimizer.Patches.Scale;
using UnityEngine;

namespace PeakSafeOptimizer.Patches.ScaleExperimental
{
    /// <summary>
    /// Explosion orb count reduction for ExplosionEffect.GetPoints() (audited source
    /// ExplosionEffect.cs:56-79). GetPoints runs once per ExplosionEffect instance from Start
    /// (ExplosionEffect.cs:34-37) and precomputes
    /// 1 + explosionPointCount + subExplosionPointCount * explosionPointCount explosion orbs. Each
    /// orb later costs one Instantiate of the orb prefab plus GetComponentInChildren&lt;Animator&gt;,
    /// GetComponentInChildren&lt;MeshRenderer&gt; and a MaterialPropertyBlock round trip
    /// (ExplosionEffect.cs:42-53), so the orb count is what turns "several sticks of dynamite going
    /// off together" into a same-frame instantiate/Mecanim spike. Patching GetPoints covers every
    /// explosion source, because Dynamite.RPC_Explode (Dynamite.cs:102),
    /// CharacterMovement.RocketExplodeRPC (CharacterMovement.cs:962),
    /// ItemCooking.RPC_CookingExplode (ItemCooking.cs:274), GhostBall.Explode (GhostBall.cs:442) and
    /// Rocketpack.LightRoutine (Rocketpack.cs:60) all locally Instantiate an explosion prefab that
    /// carries this component.
    ///
    /// Three caps are combined, and every one of them can only ever lower the orb count:
    ///   1. the base caps 3 / 1 against the vanilla 4 / 2, i.e. 7 orbs instead of 13;
    ///   2. a cluster tier: when other explosions were registered within ClusterWindowSeconds and
    ///      ClusterRadius of this one, the caps drop to 2 / 1 (5 orbs) from one neighbour on and to
    ///      1 / 0 (2 orbs) from three neighbours on;
    ///   3. a distance tier relative to the local viewpoint: 1 / 0 (2 orbs) from NearDistance on,
    ///      0 / 0 from FarDistance on.
    /// The two tiers are merged by taking the lower cap of the two, and the merged result is finally
    /// clamped to [0, prefab value], so the patch can never raise a count and never go negative.
    ///
    /// The cluster tier additionally shrinks the orbs, and that - not the orb count - is what makes a
    /// dense pile of dynamite playable. Measured on a 4GB GPU: ten sticks detonating inside one metre
    /// still ran at about five frames per second with the count reduction alone, because the orb count
    /// is not what the frame is spent on. Each orb is an animated transparent sphere of roughly ten
    /// metres across (explosionRadius = 5f, ExplosionEffect.cs:14), so an observer standing in the
    /// pile has every surviving orb covering the entire screen. The cost is orb count times screen
    /// area, and only the second factor was still untouched.
    ///
    /// <c>baseScale</c> (ExplosionEffect.cs:12) is the field that sets it: the orb prefab is scaled by
    /// <c>orb.size * baseScale</c> when it spawns (ExplosionEffect.cs:47), and that assignment is the
    /// only read of the field anywhere in Assembly-CSharp. It feeds no damage path - the blast radius,
    /// knockback and status come from a separate AOE component with its own <c>range</c> - so scaling
    /// it changes how big the fireball looks and nothing else. It is only ever lowered, and only on
    /// the cluster tiers, so a single explosion keeps its authored size exactly.
    ///
    /// Why shrinking a clustered explosion reads as almost unchanged: the tier requires the
    /// neighbours to be within ClusterRadius, i.e. the fireballs are largely coincident, and what the
    /// player sees is their union. Ten overlapping spheres at 60% radius still produce a fireball
    /// wider than any single explosion, while costing roughly a third of the fill per orb.
    ///
    /// The 0 / 0 tier is still a visible explosion: GetPoints always allocates
    /// explosionPoints[0] (ExplosionEffect.cs:58-60), so the centre orb - the flash - is unaffected.
    ///
    /// The Prefix always returns true. Skipping GetPoints would leave the private explosionPoints
    /// array null and the foreach in Start (ExplosionEffect.cs:37-41) would throw, so the original
    /// method is always allowed to run and only its two public int inputs are lowered beforehand.
    ///
    /// Hot-path discipline: the cluster history is a fixed 16-entry ring buffer of pre-allocated
    /// arrays, so an explosion costs at most 16 float compares plus 16 squared-distance compares and
    /// allocates nothing. No LINQ, no reflection, no per-explosion logging.
    ///
    /// Explicitly out of scope, by design: clearing explosion points would delete visuals and is
    /// forbidden, and the MaterialPropertyBlock allocation happens inside the compiler-generated
    /// IExplode local function nested in Start(), which is the same class of fragile target that
    /// already failed for RopeRendering in a multi-mod environment, so it is deliberately not patched.
    /// </summary>
    internal static class ExplosionScalePatch
    {
        /// <summary>Explosion orb array type of the field the original method always allocates.</summary>
        private const string ExplosionOrbArrayTypeName = "ExplosionOrb[]";

        private const int RingCapacity = 16;
        /// <summary>
        /// Neighbour counts that select the cluster tiers. The current explosion is recorded after it
        /// is counted, so the first one in a pile always sees zero and keeps full base behaviour;
        /// starting the light tier at one neighbour is what makes the second explosion onwards - i.e.
        /// every explosion that is actually piling fill on top of another - take a reduction.
        /// </summary>
        private const int ClusterLightNeighbours = 1;
        private const int ClusterHeavyNeighbours = 3;

        /// <summary>
        /// Orb size multipliers per cluster tier. Screen area goes with the square, so 0.85 costs
        /// about 72% of the fill per orb and 0.6 about 36%. Applied on top of the count reduction, so
        /// the two multiply.
        /// </summary>
        private const float ClusterLightScale = 0.85f;
        private const float ClusterHeavyScale = 0.6f;

        /// <summary>
        /// Base caps applied to every explosion. Vanilla is 4 / 2, i.e.
        /// 1 + 4 + 2 * 4 = 13 orbs; 3 / 1 gives 1 + 3 + 1 * 3 = 7.
        /// </summary>
        private const int PointCap = 3;
        private const int SubPointCap = 1;

        /// <summary>Two explosions count as clustered within this time and distance of each other.</summary>
        /// <remarks>
        /// The window has to outlast the orbs of the preceding explosion rather than just its
        /// detonation instant, because it is those orbs that the new ones are drawing over. A single
        /// explosion spawns its outermost orbs up to <c>maxDelay * 2</c> = one second after it starts
        /// (ExplosionEffect.cs:68-76) and they then play out their animation, so 2.5s is the span over
        /// which two explosions genuinely share the screen.
        /// </remarks>
        private const float ClusterWindowSeconds = 2.5f;
        private const float ClusterRadius = 3.5f;

        /// <summary>
        /// Distance tiers measured from the local viewpoint: below Near nothing extra is dropped,
        /// between Near and Far only the centre orb plus one ring remains, beyond Far only the centre.
        /// </summary>
        private const float NearDistance = 50f;
        private const float FarDistance = 120f;

        /// <summary>
        /// Ring buffer of recent explosion origins. Both arrays are allocated once at type
        /// initialization and only ever overwritten in place, so the Prefix allocates nothing.
        /// An unused slot is marked by a timestamp of float.NegativeInfinity, which is always
        /// outside the window; a plain 0f default would collide with Time.time near scene start.
        /// </summary>
        private static readonly Vector3[] _clusterPositions = new Vector3[RingCapacity];
        private static readonly float[] _clusterTimes = CreateEmptyTimes();
        private static int _clusterWriteIndex;
        private static int _clusterSceneHandle = int.MinValue;

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

            // Proof of the "no NRE" argument: GetPoints is still the producer of the private orb
            // array that Start consumes, and this patch never prevents GetPoints from running.
            ScaleValidation.RequireInstanceFieldByTypeName(
                typeof(ExplosionEffect), "explosionPoints", ExplosionOrbArrayTypeName);
            ScaleValidation.RequireInstanceMethod(
                typeof(ExplosionEffect), "Start", typeof(void), Type.EmptyTypes, requirePublic: false);
            ScaleValidation.RequireInheritedInstanceProperty(
                typeof(ExplosionEffect), "transform", typeof(Transform));

            // The orb size multiplier. Public and serialized like the two counts, read exactly once -
            // at the localScale assignment in the IExplode local function (ExplosionEffect.cs:47) -
            // and by nothing else in the assembly, which is what makes lowering it purely visual.
            FieldInfo baseScaleField = ScaleValidation.RequireInstanceField(
                typeof(ExplosionEffect), "baseScale", typeof(float));
            if (!baseScaleField.IsPublic)
                throw new InvalidOperationException(
                    "ExplosionEffect.baseScale is no longer a public instance field");
            // Proof that the size the orbs are scaled by is still the authored per-orb size times
            // this field, i.e. that lowering it lowers every orb of this explosion uniformly.
            ScaleValidation.RequireInstanceField(
                typeof(ExplosionEffect), "explosionRadius", typeof(float));
            ScaleValidation.RequireInstanceField(
                typeof(ExplosionEffect), "childSizeFactor", typeof(float));

            // Distance tier: primary viewpoint, then the local character fallback.
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
            {
                _clusterPositions[i] = Vector3.zero;
                _clusterTimes[i] = float.NegativeInfinity;
            }
            _clusterWriteIndex = 0;
            _clusterSceneHandle = int.MinValue;
        }

        internal static bool Prefix(ExplosionEffect __instance)
        {
            int sceneHandle = __instance.gameObject.scene.handle;
            if (_clusterSceneHandle != sceneHandle)
            {
                Reset();
                _clusterSceneHandle = sceneHandle;
            }

            int currentPointCount = __instance.explosionPointCount;
            int currentSubPointCount = __instance.subExplosionPointCount;
            if (currentPointCount < 0 || currentSubPointCount < 0)
            {
                // An anomalous negative prefab count would invert the clamp bounds (Mathf.Clamp with
                // min > max returns max). Leave the original untouched to preserve its behavior.
                RateLimitedDiagnostics.Hit("ExplosionScale.negative-prefab-count");
                return true;
            }

            int pointCap = PointCap;
            int subPointCap = SubPointCap;
            float scaleFactor = 1f;

            Vector3 position = __instance.transform.position;
            float now = Time.time;
            int neighbours = CountClusterNeighbours(position, now);
            Record(position, now);

            if (neighbours >= ClusterHeavyNeighbours)
            {
                pointCap = Math.Min(pointCap, 1);
                subPointCap = Math.Min(subPointCap, 0);
                scaleFactor = ClusterHeavyScale;
            }
            else if (neighbours >= ClusterLightNeighbours)
            {
                pointCap = Math.Min(pointCap, 2);
                subPointCap = Math.Min(subPointCap, 1);
                scaleFactor = ClusterLightScale;
            }

            if (TryGetViewpoint(out Vector3 viewpoint))
            {
                float distanceSqr = (position - viewpoint).sqrMagnitude;
                // NaN coordinates make both comparisons false, which means "no distance tier".
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
                // This is the only branch that changes visuals, and it only ever lowers the count.
                // Not accounted as a fault, so no diagnostics here: dense explosions would otherwise
                // hit a diagnostics counter on every single explosion.
            }

            if (scaleFactor < 1f)
            {
                float currentScale = __instance.baseScale;
                float reduced = currentScale * scaleFactor;
                // Only ever lower it. A zero or negative authored value, or a NaN, fails this
                // comparison and is passed through to the original untouched.
                if (reduced < currentScale)
                    __instance.baseScale = reduced;
            }

            return true;
        }

        /// <summary>
        /// Number of recorded explosions that are both inside the time window and inside the cluster
        /// radius of <paramref name="position"/>. The current explosion is not part of the buffer
        /// yet, so it cannot count itself. A timestamp in the future means Time.time went backwards
        /// (scene reload, timeline reset), and such an entry is treated as expired.
        /// </summary>
        private static int CountClusterNeighbours(Vector3 position, float now)
        {
            const float radiusSqr = ClusterRadius * ClusterRadius;
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

        /// <summary>
        /// Local viewpoint for the distance tier: the main camera when it exists, otherwise the local
        /// character's torso. The torso is read through CharacterRagdoll.partDict rather than
        /// Character.Center (Character.cs:266), which is the same value without the
        /// KeyNotFoundException that GetBodypart raises before the ragdoll registered its parts
        /// (Character.cs:1299-1302). No viewpoint means no distance tier at all.
        /// </summary>
        private static bool TryGetViewpoint(out Vector3 viewpoint)
        {
            viewpoint = Vector3.zero;
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

        private static float[] CreateEmptyTimes()
        {
            var times = new float[RingCapacity];
            for (int i = 0; i < times.Length; i++)
                times[i] = float.NegativeInfinity;
            return times;
        }
    }
}
