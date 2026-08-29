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
    /// orb costs one Instantiate of the orb prefab plus GetComponentInChildren&lt;Animator&gt;,
    /// GetComponentInChildren&lt;MeshRenderer&gt;, one <c>new MaterialPropertyBlock()</c> and a
    /// GetPropertyBlock/SetPropertyBlock round trip (ExplosionEffect.cs:45-52).
    ///
    /// Those per-orb costs are not paid in one frame: Start launches a coroutine per orb which waits
    /// <c>orb.delay / (speed * orb.speed)</c> first (ExplosionEffect.cs:42-44). With the shipped
    /// serialized values - speed 3.0, minDelay/maxDelay 0.3/0.5, orb.speed drawn from
    /// minSpeed/maxSpeed 0.75/1.25 - the first ring lands at 0.3..0.5 / (3 * 0.75..1.25), i.e.
    /// 0.08..0.22s, and the second ring at 0.6..1.0 / (3 * 1.5..2.5), the same 0.08..0.22s
    /// (ExplosionEffect.cs:68,76 add the parent's delay and speed). So one explosion finishes
    /// spawning in about a fifth of a second, not the "roughly one second" an earlier version of this
    /// comment claimed by forgetting the speed divisor. The same-frame pressure comes from several
    /// explosions overlapping, and the sustained cost is the fill of all live orbs at once - each orb
    /// lives 20s (RemoveAfterSeconds on the orb prefab) and the VFX root 30s.
    ///
    /// Coverage (verified against the shipped assets, not from code): the component only exists on
    /// the three Dynamite-family prefabs. Decoding every MonoBehaviour in all 16 .assets files and
    /// all 14 levels, and reverse-resolving every explosionPrefab PPtr, gives exactly three
    /// ExplosionEffect instances in the whole game:
    ///   VFX_DynamiteExplosion       baseScale 1.0, explosionPointCount 3, subExplosionPointCount 2;
    ///   VFX_DynamiteExplosionSmall  baseScale 0.5, 3 / 0;
    ///   VFX_DynamiteExplosionFae    baseScale 0.5, 3 / 0.
    /// The call sites that reach them are Dynamite.RPC_Explode (Dynamite.cs:102) - manual detonation,
    /// including the dynamite that levels place directly and the spawners that instantiate the same
    /// item prefab - and ItemCooking.RPC_CookingExplode (ItemCooking.cs:274), which covers dynamite,
    /// the cursed skull, the scout cannon, lanterns, flares, heat packs, the portable stovetop and
    /// candles.
    ///
    /// Everything else that explodes is outside this patch, and there is more of it than of dynamite.
    /// CharacterMovement.RocketExplodeRPC (CharacterMovement.cs:962) and Rocketpack.LightRoutine
    /// (Rocketpack.cs:60) instantiate VFX_FireworkExplosion; GhostBall.Explode (GhostBall.cs:442)
    /// instantiates VFX_ExplosionGhost; ItemCooking on other items reaches VFX_BalloonPop,
    /// VFX_PandoraExplo, VFX_AntidoteExplo, VFX_CureAllExplo, a sunscreen variant, a snowball impact
    /// and an anti-sphere variant; the spore mushrooms spawn VFX_SporeExploExplo / VFX_PoisonExplo /
    /// VFX_SporeExplo through SpawnGameObject (200 spawn points in one level, 377 in another); and the
    /// geysers and flash plants carry 120 pre-placed objects literally named "Explosion". None of
    /// those carries an ExplosionEffect - their cost is in ParticleSystems and AOE components - so if
    /// an explosion still stutters, the spore mushrooms are a likelier scene than dynamite. This is an
    /// asset fact and would change if the prefabs were re-authored; it is not guaranteed by any
    /// signature the Initialize assertions can check.
    ///
    /// Three caps are combined, and every one of them can only ever lower the orb count:
    ///   1. the base caps 2 / 1, which turn the shipped 3 / 2 into 5 orbs instead of 10 on the main
    ///      dynamite and the shipped 3 / 0 into 3 orbs instead of 4 on Small/Fae;
    ///   2. a cluster tier: when other explosions were registered within ClusterWindowSeconds and
    ///      ClusterRadius of this one, the caps drop to 2 / 1 from one neighbour on - which, at the
    ///      current base caps, is no count change at all, so the light tier is effectively
    ///      scale-only - and to 1 / 0 (2 orbs) from three neighbours on;
    ///   3. a distance tier relative to the local viewpoint: 1 / 0 (2 orbs) from NearDistance on,
    ///      0 / 0 (the centre orb alone) from FarDistance on.
    /// The two tiers are merged by taking the lower cap of the two, and the merged result is finally
    /// clamped to [0, prefab value], so the patch can never raise a count and never go negative.
    ///
    /// The cluster tier additionally shrinks the orbs via <c>baseScale</c> (ExplosionEffect.cs:12),
    /// whose only read anywhere in Assembly-CSharp is the orb root's
    /// <c>localScale = Vector3.one * (orb.size * baseScale)</c> at ExplosionEffect.cs:47. The world
    /// size of the orb's sphere is that root scale times the local scale of the <c>ExploSphere</c>
    /// child (4.44) times the AABB extent of its Icosphere mesh (1.373), i.e. about 12.2m across for
    /// the main dynamite orb and 6.1m for Small/Fae (their baseScale is 0.5).
    /// <c>explosionRadius</c> (6.0 on the main prefab, 1.0 on Small/Fae - not the 5f code default)
    /// plays no part in that: it only scales where the orbs are scattered (ExplosionEffect.cs:66,74)
    /// and the editor gizmo. An observer standing inside a pile of dynamite therefore has every
    /// surviving orb covering most of the screen, and sphere fill goes with the square of the size,
    /// so 0.85 is about 72% of that fill and 0.6 about 36%.
    ///
    /// What the shrink does NOT reach, now measured rather than assumed: each orb also carries a
    /// <c>Particle System</c> and a <c>Plume</c>, and both are authored with
    /// <c>scalingMode = Local</c>, which by definition ignores parent scale - and their own transforms
    /// are at scale 1. So particle sizes (0.2..0.35 and 15..20), burst counts, lifetimes and
    /// maxParticles (1000 each) are completely unaffected by <c>baseScale</c>. The shrink reduces
    /// exactly one of the orb's three renderable parts. The count reduction is what cuts all three at
    /// once - sphere, both particle systems, and the screen shake below. How the frame time splits
    /// between sphere fill and particle fill is still unmeasured, so neither is claimed to dominate.
    ///
    /// Second visual cost, besides smaller and fewer fireballs: screen shake weakens in proportion
    /// to the orb count. Every orb prefab (ExplosionOrb, ExplosionOrbSmall, ExplosionOrbFae) carries
    /// two AddScreenshake children - IntenseShake with amount 10, duration 0.3, scale 12, range 60
    /// and WideShake with amount 2, duration 0.2, scale 12, range 300 - both serialized with
    /// auto = true and positional = true. AddScreenshake.Start fires Shake() by itself when auto is
    /// set (AddScreenshake.cs:28-31), and the positional path calls
    /// GamefeelHandler.AddPerlinShakeProximity (GamefeelHandler.cs:56-69), which appends another
    /// shake to the perlin spring on every call rather than replacing the current one. So each orb
    /// contributes two stacked shakes: the main dynamite's vanilla 10 orbs stack 20, the capped 5
    /// stack 10, and the heavy cluster tier's 2 orbs stack 4. The camera kick of an explosion is
    /// audibly and visibly softer as a result. Damage, knockback and status are unaffected - those
    /// come from the AOE component on the sibling VFX root, which does its own
    /// Physics.OverlapSphere with its own range and reads neither baseScale nor either count.
    ///
    /// Why shrinking a clustered explosion reads as almost unchanged: the tier requires the
    /// neighbours to be within ClusterRadius, i.e. the fireballs are largely coincident, and what the
    /// player sees is their union. Ten overlapping spheres at 60% radius still produce a fireball
    /// wider than any single explosion.
    ///
    /// The 0 / 0 tier is still a visible explosion: GetPoints always allocates
    /// explosionPoints[0] (ExplosionEffect.cs:58-60), so the centre orb - the flash - is unaffected.
    ///
    /// The Prefix always returns true. Skipping GetPoints would leave the private explosionPoints
    /// array null and the foreach in Start (ExplosionEffect.cs:37-41) would throw, so the original
    /// method is always allowed to run and only its three public serialized inputs are lowered
    /// beforehand.
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
        /// the two multiply. How much of the frame this actually recovers is unmeasured - see the
        /// scalingMode caveat on the type.
        /// </summary>
        private const float ClusterLightScale = 0.85f;
        private const float ClusterHeavyScale = 0.6f;

        /// <summary>
        /// Base caps applied to every explosion. The code defaults on the fields are 4 / 2
        /// (ExplosionEffect.cs:30-32), but serialized prefab values override them and all three
        /// shipped prefabs carry 3 / 2 (VFX_DynamiteExplosion) or 3 / 0
        /// (VFX_DynamiteExplosionSmall, VFX_DynamiteExplosionFae). Vanilla runtime orb counts are
        /// therefore 1 + 3 + 2 * 3 = 10 on the main dynamite and 1 + 3 + 0 = 4 on Small/Fae.
        /// Capping at 2 / 1 gives 1 + 2 + 1 * 2 = 5 and 1 + 2 + 0 = 3 respectively. The caps are also
        /// defensive: if a future asset revision or another mod raises the serialized counts, the
        /// clamp still holds them down to these values.
        /// </summary>
        private const int PointCap = 2;
        private const int SubPointCap = 1;

        /// <summary>Two explosions count as clustered within this time and distance of each other.</summary>
        /// <remarks>
        /// The window has to outlast the orbs of the preceding explosion rather than just its
        /// detonation instant, because it is those orbs that the new ones are drawing over. Spawning
        /// itself is quick - about 0.22s for the whole set once the speed divisor is accounted for
        /// (see the class remarks) - but each orb then lives 20s under its own RemoteAfterSeconds and
        /// plays out its animation, and the VFX root lives 30s. So the span over which two explosions
        /// genuinely share the screen is set by orb lifetime, not by spawn spread. 2.5s is a judgement
        /// call well inside that: long enough to catch a pile thrown together, short enough that two
        /// deliberate detonations a few seconds apart are each treated on their own.
        ///
        /// Known tuning limitation, not addressed in this revision: 3.5m is one threshold for orbs of
        /// very different size - about 12.2 metres across on the main dynamite versus 6.1 on
        /// Small/Fae - and it is smaller than the AOE range the main dynamite actually damages with
        /// (12.0 as serialized). A single small explosion can therefore push a nearby main dynamite
        /// blast into the cluster tier even though the two barely overlap on screen.
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

        /// <summary>
        /// Scene the buffered positions belong to. 0 is Unity's invalid-scene handle and doubles as
        /// "nothing tracked yet", so no separate sentinel is needed.
        /// </summary>
        private static int _clusterSceneHandle;
        /// <summary>
        /// The handle that <see cref="_clusterSceneHandle"/> replaced on the last reset, and a latch
        /// that is set once we see that handle come back. Purely defensive against a third-party
        /// additive load: vanilla never has two scenes loaded that could both host an explosion (every
        /// SceneManager call in the game uses LoadSceneMode.Single, and map-segment progression is
        /// SetActive on subtrees of one Map object, MapHandler.cs:855-909 - the "Jumping from X to Y"
        /// line comes from :887 and loads nothing), so in a clean install this latch is unreachable.
        /// If a mod does load additively, two alternating handles would otherwise empty the ring
        /// buffer on every detonation and silently disable the cluster tier with no log to explain it.
        /// Once the flip is observed the handle tracking latches off and the buffer is left alone. The
        /// cost of that is bounded: entries expire on their own after ClusterWindowSeconds, so the only
        /// way a stale entry can matter is an explosion in the other scene within 2.5s and 3.5m of it,
        /// which is exactly the case the cluster tier is meant to catch anyway.
        /// </summary>
        private static int _clusterDisplacedSceneHandle;
        private static bool _clusterSceneHandleLatched;

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
            // at the orb root's localScale assignment in the IExplode local function
            // (ExplosionEffect.cs:47) - and by nothing else in the assembly, which is what makes
            // lowering it purely visual.
            FieldInfo baseScaleField = ScaleValidation.RequireInstanceField(
                typeof(ExplosionEffect), "baseScale", typeof(float));
            if (!baseScaleField.IsPublic)
                throw new InvalidOperationException(
                    "ExplosionEffect.baseScale is no longer a public instance field");
            // The other two floats GetPoints derives its orbs from. childSizeFactor is the per-orb
            // size that baseScale multiplies (ExplosionEffect.cs:62,68); explosionRadius only scales
            // where the orbs are scattered (ExplosionEffect.cs:66,74) and never reaches localScale.
            // Neither is written here - they are asserted so that a re-authored GetPoints whose
            // geometry inputs changed shape fails closed instead of being silently mis-tuned.
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
            _clusterSceneHandle = 0;
            _clusterDisplacedSceneHandle = 0;
            _clusterSceneHandleLatched = false;
        }

        internal static bool Prefix(ExplosionEffect __instance)
        {
            TrackScene(__instance.gameObject.scene.handle);

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
        /// Drops the buffered positions when explosions start coming from a different scene, so that
        /// coordinates from a torn-down level can never be counted as neighbours of a new one.
        /// Deliberately conservative: handle 0 (an object outside any loaded scene) is ignored rather
        /// than treated as a scene change, and the very first flip back to a previously replaced
        /// handle - the signature of two additively loaded scenes taking turns - latches the tracking
        /// off and reports it once, instead of resetting the buffer on every detonation and disabling
        /// the cluster tier without a trace. See the field docs for why the resulting staleness is
        /// harmless.
        /// </summary>
        private static void TrackScene(int sceneHandle)
        {
            if (_clusterSceneHandleLatched || sceneHandle == 0 || sceneHandle == _clusterSceneHandle)
                return;

            if (sceneHandle == _clusterDisplacedSceneHandle)
            {
                _clusterSceneHandleLatched = true;
                RateLimitedDiagnostics.Hit("ExplosionScale.scene-handle-thrash");
                return;
            }

            int displaced = _clusterSceneHandle;
            Reset();
            _clusterSceneHandle = sceneHandle;
            _clusterDisplacedSceneHandle = displaced;
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
        ///
        /// The whole resolution is wrapped in a catch-all because a destroyed native object behind
        /// any of these managed references, or a dictionary mutated from another thread, can still
        /// throw despite the null checks. An exception escaping a Prefix propagates through Harmony
        /// into ExplosionEffect.Start, which would lose the entire explosion's visuals and log a
        /// stack trace per detonation. Losing the distance tier for one explosion is the cheaper
        /// failure, so anything unexpected is counted once and reported as "no viewpoint".
        /// </summary>
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
