using System;
using System.Collections.Generic;
using System.Reflection;
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
    /// <c>orb.delay / (speed * orb.speed)</c> first (ExplosionEffect.cs:42-44). The numbers below are
    /// serialized prefab values, not the code defaults - the code defaults are speed 1.0
    /// (ExplosionEffect.cs:8) and minDelay/maxDelay 0.4/0.5 (ExplosionEffect.cs:20-21), and this repo
    /// has been burned before by reading field initializers instead of the shipped assets. Only
    /// minSpeed/maxSpeed 0.75/1.25 (ExplosionEffect.cs:24-25) happen to match code and assets.
    /// With the serialized values - speed 3.0, minDelay/maxDelay 0.3/0.5, orb.speed drawn from
    /// minSpeed/maxSpeed 0.75/1.25 - the first ring lands at 0.3..0.5 / (3 * 0.75..1.25), i.e.
    /// 0.08..0.22s, and the second ring at 0.6..1.0 / (3 * 1.5..2.5), the same 0.08..0.22s
    /// (ExplosionEffect.cs:68,76 add the parent's delay and speed). The conclusion does not depend on
    /// which minDelay is right: substituting the code default 0.4 gives 0.4..0.5 / (3 * 0.75..1.25) =
    /// 0.107..0.222s for the first ring and 0.8..1.0 / (3 * 1.5..2.5) = 0.107..0.222s for the second,
    /// so either input puts the whole set on screen within about 0.22s. Only the lower bound moves.
    /// So one explosion finishes spawning in about a fifth of a second, not the "roughly one second"
    /// an earlier version of this comment claimed by forgetting the speed divisor. The same-frame
    /// pressure comes from several explosions overlapping, and the sustained cost is the fill of all
    /// live orbs at once - each orb lives 20s (RemoveAfterSeconds on the orb prefab) and the VFX root
    /// 30s.
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
    ///      one fireball radius of this one - about 6.1m for the main dynamite, 3.05m for Small/Fae,
    ///      derived from this explosion's own baseScale by ResolveClusterRadiusSqr - the caps drop to
    ///      ClusterLightPointCap / ClusterLightSubPointCap from one neighbour on, which at the current
    ///      base caps is no count change at all, so the light tier is scale-only; and to 1 / 0
    ///      (2 orbs) from three neighbours on;
    ///   3. a distance tier relative to the local viewpoint: 1 / 0 (2 orbs) from NearDistance on,
    ///      0 / 0 (the centre orb alone) from FarDistance on.
    /// The two tiers are merged by taking the lower cap of the two, and the merged result is finally
    /// clamped to [0, prefab value], so the patch can never raise a count and never go negative.
    ///
    /// The cluster tier additionally shrinks the orbs via <c>baseScale</c> (ExplosionEffect.cs:12),
    /// whose only read anywhere in Assembly-CSharp is the orb root's
    /// <c>localScale = Vector3.one * (orb.size * baseScale)</c> at ExplosionEffect.cs:47. The world
    /// size of the orb's sphere is that root scale times the local scale of the <c>ExploSphere</c>
    /// child (4.4401164) times the AABB extent of its Icosphere mesh (1.372998), i.e. about 12.19m
    /// across for the main dynamite orb and 6.10m for Small/Fae (their baseScale is 0.5). That product
    /// is also what the cluster threshold is derived from - see <see cref="OrbRadiusPerBaseScale"/>.
    /// <c>explosionRadius</c> (6.0 on the main prefab, 1.0 on Small/Fae - not the 5f code default)
    /// plays no part in either: it only scales where the orbs are scattered (ExplosionEffect.cs:66,74)
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
    /// shake to the perlin spring on every call rather than replacing the current one
    /// (PerlinShake.AddShake is a List.Add, PerlinShake.cs:28-36, and Update sums the whole list,
    /// :12-26). So each orb contributes two stacked shakes: the main dynamite's vanilla 10 orbs stack
    /// 20, the capped 5 stack 10, and the heavy cluster tier's 2 orbs stack 4. The camera kick of an
    /// explosion is audibly and visibly softer as a result. That shake measures its own falloff from
    /// Character.observedCharacter.Center (GamefeelHandler.cs:63-67), which is a different origin from
    /// the one the distance tier below uses - see TryGetViewpoint for why the camera is the right
    /// origin there and why the divergence costs nothing. Damage, knockback and status are unaffected -
    /// those come from the AOE component on the sibling VFX root, which does its own
    /// Physics.OverlapSphere with its own range (AOE.cs:88-94) and reads neither baseScale nor either
    /// count.
    ///
    /// Why shrinking a clustered explosion reads as almost unchanged: the tier requires the
    /// neighbours to be within one fireball radius, i.e. the fireballs are largely coincident, and what
    /// the player sees is their union. A pile of five detonations in the heavy tier still puts ten orbs
    /// on screen (2 each) at 60% radius, and their union is still wider than one unreduced explosion.
    /// The count that matters for the union is the sum across the pile, never the 2 orbs of any one
    /// explosion on its own.
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
    /// arrays, so an explosion costs at most 32 float compares (each slot tests both ends of the time
    /// window, CountClusterNeighbours below) plus 16 squared-distance compares, plus two multiplies and
    /// three compares to derive the threshold, and allocates nothing. No LINQ, no reflection, no
    /// per-explosion logging.
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
        /// is counted, so the first one in a pile always sees zero and keeps full base behaviour; from
        /// one neighbour on - i.e. every explosion that is actually piling fill on top of another - the
        /// light tier applies.
        /// </summary>
        private const int ClusterLightNeighbours = 1;
        private const int ClusterHeavyNeighbours = 3;

        /// <summary>
        /// Per-tier caps. Each tier states its own caps rather than reusing a bare literal, because the
        /// light tier's values happen to equal the base caps (PointCap 2 / SubPointCap 1) and its two
        /// Math.Min calls are therefore identity transforms today - the light tier is scale-only, which
        /// is what the type remarks say. Writing that coincidence as `Math.Min(pointCap, 2)` made the
        /// code look like it tightened a cap when it did not, and a reader had to substitute the base
        /// constants to find out. Naming the tier's own caps keeps the "every tier can only lower the
        /// count" invariant in the structure (the Math.Min stays) while making the no-op explicit and
        /// independently adjustable: raising PointCap no longer silently changes what the light tier
        /// does, and giving the light tier a real count reduction is now a one-constant edit here
        /// instead of a change of meaning at the call site.
        ///
        /// Only the heavy tier lowers counts today: 1 / 0 turns the shipped 3 / 2 main dynamite into
        /// 1 + 1 + 0 = 2 orbs, and the shipped 3 / 0 Small/Fae into the same 2.
        /// </summary>
        private const int ClusterLightPointCap = 2;
        private const int ClusterLightSubPointCap = 1;
        private const int ClusterHeavyPointCap = 1;
        private const int ClusterHeavySubPointCap = 0;

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

        /// <summary>
        /// World radius of one orb's sphere per unit of <c>baseScale</c>, in metres.
        ///
        /// Asset fact, not derivable from the decompiled source: the orb root's localScale is set to
        /// <c>Vector3.one * (orb.size * baseScale)</c> (ExplosionEffect.cs:47), under it the
        /// <c>ExploSphere</c> child carries a local scale of 4.4401164, and its Icosphere mesh has an
        /// AABB extent of 1.372998 - an extent being a half-size, so this product is already a radius.
        /// 4.4401164 * 1.372998 = 6.0962, i.e. a 12.19m diameter at baseScale 1.0 (the main dynamite)
        /// and 6.10m at 0.5 (Small/Fae), which is where the diameters quoted in the type remarks come
        /// from. <c>orb.size</c> is left out on purpose: it is 1 for the centre orb and
        /// childSizeFactor (0.9) or its square for the rings (ExplosionEffect.cs:60,62,68,76), so this
        /// is the radius of the largest orb of the set, which is the right one for "is this fireball
        /// landing on top of existing fill".
        /// </summary>
        private const float OrbRadiusPerBaseScale = 6.0962f;

        /// <summary>How recently another explosion must have been recorded to count as clustered.</summary>
        /// <remarks>
        /// The window has to outlast the orbs of the preceding explosion rather than just its
        /// detonation instant, because it is those orbs that the new ones are drawing over. Spawning
        /// itself is quick - about 0.22s for the whole set once the speed divisor is accounted for
        /// (see the class remarks) - but each orb then lives 20s under its own RemoveAfterSeconds and
        /// plays out its animation, and the VFX root lives 30s. So the span over which two explosions
        /// genuinely share the screen is set by orb lifetime, not by spawn spread. 2.5s is a judgement
        /// call well inside that: long enough to catch a pile thrown together, short enough that two
        /// deliberate detonations a few seconds apart are each treated on their own.
        ///
        /// The window is also the entire reason no scene tracking is needed. Explosion positions are
        /// world coordinates, so in principle a coordinate from a torn-down level could be scored as a
        /// neighbour of a new one; in practice a buffered entry expires 2.5s after it was recorded, and
        /// every scene change in the game takes far longer than that. Every load is
        /// LoadSceneMode.Single - Pretitle.cs:22 passes it explicitly, and
        /// DebugMainMenu.cs:48,54,60,75,93, KickedState.cs:27, NetworkConnector.cs:161,200,230 and
        /// LoadingScreenHandler.cs:179 all use the SceneManager overload whose mode defaults to Single,
        /// while the networked path goes through PhotonNetwork.LoadLevel (LoadingScreenHandler.cs:139),
        /// which is Single as well - so the old scene is destroyed rather than kept alongside the new
        /// one. Every one of those loads that can be followed by an explosion runs behind
        /// LoadSceneProcess (LoadingScreenHandler.cs:122-132), which waits for the load itself, then
        /// for Character.localCharacter to spawn (:170-174, :200-204 via WaitForCharacterSpawn at
        /// :207-227) and then for extraYieldTimeOnEnd - 3s by default, and the call sites that pass 0f
        /// (GameOverHandler.cs:60, AirportCheckInKiosk.cs:163, Peak\Quicksave.cs:402) still wait on the
        /// spawn. Map-segment progression, the one transition with no loading screen, loads no scene at
        /// all: it is SetActive on subtrees of one Map object (MapHandler.JumpToSegmentLogic,
        /// MapHandler.cs:855-909; the "Jumping from X to Y" line is :887), so the coordinates stay in
        /// the same scene and the same frame of reference and there is nothing to invalidate. A
        /// Time.time that went backwards over a reload is handled by CountClusterNeighbours' own
        /// `stamp > now` test, which treats a future timestamp as expired.
        ///
        /// An earlier revision tracked gameObject.scene.handle and cleared the buffer when it changed,
        /// with a latch and an "ExplosionScale.scene-handle-thrash" diagnostic on top to survive a
        /// hypothetical third-party additive load. All of it is gone: the window already provided the
        /// guarantee, so the tracking could only ever clear a buffer whose entries were expired anyway,
        /// while the diagnostic could fire with no additive load at all (three consecutive Single loads
        /// whose third scene happens to reuse the first scene's handle) and would have told a tester to
        /// look for a problem that was not there.
        /// </remarks>
        private const float ClusterWindowSeconds = 2.5f;
        /// <summary>
        /// Fallback cluster radius, used only when an explosion's <c>baseScale</c> is pathological and
        /// <see cref="ResolveClusterRadiusSqr"/> cannot derive a radius from it. The normal path derives
        /// the threshold from the fireball's own size instead of this fixed number, which was the single
        /// threshold every explosion used before that derivation existed.
        ///
        /// What the derivation changed, in both directions, stated plainly:
        ///   the main dynamite's threshold went UP, 3.5m to about 6.1m, so a main charge is now pulled
        ///     into the cluster tier by neighbours it used to ignore - other main charges, and small
        ///     pops too. That is the intended direction, and it is not a guess: a neighbour inside 6.1m
        ///     has its *centre* inside this explosion's own fireball (radius about 6.1m, half the
        ///     12.19m diameter), and GetPoints always spawns a centre orb at that position
        ///     (ExplosionEffect.cs:58-60), so there is provably an orb sphere overlapping this one's.
        ///     Under the flat 3.5m the same charge kept all 5 orbs at full size next to a second charge
        ///     5m away whose fireball was almost entirely coincident with it, which is precisely the
        ///     stacked fill this patch exists to cut and the case it was missing. The cost is that a
        ///     main charge surrounded by three cooked items - each only a 6.1m-wide pop - now takes the
        ///     heavy tier, which is a bigger visual change than the overlap alone would justify; it
        ///     still takes four detonations inside 6.1m and 2.5s to get there, which is a moment where
        ///     frames are worth more than fireball size;
        ///   Small/Fae's threshold went DOWN, 3.5m to about 3.05m, so a small pop needs a closer
        ///     neighbour than before to reduce *itself*. The old flat value exceeded the small
        ///     fireball's own radius, i.e. it counted neighbours that were outside the sphere being
        ///     decided about.
        ///
        /// Still a known limitation: this is a centre-to-centre distance, i.e. a proxy for "do these
        /// fireballs overlap on screen" rather than a measurement of it, and it takes no account of the
        /// neighbour's own size (see ResolveClusterRadiusSqr for why the asymmetry is deliberate). It is
        /// also unrelated to the AOE range the main dynamite damages with (12.0 as serialized) - damage
        /// is on a different component entirely and nothing here touches it.
        /// </summary>
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
        /// That sentinel is the only thing that makes a slot uncountable - see <see cref="Reset"/> -
        /// so the timestamp array is the one that gets cleared and the position array is left as it is.
        /// </summary>
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

            // Proof of the "no NRE" argument: GetPoints is still the producer of the private orb
            // array that Start consumes, and this patch never prevents GetPoints from running.
            ScaleValidation.RequireInstanceFieldByTypeName(
                typeof(ExplosionEffect), "explosionPoints", ExplosionOrbArrayTypeName);
            ScaleValidation.RequireInstanceMethod(
                typeof(ExplosionEffect), "Start", typeof(void), Type.EmptyTypes, requirePublic: false);
            ScaleValidation.RequireInheritedInstanceProperty(
                typeof(ExplosionEffect), "transform", typeof(Transform));

            // The orb size multiplier, and the one field this patch both reads and writes for two
            // different purposes. Public and serialized like the two counts, read exactly once by the
            // game - at the orb root's localScale assignment in the IExplode local function
            // (ExplosionEffect.cs:47) - and by nothing else in the assembly, which is what makes
            // lowering it purely visual. The Prefix additionally reads it to derive this explosion's
            // cluster radius (see ResolveClusterRadiusSqr).
            FieldInfo baseScaleField = ScaleValidation.RequireInstanceField(
                typeof(ExplosionEffect), "baseScale", typeof(float));
            if (!baseScaleField.IsPublic)
                throw new InvalidOperationException(
                    "ExplosionEffect.baseScale is no longer a public instance field");
            // Deliberately NOT asserted: explosionRadius, childSizeFactor, spawnRadiusFactor, speed,
            // minDelay, maxDelay, minSpeed, maxSpeed, explosionOrb. An earlier revision asserted all
            // nine on the grounds that GetPoints / IExplode read them as geometry or timing inputs and
            // that this type's remarks reason about them. Both halves of that reasoning fail:
            //
            //   1. A structural assertion checks a member's *shape*, and none of the remarks' arguments
            //      depend on a shape. The ~0.22s spawn-window argument depends on speed being 3.0 and
            //      minDelay/maxDelay being 0.3/0.5 - serialized prefab values, which by this repo's
            //      most expensive lesson override the field initializers. RequireInstanceField(...,
            //      "speed", typeof(float)) cannot see a serialized value at all, so it does not
            //      protect the argument it was added to protect. The same holds for the 12.19m orb
            //      diameter (a prefab hierarchy fact, see OrbRadiusPerBaseScale) and for the 3/2 and
            //      3/0 counts.
            //   2. That revision leaned on a backstop that no longer exists. It claimed that because
            //      this patch is registered with requireCurrentMvid: true, PatchInstaller would skip
            //      the patch outright - before Initialize even runs - whenever the Assembly-CSharp MVID
            //      was not the audited one, so on the only build where Initialize ran, all nine fields
            //      were already guaranteed to have their audited shape. 1.4.0 removed that gate on
            //      purpose (see the comment in PatchInstaller.InstallOne): MVID mismatch no longer
            //      skips anything, and this patch now installs on any build whose shapes it happens to
            //      match. Nothing here checks those nine fields, and it should not pretend otherwise.
            //
            // The consequence, stated plainly rather than papered over: the correctness of this patch
            // rests on facts no assertion in this file can reach - the three prefab serialized values,
            // the orb hierarchy and its particle systems' scalingMode, and the assumption that
            // lowering the two counts and baseScale stays purely visual. It stays safe only as long as
            // somebody re-reads the freshly decompiled ExplosionEffect, the three prefabs and the AOE
            // sibling after a game update. That is a re-audit obligation, not a structural one, and it
            // is the reason this patch is classified Fingerprint.
            //
            // What is left asserted is exactly what the Prefix reads or writes, plus Character.Center
            // (see TryGetViewpoint for why that one is a stand-in assertion rather than a dead one).
            // Those are the assertions that actually carry weight: a changed shape on a member the
            // patch reads or writes changes what the patch does, whereas a changed shape on one of
            // those nine untouched fields could only make this patch refuse to install - which is why
            // asserting them would add fail-closed surface without protecting an argument.

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

        /// <summary>
        /// Patch teardown (called from ExplosionScaleModule's reset hook). Empties the cluster history
        /// so a re-install starts from a clean slate.
        ///
        /// Only the timestamps are cleared. The positions are deliberately left alone, and the sentinel
        /// argument that makes that safe holds at the single entry point that reads a position.
        /// <see cref="CountClusterNeighbours"/> reads <c>_clusterPositions[i]</c> only after both of its
        /// timestamp tests fail to skip the slot, i.e. only when
        /// <c>!(stamp &gt; now) &amp;&amp; !(now - stamp &gt; ClusterWindowSeconds)</c>. With
        /// <c>stamp == float.NegativeInfinity</c> and any finite or +inf <c>now</c>,
        /// <c>now - stamp</c> is +inf and the second test skips the slot, so a cleared slot is
        /// uncountable no matter what its position holds. Its one caller passes <c>Time.time</c>, which
        /// is the engine's monotonically rising play-time accumulator: it is never negative infinity,
        /// and a NaN there would have broken every WaitForSeconds and every deltaTime consumer in the
        /// game long before reaching this counter. The only writer of a finite timestamp is
        /// <see cref="Record"/>, which writes a slot's position and timestamp together in one call, so
        /// every countable slot has a matching position.
        ///
        /// An earlier revision also zeroed the positions. That was dead in both directions: for a
        /// never-recorded slot it wrote the value the array already held, and for a recorded one it
        /// overwrote a value the -inf timestamp had already made unreachable, so it has been removed.
        /// Even in the impossible NaN-time case the residue would be bounded and cosmetic - up to 16
        /// stale positions could be scored as neighbours, which can only lower an orb count, never
        /// raise one (every cap is a Math.Min and the result is clamped to [0, prefab value]) and
        /// never throws.
        /// </summary>
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
                // An anomalous negative prefab count would invert the clamp bounds (Mathf.Clamp with
                // min > max returns max). Leave the original untouched to preserve its behavior.
                RateLimitedDiagnostics.Hit("ExplosionScale.negative-prefab-count");
                return true;
            }

            int pointCap = PointCap;
            int subPointCap = SubPointCap;
            float scaleFactor = 1f;

            // Read once, up front, and used for two things: deriving this explosion's cluster radius
            // below, and the shrink at the end of this method. Nothing in between can change the field
            // - no game code runs inside this Prefix - so the single read is equivalent to the two the
            // previous revision made, and it guarantees the radius and the shrink reason about the same
            // value. It is never written before it has been used for both.
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
                // NaN coordinates make both comparisons false, which means "no distance tier".
                // The literals here need no named constants of their own: unlike the cluster tiers,
                // every one of them is strictly below the corresponding base cap (PointCap 2,
                // SubPointCap 1), so none of them can quietly become a no-op the way
                // ClusterLightPointCap could if it were written as a bare 2.
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
                float reduced = currentScale * scaleFactor;
                // Only ever lower it. A zero or negative authored value, or a NaN, fails this
                // comparison and is passed through to the original untouched. In particular a negative
                // value is never pushed towards zero: -1 * 0.85 = -0.85 is not less than -1.
                if (reduced < currentScale)
                    __instance.baseScale = reduced;
            }

            return true;
        }

        /// <summary>
        /// Squared cluster radius for one explosion, derived from its own <c>baseScale</c>.
        ///
        /// The threshold's physical meaning is "is this fireball landing on top of fill that is already
        /// on screen", so the natural scale for it is the fireball's own radius rather than a fixed
        /// number: <c>baseScale * OrbRadiusPerBaseScale</c>, i.e. about 6.1m for the main dynamite and
        /// 3.05m for Small/Fae. A centre distance below one radius means the two fireballs are
        /// substantially coincident, which is exactly the stacked fill this patch exists to cut.
        ///
        /// Deliberately asymmetric: this is the radius of the explosion being decided about, not of the
        /// buffered ones. The decision is how many orbs *this* detonation gets, and the question that
        /// answers it is whether this fireball's own volume is being drawn over an area that is already
        /// covered. Recording each entry's baseScale and testing against the sum of the two radii was
        /// considered and rejected: the sum is a "do the two surfaces touch at all" test (up to 12.2m
        /// for two main charges), which would pull in explosions that merely graze each other. The
        /// asymmetry also reads correctly from both sides - a main charge next to a few small pops uses
        /// 6.1m, because those pops really are inside its fireball, while a small pop next to a main
        /// charge has to be within 3.05m, i.e. well inside the main fireball, before it counts itself
        /// as piling on.
        ///
        /// Everything pathological falls back to the fixed <see cref="ClusterRadius"/>, and the tests
        /// are written negatively so that NaN takes the fallback instead of slipping through a positive
        /// test. Exhaustively, for <c>radius = baseScale * OrbRadiusPerBaseScale</c> and
        /// <c>radiusSqr = radius * radius</c>:
        ///   NaN baseScale       radius is NaN, <c>!(radius &gt; 0f)</c> holds        -> fallback;
        ///   zero or negative    radius is zero or negative, same test holds          -> fallback;
        ///   denormally small    radius is positive but radiusSqr underflows to zero,
        ///                       <c>!(radiusSqr &gt; 0f)</c> holds                     -> fallback;
        ///   +inf, or finite but
        ///   above about 3e18    radiusSqr overflows to +inf, IsInfinity holds        -> fallback;
        ///   anything else       radiusSqr is a positive finite number                -> used.
        /// The overflow test is not cosmetic: an infinite radiusSqr would make every unexpired slot pass
        /// the distance compare regardless of where it is, so any explosion following three others
        /// anywhere in the world within the window would take the heavy tier. (A genuinely lone
        /// explosion is still safe even then, because every slot's -inf timestamp fails the time test
        /// first.)
        /// </summary>
        private static float ResolveClusterRadiusSqr(float baseScale)
        {
            const float fallbackSqr = ClusterRadius * ClusterRadius;
            float radius = baseScale * OrbRadiusPerBaseScale;
            float radiusSqr = radius * radius;
            if (!(radius > 0f) || !(radiusSqr > 0f) || float.IsInfinity(radiusSqr))
                return fallbackSqr;
            return radiusSqr;
        }

        /// <summary>
        /// Number of recorded explosions that are both inside the time window and inside
        /// <paramref name="radiusSqr"/> of <paramref name="position"/>. The current explosion is not
        /// part of the buffer yet, so it cannot count itself. A timestamp in the future means Time.time
        /// went backwards (scene reload, timeline reset), and such an entry is treated as expired.
        /// </summary>
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

        /// <summary>
        /// Local viewpoint for the distance tier: the main camera when it exists, otherwise the local
        /// character's torso. The torso is read through CharacterRagdoll.partDict rather than
        /// Character.Center (Character.cs:266), which is the same value without the
        /// KeyNotFoundException that GetBodypart raises before the ragdoll registered its parts
        /// (Character.cs:1299-1302). No viewpoint means no distance tier at all.
        ///
        /// The camera - not Character.observedCharacter - is deliberately the origin, and this is the
        /// one place in this plugin where those two frames of reference are not interchangeable, so the
        /// difference is spelled out. The screen shake that shrinks along with the orb count measures
        /// its falloff from <c>Character.observedCharacter.Center</c>
        /// (GamefeelHandler.AddPerlinShakeProximity, GamefeelHandler.cs:63-67), and the sibling
        /// RemoteRagdollLod lane also measures from observedCharacter (see its PassesFallbacks and
        /// ShouldThrottleAnimation). This tier does not, because it is deciding a *rendering* cost: how
        /// much of the screen the surviving orbs will cover, and therefore how much fill can be dropped
        /// without the player noticing. Screen coverage is a function of the camera's position, not of
        /// which character the camera is following, so the camera is the origin that makes the tier mean
        /// what it says. Matching the shake's frame instead would make the tier wrong exactly where the
        /// two diverge:
        ///   - the god camera and any CameraOverride move the camera independently of every character
        ///     (MainCameraMovement.cs:63-67 and :72-75, :312-317), so measuring from observedCharacter
        ///     could keep full orb counts for an explosion that is a few pixels wide on screen;
        ///   - while spectating, the camera sits up to about 5m behind the spec target
        ///     (position = GetSpectatePosition() then a TransformDirection offset of
        ///     (0, 0.5, -spectateZoom) with spectateZoom clamped to spectateZoomMin/Max 1..5,
        ///     MainCameraMovement.cs:154,170-172), and if that target is dead GetSpectatePosition
        ///     returns LastLivingPosition rather than its torso (Character.cs:431-437), which can be
        ///     anywhere.
        /// In first person the two frames agree to within a metre anyway: the camera is clamped to
        /// characterPovMaxDistance (0.1) of a point lerped between the head offset and the torso
        /// position (MainCameraMovement.cs:302-309), so nothing about the common case turns on this
        /// choice - only the spectating and free-camera cases do, and they turn on it in the camera's
        /// favour. The consequence to be aware of is that shake strength and orb count are keyed to
        /// slightly different origins; since the shake reduction is a side effect of the count and never
        /// a target of this patch, that costs nothing beyond this paragraph.
        ///
        /// The fallback reads localCharacter rather than observedCharacter for the same reason it is
        /// only a fallback: it runs when MainCamera.instance is still null, i.e. before MainCamera.Awake
        /// (MainCamera.cs:15-19) has run. MainCameraMovement lives on that same GameObject
        /// (MainCameraMovement.cs:55 fetches MainCamera with GetComponent), so its LateUpdate has not
        /// run either and MainCameraMovement.specCharacter is still null - which makes
        /// Character.observedCharacter return localCharacter itself (Character.cs:203-214). The two are
        /// therefore equivalent in the only window this branch is reachable, and localCharacter is the
        /// cheaper of the two.
        ///
        /// Character.Center is asserted in Initialize but never invoked from here, which is
        /// deliberate and not a dead assertion: the assertion pins the shape of the API this method
        /// stands in for, so that a re-authored Center (different type, or no longer the torso
        /// position) fails the patch closed instead of leaving this hand-rolled equivalent silently
        /// wrong. The same pattern is used in RemoteRagdollLod.cs and
        /// Patches\Stability\BinocularSunGuardPatch.cs, where the reasoning is spelled out in the
        /// comment above its own Character.Center assertion.
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
