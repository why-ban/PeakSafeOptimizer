using System.Collections.Generic;
using BepInEx.Configuration;

namespace PeakSafeOptimizer.Config
{
    /// <summary>
    /// One entry per independently gated patch. Every member has both a config binding in
    /// <see cref="OptimizerConfig.Bind"/> and a registered PatchDefinition, so the generated config
    /// file never advertises a switch that does nothing.
    ///
    /// Default policy for a shipped build: a patch is on by default only when it is behaviourally
    /// equivalent to vanilla, or when it repairs a vanilla fault. Anything that trades appearance,
    /// feel or animation fidelity for frames is off by default, however real its win is - that trade
    /// belongs to whoever installed the mod, not to the mod.
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
        BodypartMovementForceIdentity,
        // Stability lane: guards Action_ShowBinocularOverlay.TestLookAtSun(), reached every frame
        // from Update while anyone holds raised binoculars. Vanilla only null-checks
        // Character.localCharacter and then dereferences Singleton<MapHandler>.Instance and
        // DayNightManager.instance.sun unguarded (Action_ShowBinocularOverlay.cs:40-42). Confirmed
        // in-game: 1544 NullReferenceException in one session, i.e. one per frame while raised.
        BinocularSunGuard,
        // Stability lane: guards Peak.ItemOptimizationManager.DeregisterItem(Item) against a null
        // item. ItemOptimizer.OnEnable writes `enabled = false` when its item is null
        // (ItemOptimizer.cs:9-19), which synchronously invokes OnDisable, which calls
        // DeregisterItem(null), and Dictionary.Remove(null) throws. Confirmed in-game: 67 times.
        ItemOptimizerDeregisterGuard,
        // Scale lane: equivalent replacement of StatusEmitter.Update(), which every spore-gas volume
        // in the level runs every frame with no distance gate of its own. Squares the range test,
        // resolves the local character's Center once instead of two to four times, and only touches
        // the string-keyed overlap dictionary while actually in range.
        StatusEmitterScan,
        // Scale lane: skips the per-frame emission write of ParticleCountCuller when the attenuation
        // it computed is unchanged, and guards the missing Character.observedCharacter null check that
        // makes vanilla throw every frame before a character exists.
        ParticleCountCullerWrite
    }

    public sealed class OptimizerConfig
    {
        private readonly Dictionary<PatchId, ConfigEntry<bool>> _patches;
        /// <summary>
        /// Reverse index of <see cref="_patches"/>. A group switch maps to several PatchIds, so hot
        /// reload has to toggle all of them when one entry changes.
        /// </summary>
        private readonly Dictionary<ConfigEntry<bool>, List<PatchId>> _byEntry;

        private OptimizerConfig(Dictionary<PatchId, ConfigEntry<bool>> patches, ConfigEntry<bool> detailedDiagnostics)
        {
            _patches = patches;
            DetailedDiagnostics = detailedDiagnostics;
            _byEntry = new Dictionary<ConfigEntry<bool>, List<PatchId>>();
            foreach (KeyValuePair<PatchId, ConfigEntry<bool>> pair in patches)
            {
                if (!_byEntry.TryGetValue(pair.Value, out List<PatchId> ids))
                {
                    ids = new List<PatchId>(1);
                    _byEntry.Add(pair.Value, ids);
                }
                ids.Add(pair.Key);
            }
        }

        public ConfigEntry<bool> DetailedDiagnostics { get; }

        // Non-Harmony lanes. Both are switches: every tuning value they used to expose is now a named
        // constant next to the code that reasons about it, because a number in a config file is a
        // number nobody can validate. Stored statically because PatchDefinition.Initialize has no
        // ConfigFile/instance parameter.
        internal static ConfigEntry<bool> GraphicsCapBoneWeights;

        /// <summary>
        /// True when the collision-callback reuse flag may be flipped. Plugin only applies it after
        /// both Snowball guards are confirmed installed, because the game caches one Collision
        /// reference across frames there.
        /// </summary>
        internal static ConfigEntry<bool> CollisionCallbackReuse;

        public bool IsEnabled(PatchId id) => _patches.TryGetValue(id, out ConfigEntry<bool> entry) && entry.Value;

        /// <summary>
        /// Reverse lookup used by hot reload. Given the ConfigEntryBase raised by
        /// ConfigFile.SettingChanged, returns true and every PatchId it controls if it is one of this
        /// plugin's boolean patch toggles - several, when the switch is a group of patches that only
        /// make sense together. Value-typed entries (DetailedLogging, ExplosionPointCount,
        /// ExplosionSubPointCount, ...) are not patch toggles and return false, because they are
        /// read at runtime and already take effect immediately.
        /// </summary>
        public bool TryGetPatchIds(ConfigEntryBase changedSetting, out IReadOnlyList<PatchId> ids)
        {
            foreach (KeyValuePair<ConfigEntry<bool>, List<PatchId>> pair in _byEntry)
            {
                if (ReferenceEquals(pair.Key, changedSetting))
                {
                    ids = pair.Value;
                    return true;
                }
            }
            ids = null;
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
                [PatchId.DetailBodypartThrottle] =
                    BindExperimental(config, PatchId.DetailBodypartThrottle, false),
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
                    BindExperimental(config, PatchId.RemoteClusterAnimationThrottle, false),
                [PatchId.CollisionContactsNoAlloc] =
                    BindPatch(config, PatchId.CollisionContactsNoAlloc, true),
                [PatchId.BodypartMovementForceIdentity] =
                    BindPatch(config, PatchId.BodypartMovementForceIdentity, true),
                [PatchId.BinocularSunGuard] =
                    BindPatch(config, PatchId.BinocularSunGuard, true),
                [PatchId.ItemOptimizerDeregisterGuard] =
                    BindPatch(config, PatchId.ItemOptimizerDeregisterGuard, true),
                [PatchId.StatusEmitterScan] =
                    BindPatch(config, PatchId.StatusEmitterScan, true),
                [PatchId.ParticleCountCullerWrite] =
                    BindPatch(config, PatchId.ParticleCountCullerWrite, true)
            };

            // Groups: sets of patches that are only correct together, so they get one switch each
            // rather than one per patch. A per-patch switch would invite a half-enabled combination
            // that looks supported and is not.
            //
            // RemoteRagdollLod: four patches sharing one distance-tier decision
            // (Patches\ScaleExperimental\RemoteRagdollLod). Enabling only some of them means a remote
            // character has its animation graph skipped but keeps being driven towards animation
            // targets, or the reverse - neither is a state the tier was reasoned about in.
            BindGroup(config, patches, "Experimental", "RemoteRagdollLod", false,
                "Distance tier for remote characters: beyond RemoteRagdollLodEnterDistance a remote, " +
                "fully controlled, non-interacting player stops evaluating its animation graph, joint " +
                "animation, movement force and Animator parameters, and becomes a passive ragdoll " +
                "carried by physics and the network. Gravity, drag and force flushing keep running. " +
                "Off by default: a distant character visibly freezes its pose. One switch for all four " +
                "patches, which are only coherent together.",
                PatchId.RemoteRagdollLodPhysics,
                PatchId.RemoteRagdollLodAnimate,
                PatchId.RemoteRagdollLodMovementForce,
                PatchId.RemoteRagdollLodAnimator);

            // Snowball: the snapshot is written by one patch and read by the other. Installing only
            // the writer wastes work; installing only the reader leaves it with nothing to read.
            BindGroup(config, patches, "Fixes", "SnowballContactGuard", true,
                "Makes Snowball keep a value copy of its collision instead of the engine's Collision " +
                "instance (Snowball.cs:105 is the only cross-frame Collision reference in the game). " +
                "Equivalent on its own, and the prerequisite for Experimental.CollisionCallbackReuse. " +
                "One switch for the writing and the reading half, which are useless apart.",
                PatchId.SnowballContactSnapshot,
                PatchId.SnowballContactConsume);

            CollisionCallbackReuse = config.Bind("Experimental", "CollisionCallbackReuse", false,
                "Set Physics.reuseCollisionCallbacks so the engine stops allocating a Collision " +
                "object plus a ContactPoint[] for every reported collision. Only applied when " +
                "both Snowball guards installed successfully, because Snowball is the only place in " +
                "the game that keeps a Collision reference across frames. Off by default because it " +
                "is a process-wide engine flag: the game side is audited and guarded, but any other " +
                "mod that keeps a Collision past its callback would start reading a reused instance.");
            GraphicsCapBoneWeights = config.Bind("Graphics", "CapBoneWeights", false,
                "Limit QualitySettings.skinWeights to two bones per vertex. Bones per vertex is a " +
                "per-vertex cost of every skinned character, so it scales with how many players are " +
                "on screen, and the game exposes no setting for it. Only ever lowered, never raised, " +
                "and the launch value is restored when this is turned off. Off by default: joint " +
                "deformation (shoulders, hips) becomes a little coarser.");
            ConfigEntry<bool> detailed = config.Bind("Diagnostics", "DetailedLogging", true,
                "Emit per-reason aggregate repair diagnostics no more frequently than every 30 seconds.");
            return new OptimizerConfig(patches, detailed);
        }

        private static ConfigEntry<bool> BindPatch(ConfigFile config, PatchId id, bool defaultValue) =>
            config.Bind("Fixes", id.ToString(), defaultValue, $"Enable the independent {id} optimization/fix lane.");

        private static ConfigEntry<bool> BindExperimental(ConfigFile config, PatchId id, bool defaultValue) =>
            config.Bind("Experimental", id.ToString(), defaultValue, $"Enable the independent experimental {id} lane.");

        /// <summary>
        /// Binds one switch for a set of patches that are only correct together, and maps every member
        /// to that single entry. <see cref="TryGetPatchIds"/> then hot-reloads the whole group at once.
        /// </summary>
        private static void BindGroup(
            ConfigFile config,
            Dictionary<PatchId, ConfigEntry<bool>> patches,
            string section,
            string key,
            bool defaultValue,
            string description,
            params PatchId[] members)
        {
            ConfigEntry<bool> entry = config.Bind(section, key, defaultValue, description);
            for (int i = 0; i < members.Length; i++)
                patches.Add(members[i], entry);
        }
    }
}
