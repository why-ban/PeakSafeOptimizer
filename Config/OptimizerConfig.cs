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
    ///
    /// There are exactly two declared exceptions, both on by default despite not being equivalent,
    /// and both spelled out where they are declared below: <see cref="ExplosionScale"/> (fewer
    /// explosion orbs and fewer screen-shake instances) and <see cref="TumbleWeedTargetScan"/> (up to
    /// 0.1s of chase-target lag). Any further exception needs the same treatment: the cost stated in
    /// the entry's own comment, not just in a commit message.
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
        // Stability lane: skips GUIManager.UpdateEmoteWheel only on the frames where vanilla would
        // dereference something unavailable. The chain it walks is short-circuited in two places -
        // canEmote returns early when data.dead, and `canEmote && input.emoteIsPressed` never touches
        // input when canEmote is false - so the guard reproduces both, because skipping a frame vanilla
        // survives would also suppress the branch that closes the wheel and clears usingEmoteWheel.
        EmoteWheelGuard,
        Campfire,
        // Scale lane (Lane E1): scan/redundant-write reduction for multiplayer player counts
        // and ground item counts. Each entry is registered and gated independently.
        HeatEmissionScan,
        ItemCollisionMode,
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
        // ScaleExperimental lane: explosion orb count reduction. The three explosion prefabs that
        // carry ExplosionEffect are the dynamite family, and their authored counts are 3/2 (the main
        // one, i.e. 1 + 3 + 2 * 3 = 10 orbs) and 3/0 (the small and fae variants, 4 orbs) - the 4/2
        // field defaults in the class are overridden by the serialized values. The shipped caps are
        // 2/1, i.e. 5 orbs and 3 orbs. On top of the static caps the patch applies a cluster tier
        // (several explosions close together in time and space) and a distance tier (explosions far
        // from the local viewpoint), both of which can only lower the caps further, and the cluster
        // tier also shrinks the orbs. Fewer orbs also means fewer screen-shake instances, because
        // each orb carries two auto AddScreenshake children - that is the second visual cost and it
        // is why this entry is the one visual reduction that is still on by default.
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
        // Only the target *solve* is throttled - to once every 5 physics ticks, i.e. 0.1s. AddForce
        // still runs every tick with the audited magnitude, and its direction is recomputed from the
        // cached target's current Center, so a cached target that keeps moving still steers the
        // tumbleweed continuously; a target that died or was destroyed forces an immediate rescan.
        // The cost, and the reason this is the second declared exception to the default-on policy: a
        // newly closer character can be ignored for up to 0.1s, so a tumbleweed can keep chasing the
        // previous player for that long before switching. It is only observable on the desert/mesa
        // biomes where tumbleweeds spawn, only when two players contend for the same one, and only as
        // a fraction of a second of steering lag - while the saving is O(tumbleweeds x players) angle
        // plus distance work at 50Hz, which the user confirmed measurably affects framerate there.
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
        // once through Collision.GetContact(0) instead of six times through the allocating
        // Collision.contacts array (CharacterMovement.cs:928, :948, and twice each on :953 and :955).
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
        /// make sense together. The remaining entries (DetailedLogging, CapBoneWeights,
        /// CollisionCallbackReuse) are not patch toggles and return false: the first is read at
        /// runtime, the other two drive the non-Harmony lanes that Plugin.Update polls.
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

        /// <summary>
        /// Every PatchId controlled by the same switch as <paramref name="id"/>, including itself, in
        /// declaration order. A single-patch switch returns a one-element list. Used by the installer
        /// to keep a group all-or-nothing: a group whose members are only correct together must never
        /// end up half installed, so if one member cannot install the ones that did are withdrawn.
        /// </summary>
        public IReadOnlyList<PatchId> GetGroupMembers(PatchId id)
        {
            if (_patches.TryGetValue(id, out ConfigEntry<bool> entry) &&
                _byEntry.TryGetValue(entry, out List<PatchId> members))
            {
                return members;
            }
            return new[] { id };
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
                [PatchId.ItemCollisionMode] = BindPatch(config, PatchId.ItemCollisionMode, true),
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
                    BindPatch(config, PatchId.ItemOptimizerDeregisterGuard, true)
            };

            // Groups: sets of patches that are only correct together, so they get one switch each
            // rather than one per patch. A per-patch switch would invite a half-enabled combination
            // that looks supported and is not. The installer enforces this too: a group is installed
            // all-or-nothing, and a member that cannot install withdraws the ones that did
            // (PatchInstaller.InstallGroup).
            //
            // RemoteRagdollLod: four patches sharing one distance-tier decision
            // (Patches\ScaleExperimental\RemoteRagdollLod). Enabling only some of them means a remote
            // character has its animation graph skipped but keeps being driven towards animation
            // targets, or the reverse - neither is a state the tier was reasoned about in.
            BindGroup(config, patches, "Experimental", "RemoteRagdollLod", false,
                "Distance tier for remote characters: past 60m a remote, " +
                "fully controlled, non-interacting player stops evaluating its animation graph, joint " +
                "animation, movement force and Animator parameters, and becomes a passive ragdoll " +
                "carried by physics and the network. Gravity, drag and force flushing keep running. " +
                "Distance is measured from whoever you are currently watching, not from your own " +
                "corpse, so spectating does not freeze the lobby around you. " +
                "Off by default: a distant character visibly freezes its pose. One switch for all four " +
                "patches, which are only coherent together and are installed as a unit.",
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
                "the game that keeps a Collision reference across frames. Turned off again before any " +
                "patch toggle and on game exit, and only ever restored to the value it found while " +
                "that value is still the one this lane wrote. Off by default because it " +
                "is a process-wide engine flag: the game side is audited and guarded, but the game " +
                "also hands the live Collision to mods through Bodypart.collisionStayAction and " +
                "CollisionModifier.onCollide, so a mod that keeps one past its callback would start " +
                "reading a reused instance.");
            GraphicsCapBoneWeights = config.Bind("Graphics", "CapBoneWeights", false,
                "Limit QualitySettings.skinWeights to two bones per vertex. Bones per vertex is a " +
                "per-vertex cost of every skinned character, so it scales with how many players are " +
                "on screen, and the game exposes no setting for it. Only ever lowered, never raised, " +
                "and the value found when it was switched on is restored when it is switched off. " +
                "Off by default: joint deformation (shoulders, hips) becomes a little coarser.");
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
