using System.Collections.Generic;
using BepInEx.Configuration;

namespace PeakSafeOptimizer.Config
{
    /// <summary>
    /// One entry per independently gated patch. Every member has both a config binding in
    /// <see cref="OptimizerConfig.Bind"/> and a registered PatchDefinition, so the generated config
    /// file never advertises a switch that does nothing.
    /// </summary>
    public enum PatchId
    {
        RunBasedValues,
        Parachute,
        // Stability lane: guards CharacterRopeHandling.Climbing() when the held rope reports an
        // empty/unavailable segment list. RopeClimbingAPI.GetSegmentFromPercent forces its low index
        // to 1 and then clamps with math.clamp(index, 0, Count - 1); with Count == 0 that yields -1
        // and ropeSegments[-1] throws every physics frame. Confirmed in-game: 184 occurrences in a
        // single multiplayer session.
        RopeSegmentGuard,
        // Stability lane: skips a frame of hand-IK when the held item prefab lacks "Hand_R"/"Hand_L"
        // nodes, which otherwise throws NullReferenceException in ConfigureIK every physics frame
        // during the equip transition (135 occurrences confirmed in-game).
        IKItemGuard,
        // Stability lane: skips GUIManager.UpdateEmoteWheel when Character.localCharacter is null or
        // destroyed (spectating/respawn/quit), removing a per-frame NullReferenceException flood
        // (2315 occurrences confirmed in-game).
        EmoteWheelGuard,
        Campfire,
        BreakableBridge,
        // Scale lane (Lane E1): scan/redundant-write reduction for multiplayer player counts
        // and ground item counts. Each entry is registered and gated independently.
        HeatEmissionScan,
        RagdollPhysicsMats,
        ItemCollisionMode,
        GenericOptimizerRange,
        // Scale lane: merges Campfire.ApplyCampfireProtection's two same-frame full player scans
        // into one. Strictly equivalent; the single scan reproduces _charactersInRadius plus
        // everyoneInRange and reuses them for the OrbFogHandler write and the buff branch.
        CampfireProtectionMerge,
        // Scale lane, host-side zombie scans driven every frame by ZombieManager.Update.
        // Squared-distance equivalents; ZombieScanRange also early-breaks once both flags are set.
        ZombieScanRange,
        ZombieSpawnScanRange,
        // ScaleExperimental lane: more aggressive multiplayer / ground-item work.
        // Implemented entries default to true during private testing and remain independently gated.
        //
        // WeightRefreshCoalescing is registered again after re-auditing idempotency:
        // CharacterAfflictions.UpdateWeight (CharacterAfflictions.cs:410-466) only reads state, its
        // SFX / GUI / particle calls are guarded by "new value greater than current"
        // (:468 and :478), and :487-489 are absolute SetStatus assignments. A duplicate call in the
        // same frame therefore produces neither a state change nor an extra effect.
        WeightRefreshCoalescing,
        // ScaleExperimental lane: equivalent replacement of MushroomZombie.DoSleeping() with squared
        // distance and Vector3.Angle deferred behind the distance test.
        SleepingZombieScan,
        ItemScaleRedundantWrite,
        RemoteItemInterpolationThreshold,
        EyeLookComponentCache,
        // ScaleExperimental lane: explosion orb count reduction. Vanilla is 4 / 2, i.e.
        // 1 + 4 + 2 * 4 = 13 orbs per explosion; the shipped caps are 3 / 1, i.e.
        // 1 + 3 + 1 * 3 = 7 orbs. On top of the static caps the patch applies a cluster tier
        // (several explosions close together in time and space) and a distance tier (explosions far
        // from the local viewpoint), both of which can only lower the caps further.
        ExplosionScale,
        // Scale lane, class-A strict-equivalence optimizations. All default-enabled. Each is an
        // independent redundant-work elimination with an exact structure check in Initialize.
        ItemDatabaseNameLookup,
        IsLookedAtScan,
        ItemAudioManagerHash,
        BarAfflictionLayout,
        AnimatedMouthMaterial,
        // ScaleExperimental lane, distance LOD for remote ragdolls. All four implemented entries
        // default to true during private testing and remain independently gated; they share the helper
        // Patches\ScaleExperimental\RemoteRagdollLod. Only animation-driven work is skipped for a
        // distant, fully controlled, non-interacting remote character: gravity, drag,
        // ToggleUseGravity and ApplyForces keep running, so a downgraded character stays a passive
        // ragdoll instead of floating, drifting or accumulating force.
        RemoteRagdollLodPhysics,
        RemoteRagdollLodAnimate,
        RemoteRagdollLodMovementForce,
        RemoteRagdollLodAnimator,
        // ScaleExperimental lane: downgrades every ragdoll ConfigurableJoint created by
        // RigCreator.SpawnJoint from JointProjectionMode.PositionAndRotation to PositionOnly. One
        // write per joint at creation time, no per-frame state.
        JointProjection,
        // ScaleExperimental lane: runs Bodypart.Animate only every Nth physics tick for
        // the detail bodyparts (fingers, jiggle bones, toes, mouth/jaw) of remote, fully controlled,
        // non-interacting player characters. Unlike the RemoteRagdollLod entries this has no distance
        // term, so it is the only lane that still reduces remote ragdoll cost when the whole lobby is
        // clustered together. Gravity, ToggleUseGravity, Drag and ApplyForces keep running on every
        // part on every physics step, so a throttled part swings passively for a step instead of
        // floating, drifting or accumulating force.
        DetailBodypartThrottle,
        CollisionCharacterLookup,
        AnimatorValuesHash,
        PlayerNameUiWrites,
        // Stability-style guard in the Scale lane: protects CharacterItems.UpdatePocketBehaviors()
        // during the join window, where Character.player (a PlayerHandler lookup, Character.cs:201)
        // is not linked yet and the original dereferences character.player.itemSlots every frame.
        // Confirmed in-game: 14 NullReferenceException around the player registration log lines.
        PocketBehaviorGuard,
        // Scale lane: skips Bodypart.Drag when the effective drag is exactly 1f, which makes both
        // velocity multiplications IEEE identities. Concentrated on ragdolled characters, because
        // currentRagdollControll == 0 gives Mathf.Lerp(1f, drag, 0f) == 1f.
        BodypartDragIdentity,
        // Scale lane: caches LightVolume.SamplePositionAlpha per truncated lightmap voxel, so the
        // per-character Texture3D.GetPixel behind VoiceObscuranceFilter.Update (plus the ambience and
        // wind callers) collapses to one read per distinct voxel per lightmap generation.
        LightVolumeSampleCache,
        // ScaleExperimental lane: reuses a tumbleweed's chase target for a few physics ticks instead
        // of rescanning every character every tick. Desert/Mesa specific because tumbleweeds are.
        TumbleWeedTargetScan,
        // ScaleExperimental lane: evaluates the animation PlayableGraph of a remote, fully
        // controlled, non-interacting player character once every N physics ticks instead of every
        // tick, using the accumulated delta time so the animation still advances at real speed.
        // Unlike the RemoteRagdollLod entries this has no distance term, so it is the main lever for
        // a clustered lobby, which is where CharacterRagdoll.FixedUpdate dominates the managed
        // physics step.
        RemoteClusterAnimationThrottle,
        // Scale lane: makes Snowball.OnCollisionStay store a private copy of the collision instead of
        // the engine's Collision instance. Prerequisite for CollisionCallbackReuse, because with
        // reuse enabled that instance is a single shared object whose data later contact pairs
        // overwrite, and Snowball is the only place in the game that keeps one across frames.
        SnowballContactSnapshot,
        // Scale lane: equivalent replacement of Snowball.ProcessLastCollision that consumes the
        // snapshot captured by SnowballContactSnapshot instead of re-reading the stored Collision.
        SnowballContactConsume,
        // Scale lane: equivalent replacement of CharacterMovement.OnCollision that reads contact 0
        // once through Collision.GetContact(0) instead of four times through the allocating
        // Collision.contacts array.
        CollisionContactsNoAlloc,
        // Scale lane: skips Bodypart.AddMovementForce when the force it would accumulate is exactly
        // the zero vector, which is the case for every part of every character that is not pressing a
        // movement key - i.e. everyone standing in a cluster.
        BodypartMovementForceIdentity
    }

    public sealed class OptimizerConfig
    {
        private readonly Dictionary<PatchId, ConfigEntry<bool>> _patches;

        private OptimizerConfig(Dictionary<PatchId, ConfigEntry<bool>> patches, ConfigEntry<bool> detailedDiagnostics)
        {
            _patches = patches;
            DetailedDiagnostics = detailedDiagnostics;
        }

        public ConfigEntry<bool> DetailedDiagnostics { get; }

        // Cross-cutting value configuration consumed by the ExplosionScale patch. Stored statically
        // because PatchDefinition.Initialize has no ConfigFile/instance parameter.
        internal static ConfigEntry<int> ExplosionPointCount;
        internal static ConfigEntry<int> ExplosionSubPointCount;

        // Cluster tier: an explosion counts as clustered when other explosions happened within
        // ExplosionClusterWindowSeconds and ExplosionClusterRadius of it. Read at runtime by
        // Patches\ScaleExperimental\ExplosionScalePatch, which falls back to its own defaults for a
        // non-positive or NaN value.
        internal static ConfigEntry<float> ExplosionClusterWindowSeconds;
        internal static ConfigEntry<float> ExplosionClusterRadius;

        // Distance tier for the same patch: below Near nothing extra is dropped, between Near and
        // Far the orb count is capped at 2, beyond Far only the always-present centre orb remains.
        internal static ConfigEntry<float> ExplosionNearDistance;
        internal static ConfigEntry<float> ExplosionFarDistance;

        // Distance hysteresis for the RemoteRagdollLod entries. Read at runtime by
        // Patches\ScaleExperimental\RemoteRagdollLod, which forces Exit below Enter if the pair is
        // misconfigured, so a bad combination cannot make the tier oscillate every frame.
        internal static ConfigEntry<float> RemoteRagdollLodEnterDistance;
        internal static ConfigEntry<float> RemoteRagdollLodExitDistance;

        // Throttle interval for the DetailBodypartThrottle entry. Read at runtime by
        // Patches\ScaleExperimental\DetailBodypartThrottlePatch, which treats 1 or less as "no
        // throttling" and clamps anything above 10, so no value can freeze a detail part.
        internal static ConfigEntry<int> DetailBodypartThrottleInterval;

        // Rescan interval for the TumbleWeedTargetScan entry. Read at runtime by
        // Patches\ScaleExperimental\TumbleWeedTargetScanPatch, which treats 1 or less as "rescan
        // every tick" and clamps anything above 10.
        internal static ConfigEntry<int> TumbleWeedTargetScanInterval;

        // Evaluation interval for the RemoteClusterAnimationThrottle entry. Read at runtime by
        // Patches\ScaleExperimental\RemoteClusterAnimationThrottlePatch, which treats 1 or less as
        // "evaluate every tick" and clamps anything above 4.
        internal static ConfigEntry<int> RemoteClusterAnimationInterval;

        // Graphics budget (non-Harmony lane, see Graphics\GraphicsBudget). Every write is a
        // one-directional min(): a value that is already cheaper than the cap is left alone, and the
        // launch snapshot is restored on shutdown or when the lane is disabled.
        //
        // Only knobs the game does not expose belong here. Shadow distance and cascade count were
        // capped here and have been removed: the game has a Shadow Distance setting that writes both,
        // so capping them overrode a choice the player had already made.
        internal static ConfigEntry<bool> GraphicsBudgetEnabled;
        internal static ConfigEntry<bool> GraphicsDisableMsaa;
        internal static ConfigEntry<int> GraphicsSkinWeightCap;

        /// <summary>
        /// True when the collision-callback reuse flag may be flipped. Plugin only applies it after
        /// both Snowball guards are confirmed installed, because the game caches one Collision
        /// reference across frames there.
        /// </summary>
        internal static ConfigEntry<bool> CollisionCallbackReuse;

        public bool IsEnabled(PatchId id) => _patches.TryGetValue(id, out ConfigEntry<bool> entry) && entry.Value;

        /// <summary>
        /// Reverse lookup used by hot reload. Given the ConfigEntryBase raised by
        /// ConfigFile.SettingChanged, returns true and the PatchId if it is one of this plugin's
        /// boolean patch toggles. Value-typed entries (DetailedLogging, ExplosionPointCount,
        /// ExplosionSubPointCount, ...) are not patch toggles and return false, because they are
        /// read at runtime and already take effect immediately.
        /// </summary>
        public bool TryGetPatchId(ConfigEntryBase changedSetting, out PatchId id)
        {
            foreach (KeyValuePair<PatchId, ConfigEntry<bool>> pair in _patches)
            {
                if (ReferenceEquals(pair.Value, changedSetting))
                {
                    id = pair.Key;
                    return true;
                }
            }
            id = default;
            return false;
        }

        public static OptimizerConfig Bind(ConfigFile config)
        {
            var patches = new Dictionary<PatchId, ConfigEntry<bool>>
            {
                [PatchId.RunBasedValues] = BindPatch(config, PatchId.RunBasedValues, true),
                [PatchId.Parachute] = BindPatch(config, PatchId.Parachute, true),
                [PatchId.RopeSegmentGuard] = BindPatch(config, PatchId.RopeSegmentGuard, true),
                [PatchId.IKItemGuard] = BindPatch(config, PatchId.IKItemGuard, true),
                [PatchId.EmoteWheelGuard] = BindPatch(config, PatchId.EmoteWheelGuard, true),
                [PatchId.Campfire] = BindPatch(config, PatchId.Campfire, true),
                [PatchId.BreakableBridge] = BindPatch(config, PatchId.BreakableBridge, true),
                [PatchId.HeatEmissionScan] = BindPatch(config, PatchId.HeatEmissionScan, true),
                [PatchId.RagdollPhysicsMats] = BindPatch(config, PatchId.RagdollPhysicsMats, true),
                [PatchId.ItemCollisionMode] = BindPatch(config, PatchId.ItemCollisionMode, true),
                // Squared-distance equivalent for GenericOptimizer.AnyCharacterWithinRange. It is a
                // strict equivalence, but with the audited default rangeToDisableScripts =
                // float.PositiveInfinity it only adds a thin indirection on default scene data.
                // Still enabled by default per user request.
                [PatchId.GenericOptimizerRange] = BindPatch(config, PatchId.GenericOptimizerRange, true),
                [PatchId.CampfireProtectionMerge] = BindPatch(config, PatchId.CampfireProtectionMerge, true),
                // Host-only zombie scan optimizations (squared-distance equivalents).
                [PatchId.ZombieScanRange] = BindExperimental(config, PatchId.ZombieScanRange, true),
                [PatchId.ZombieSpawnScanRange] = BindExperimental(config, PatchId.ZombieSpawnScanRange, true),
                [PatchId.WeightRefreshCoalescing] = BindExperimental(config, PatchId.WeightRefreshCoalescing, true),
                [PatchId.SleepingZombieScan] = BindExperimental(config, PatchId.SleepingZombieScan, true),
                [PatchId.ItemScaleRedundantWrite] = BindExperimental(config, PatchId.ItemScaleRedundantWrite, true),
                [PatchId.RemoteItemInterpolationThreshold] =
                    BindExperimental(config, PatchId.RemoteItemInterpolationThreshold, true),
                [PatchId.EyeLookComponentCache] = BindExperimental(config, PatchId.EyeLookComponentCache, true),
                [PatchId.ExplosionScale] = BindExperimental(config, PatchId.ExplosionScale, true),
                [PatchId.ItemDatabaseNameLookup] = BindPatch(config, PatchId.ItemDatabaseNameLookup, true),
                [PatchId.IsLookedAtScan] = BindPatch(config, PatchId.IsLookedAtScan, true),
                [PatchId.ItemAudioManagerHash] = BindPatch(config, PatchId.ItemAudioManagerHash, true),
                [PatchId.BarAfflictionLayout] = BindPatch(config, PatchId.BarAfflictionLayout, true),
                [PatchId.AnimatedMouthMaterial] = BindPatch(config, PatchId.AnimatedMouthMaterial, true),
                [PatchId.RemoteRagdollLodPhysics] =
                    BindExperimental(config, PatchId.RemoteRagdollLodPhysics, true),
                [PatchId.RemoteRagdollLodAnimate] =
                    BindExperimental(config, PatchId.RemoteRagdollLodAnimate, true),
                [PatchId.RemoteRagdollLodMovementForce] =
                    BindExperimental(config, PatchId.RemoteRagdollLodMovementForce, true),
                [PatchId.RemoteRagdollLodAnimator] =
                    BindExperimental(config, PatchId.RemoteRagdollLodAnimator, true),
                [PatchId.JointProjection] = BindExperimental(config, PatchId.JointProjection, true),
                [PatchId.DetailBodypartThrottle] =
                    BindExperimental(config, PatchId.DetailBodypartThrottle, true),
                [PatchId.CollisionCharacterLookup] = BindPatch(config, PatchId.CollisionCharacterLookup, true),
                [PatchId.AnimatorValuesHash] = BindPatch(config, PatchId.AnimatorValuesHash, true),
                [PatchId.PlayerNameUiWrites] = BindPatch(config, PatchId.PlayerNameUiWrites, true),
                [PatchId.PocketBehaviorGuard] = BindPatch(config, PatchId.PocketBehaviorGuard, true),
                [PatchId.BodypartDragIdentity] = BindPatch(config, PatchId.BodypartDragIdentity, true),
                [PatchId.LightVolumeSampleCache] =
                    BindPatch(config, PatchId.LightVolumeSampleCache, true),
                [PatchId.TumbleWeedTargetScan] =
                    BindExperimental(config, PatchId.TumbleWeedTargetScan, true),
                [PatchId.RemoteClusterAnimationThrottle] =
                    BindExperimental(config, PatchId.RemoteClusterAnimationThrottle, true),
                [PatchId.SnowballContactSnapshot] =
                    BindPatch(config, PatchId.SnowballContactSnapshot, true),
                [PatchId.SnowballContactConsume] =
                    BindPatch(config, PatchId.SnowballContactConsume, true),
                [PatchId.CollisionContactsNoAlloc] =
                    BindPatch(config, PatchId.CollisionContactsNoAlloc, true),
                [PatchId.BodypartMovementForceIdentity] =
                    BindPatch(config, PatchId.BodypartMovementForceIdentity, true)
            };
            ExplosionPointCount = config.Bind("Experimental", "ExplosionPointCount", 3,
                "Upper bound for explosionPointCount when ExplosionScale is enabled. Vanilla is 4, " +
                "which together with the vanilla subExplosionPointCount of 2 spawns " +
                "1 + 4 + 2 * 4 = 13 orbs per explosion; the default 3 with 1 spawns 7.");
            ExplosionSubPointCount = config.Bind("Experimental", "ExplosionSubPointCount", 1,
                "Upper bound for subExplosionPointCount when ExplosionScale is enabled (vanilla 2).");
            ExplosionClusterWindowSeconds = config.Bind("Experimental", "ExplosionClusterWindowSeconds", 1.2f,
                "Time window in seconds used to detect an explosion cluster. Non-positive or NaN restores 1.2.");
            ExplosionClusterRadius = config.Bind("Experimental", "ExplosionClusterRadius", 3.5f,
                "Radius in world units used to detect an explosion cluster. Non-positive or NaN restores 3.5.");
            ExplosionNearDistance = config.Bind("Experimental", "ExplosionNearDistance", 50f,
                "Explosions closer than this to the local viewpoint keep the configured orb count.");
            ExplosionFarDistance = config.Bind("Experimental", "ExplosionFarDistance", 120f,
                "Explosions at or beyond this distance keep only the single centre orb. Must be " +
                "greater than ExplosionNearDistance; otherwise it is forced to Near * 2 at runtime.");
            RemoteRagdollLodEnterDistance = config.Bind("Experimental", "RemoteRagdollLodEnterDistance", 60f,
                "Distance in world units at which a remote character enters the passive-ragdoll tier.");
            RemoteRagdollLodExitDistance = config.Bind("Experimental", "RemoteRagdollLodExitDistance", 50f,
                "Distance in world units below which a remote character returns to full behaviour. " +
                "Must be lower than RemoteRagdollLodEnterDistance; otherwise it is clamped at runtime.");
            DetailBodypartThrottleInterval = config.Bind("Experimental", "DetailBodypartThrottleInterval", 3,
                "Animate for detail bodyparts (fingers / jiggle bones / toes / mouth and jaw) of " +
                "remote characters runs once every N physics ticks; 1 = no throttling. Values " +
                "above 10 are clamped to 10 at runtime.");
            TumbleWeedTargetScanInterval = config.Bind("Experimental", "TumbleWeedTargetScanInterval", 5,
                "A tumbleweed re-solves its chase target once every N physics ticks and reuses the " +
                "previous direction in between; 1 = rescan every tick. Values above 10 are clamped " +
                "to 10 at runtime.");
            RemoteClusterAnimationInterval = config.Bind("Experimental", "RemoteClusterAnimationInterval", 2,
                "A remote, fully controlled, non-interacting player character evaluates its animation " +
                "graph once every N physics ticks; the accumulated delta time is passed to the " +
                "evaluation so animation speed is unchanged. 1 = every tick. Values above 4 are " +
                "clamped to 4 at runtime.");
            CollisionCallbackReuse = config.Bind("Experimental", "CollisionCallbackReuse", true,
                "Set Physics.reuseCollisionCallbacks so the engine stops allocating a Collision " +
                "object plus a ContactPoint[] for every reported collision. Only applied when " +
                "both Snowball guards installed successfully, because Snowball is the only place in " +
                "the game that keeps a Collision reference across frames.");
            GraphicsBudgetEnabled = config.Bind("Graphics", "GraphicsBudgetEnabled", true,
                "Master switch for the one-directional graphics caps below. Each cap is only ever " +
                "written when the current value is more expensive, and the values captured at " +
                "startup are restored when this is turned off or the game shuts down.");
            GraphicsDisableMsaa = config.Bind("Graphics", "GraphicsDisableMsaa", true,
                "Force URP msaaSampleCount to 1. In this build MSAA and STP upscaling are mutually " +
                "exclusive (UniversalCameraData.IsTemporalAAEnabled requires msaaSamples == 1), and " +
                "the shipped default is render scale 0.8 with STP requested, so MSAA currently costs " +
                "full price while silently downgrading the upscaler to bilinear.");
            GraphicsSkinWeightCap = config.Bind("Graphics", "GraphicsSkinWeightCap", 2,
                "Upper bound for QualitySettings.skinWeights (1, 2, 4 or 255). Bones per vertex is " +
                "the per-vertex cost of every skinned character, so this scales with how many " +
                "players are on screen. Any other value disables this cap.");
            ConfigEntry<bool> detailed = config.Bind("Diagnostics", "DetailedLogging", true,
                "Emit per-reason aggregate repair diagnostics no more frequently than every 30 seconds.");
            return new OptimizerConfig(patches, detailed);
        }

        private static ConfigEntry<bool> BindPatch(ConfigFile config, PatchId id, bool defaultValue) =>
            config.Bind("Fixes", id.ToString(), defaultValue, $"Enable the independent {id} optimization/fix lane.");

        private static ConfigEntry<bool> BindExperimental(ConfigFile config, PatchId id, bool defaultValue) =>
            config.Bind("Experimental", id.ToString(), defaultValue, $"Enable the independent experimental {id} lane.");
    }
}
