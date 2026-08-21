using System;
using System.Collections.Generic;
using BepInEx.Configuration;

namespace PeakSafeOptimizer.Config
{
    public enum PatchId
    {
        RunBasedValues,
        Parachute,
        // Stability lane: guards BugleSFX.Update() against an out-of-range currentClip received
        // from a client whose custom bugle-song mod has a longer bugle array. Vanilla
        // RPC_StartToot does not range-check the index, and the resulting IndexOutOfRangeException
        // fires before `t = true`, so the same frame block retries and throws every frame.
        BugleClipGuard,
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
        HeatEmission,
        Campfire,
        MapHandler,
        BreakableBridge,
        RopeRendering,
        ShakyRock,
        PlayerEyeLook,
        WeightRefresh,
        RavenPerch,
        EruptionSpawner,
        BeeSwarm,
        ZombieManager,
        SleepingZombie,
        Tornado,
        GloomVoiceConfuser,
        MovingLava,
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
        // ExplosionShaderWarmup is reserved and intentionally has no PatchDefinition. It used
        // Shader.WarmupAllShaders() from a Postfix on Dynamite.Start, which is a global, blocking
        // warm-up of every loaded shader and all of its variants - not a targeted warm-up of the
        // explosion orb material. On a URP build with a large mod list that froze the main thread
        // for long enough to look like a hang: spawning a single dynamite made the process stop
        // responding. Confirmed in-game, so the implementation was removed. A targeted warm-up
        // would need to touch only the orb material's shader variants, which cannot be expressed
        // through the public Shader API without rendering something.
        ExplosionShaderWarmup,
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
        // ScaleExperimental lane: runs Bodypart.Animate only every Nth rendered frame for
        // the detail bodyparts (fingers, jiggle bones, toes, mouth/jaw) of remote, fully controlled,
        // non-interacting player characters. Unlike the RemoteRagdollLod entries this has no distance
        // term, so it is the only lane that still reduces remote ragdoll cost when the whole lobby is
        // clustered together. Gravity, ToggleUseGravity, Drag and ApplyForces keep running on every
        // part on every physics step, so a throttled part swings passively for a step instead of
        // floating, drifting or accumulating force.
        DetailBodypartThrottle,
        CollisionCharacterLookup,
        AnimatorValuesHash,
        PlayerNameUiWrites
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

        public ConfigEntry<bool> BindExperimental(ConfigFile config, string name, string description)
        {
            if (string.Equals(name, "EnableAllExperimental", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("EnableAllExperimental is prohibited; bind each experiment independently.", nameof(name));
            return config.Bind("Experimental", name, false, description);
        }

        public static OptimizerConfig Bind(ConfigFile config)
        {
            var patches = new Dictionary<PatchId, ConfigEntry<bool>>
            {
                [PatchId.RunBasedValues] = BindPatch(config, PatchId.RunBasedValues, true),
                [PatchId.Parachute] = BindPatch(config, PatchId.Parachute, true),
                [PatchId.BugleClipGuard] = BindPatch(config, PatchId.BugleClipGuard, true),
                [PatchId.RopeSegmentGuard] = BindPatch(config, PatchId.RopeSegmentGuard, true),
                [PatchId.IKItemGuard] = BindPatch(config, PatchId.IKItemGuard, true),
                [PatchId.EmoteWheelGuard] = BindPatch(config, PatchId.EmoteWheelGuard, true),
                // Reserved compatibility keys: no PatchDefinition is registered in the
                // first-test baseline, even if a user manually changes these values.
                [PatchId.HeatEmission] = BindPatch(config, PatchId.HeatEmission, false),
                [PatchId.Campfire] = BindPatch(config, PatchId.Campfire, true),
                [PatchId.MapHandler] = BindPatch(config, PatchId.MapHandler, false),
                [PatchId.BreakableBridge] = BindPatch(config, PatchId.BreakableBridge, true),
                // In-game test (BepInEx 5.4.23.3 + 21 mods incl. MMHOOK_Assembly-CSharp /
                // MonoDetour / AutoHookGenPatcher): the 14-instruction owner LINQ pattern matched
                // 0 times at runtime, so Initialize failed closed. The implementation was removed;
                // this key is reserved and reports "no definition registered".
                [PatchId.RopeRendering] = BindPatch(config, PatchId.RopeRendering, false),
                [PatchId.ShakyRock] = BindExperimental(config, PatchId.ShakyRock),
                [PatchId.PlayerEyeLook] = BindExperimental(config, PatchId.PlayerEyeLook),
                [PatchId.WeightRefresh] = BindExperimental(config, PatchId.WeightRefresh),
                [PatchId.RavenPerch] = BindExperimental(config, PatchId.RavenPerch),
                // In-game test: the Transpiler produced invalid IL (HarmonyException: IL Compile
                // Error) because the matched window ended on the trailing brtrue, which the
                // replacement dropped, leaving the query bool unconsumed before a void ret. The
                // implementation was removed; this key is reserved and reports
                // "no definition registered".
                [PatchId.EruptionSpawner] = BindExperimental(config, PatchId.EruptionSpawner),
                [PatchId.BeeSwarm] = BindExperimental(config, PatchId.BeeSwarm),
                [PatchId.ZombieManager] = BindExperimental(config, PatchId.ZombieManager),
                [PatchId.SleepingZombie] = BindExperimental(config, PatchId.SleepingZombie),
                [PatchId.Tornado] = BindExperimental(config, PatchId.Tornado),
                [PatchId.GloomVoiceConfuser] = BindExperimental(config, PatchId.GloomVoiceConfuser),
                [PatchId.MovingLava] = BindExperimental(config, PatchId.MovingLava),
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
                // Reserved key with no PatchDefinition; see the enum comment. Kept false so that a
                // stale config file cannot re-enable the removed global warm-up.
                [PatchId.ExplosionShaderWarmup] =
                    BindExperimental(config, PatchId.ExplosionShaderWarmup, false),
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
                [PatchId.PlayerNameUiWrites] = BindPatch(config, PatchId.PlayerNameUiWrites, true)
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
                "remote characters runs once every N rendered frames; 1 = no throttling. Values " +
                "above 10 are clamped to 10 at runtime.");
            ConfigEntry<bool> detailed = config.Bind("Diagnostics", "DetailedLogging", true,
                "Emit per-reason aggregate repair diagnostics no more frequently than every 30 seconds.");
            return new OptimizerConfig(patches, detailed);
        }

        private static ConfigEntry<bool> BindPatch(ConfigFile config, PatchId id, bool defaultValue) =>
            config.Bind("Fixes", id.ToString(), defaultValue, $"Enable the independent {id} optimization/fix lane.");

        private static ConfigEntry<bool> BindExperimental(ConfigFile config, PatchId id, bool defaultValue = false) =>
            config.Bind("Experimental", id.ToString(), defaultValue, $"Enable the independent experimental {id} lane.");
    }
}
