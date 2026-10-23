using System.Collections.Generic;
using BepInEx.Configuration;

namespace PeakSafeOptimizer.Config
{
    /// <summary>
    /// One entry per independently gated patch. Every member needs exactly one config binding and one
    /// registered <c>PatchDefinition</c>; a member with only one of the two is a bug the startup
    /// report will not catch.
    /// </summary>
    public enum PatchId
    {
        RunBasedValues,
        Parachute,
        RopeSegmentGuard,
        IKItemGuard,
        EmoteWheelGuard,
        Campfire,
        HeatEmissionScan,
        ItemCollisionMode,
        CampfireProtectionMerge,
        ZombieScanRange,
        ZombieSpawnScanRange,
        WeightRefreshCoalescing,
        SleepingZombieScan,
        ExplosionScale,
        ItemDatabaseNameLookup,
        IsLookedAtScan,
        ItemAudioManagerHash,
        BarAfflictionLayout,
        AnimatedMouthMaterial,
        CollisionCharacterLookup,
        PlayerNameUiWrites,
        BodypartDragIdentity,
        LightVolumeSampleCache,
        RemoteClusterAnimationThrottle,
        SnowballContactSnapshot,
        SnowballContactConsume,
        CollisionContactsNoAlloc,
        BodypartMovementForceIdentity,
        BinocularSunGuard,
        ItemOptimizerDeregisterGuard,
        RemoteRagdollLodPhysics,
        RemoteRagdollLodAnimate,
        RemoteRagdollLodMovementForce,
        RemoteRagdollLodAnimator,
        ItemPromptDuplicateRefresh,
        InventoryNameTextRefresh,
    }

    public sealed class OptimizerConfig
    {
        private readonly Dictionary<PatchId, ConfigEntry<bool>> _patches;
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

        internal static ConfigEntry<bool> GraphicsCapBoneWeights;

        internal static ConfigEntry<bool> CollisionCallbackReuse;

        public bool IsEnabled(PatchId id) => _patches.TryGetValue(id, out ConfigEntry<bool> entry) && entry.Value;

        /// <summary>
        /// Resolves a changed config entry to the patch ids it controls. False means the entry is not a
        /// patch switch: the three non-Harmony entries are polled from Plugin.Update instead.
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
        /// Every patch controlled by the same switch, itself included. The installer installs and
        /// removes a group as a unit, so a group whose members are only correct together can never
        /// end up half installed.
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
                [PatchId.ZombieScanRange] = BindExperimental(config, PatchId.ZombieScanRange, true),
                [PatchId.ZombieSpawnScanRange] = BindExperimental(config, PatchId.ZombieSpawnScanRange, true),
                [PatchId.WeightRefreshCoalescing] = BindExperimental(config, PatchId.WeightRefreshCoalescing, true),
                [PatchId.SleepingZombieScan] = BindExperimental(config, PatchId.SleepingZombieScan, true),
                [PatchId.ExplosionScale] = BindExperimental(config, PatchId.ExplosionScale, true),
                [PatchId.ItemDatabaseNameLookup] = BindPatch(config, PatchId.ItemDatabaseNameLookup, true),
                [PatchId.IsLookedAtScan] = BindPatch(config, PatchId.IsLookedAtScan, true),
                [PatchId.ItemAudioManagerHash] = BindPatch(config, PatchId.ItemAudioManagerHash, true),
                [PatchId.BarAfflictionLayout] = BindPatch(config, PatchId.BarAfflictionLayout, true),
                [PatchId.AnimatedMouthMaterial] = BindPatch(config, PatchId.AnimatedMouthMaterial, true),
                [PatchId.CollisionCharacterLookup] = BindPatch(config, PatchId.CollisionCharacterLookup, true),
                [PatchId.PlayerNameUiWrites] = BindPatch(config, PatchId.PlayerNameUiWrites, true),
                [PatchId.BodypartDragIdentity] = BindPatch(config, PatchId.BodypartDragIdentity, true),
                [PatchId.LightVolumeSampleCache] = BindPatch(config, PatchId.LightVolumeSampleCache, true),
                [PatchId.RemoteClusterAnimationThrottle] =
                    BindExperimental(config, PatchId.RemoteClusterAnimationThrottle, true),
                [PatchId.CollisionContactsNoAlloc] = BindPatch(config, PatchId.CollisionContactsNoAlloc, true),
                [PatchId.BodypartMovementForceIdentity] =
                    BindPatch(config, PatchId.BodypartMovementForceIdentity, true),
                [PatchId.BinocularSunGuard] = BindPatch(config, PatchId.BinocularSunGuard, true),
                [PatchId.ItemOptimizerDeregisterGuard] =
                    BindPatch(config, PatchId.ItemOptimizerDeregisterGuard, true),
                [PatchId.ItemPromptDuplicateRefresh] =
                    BindPatch(config, PatchId.ItemPromptDuplicateRefresh, true),
                [PatchId.InventoryNameTextRefresh] =
                    BindPatch(config, PatchId.InventoryNameTextRefresh, true),
            };

            BindGroup(config, patches, "Experimental", "RemoteRagdollLod", true,
                "Distance tier for remote characters: past 60m a remote, fully controlled, " +
                "non-interacting player stops evaluating its animation graph, joint animation, movement " +
                "force and Animator parameters, and becomes a passive ragdoll carried by physics and the " +
                "network. Gravity, drag and force flushing keep running. Distance is measured from " +
                "whoever you are currently watching, so spectating does not freeze the lobby around " +
                "you. A distant character visibly freezes its pose. One switch for all four patches, " +
                "which are only coherent together and are installed as a unit.",
                PatchId.RemoteRagdollLodPhysics,
                PatchId.RemoteRagdollLodAnimate,
                PatchId.RemoteRagdollLodMovementForce,
                PatchId.RemoteRagdollLodAnimator);

            BindGroup(config, patches, "Fixes", "SnowballContactGuard", true,
                "Makes Snowball keep a value copy of its collision instead of the engine's Collision " +
                "instance, which is the only cross-frame Collision reference in the game. Inert unless " +
                "Experimental.CollisionCallbackReuse is on. One switch for the writing and the reading " +
                "half, which are useless apart.",
                PatchId.SnowballContactSnapshot,
                PatchId.SnowballContactConsume);

            CollisionCallbackReuse = config.Bind("Experimental", "CollisionCallbackReuse", true,
                "Set Physics.reuseCollisionCallbacks so the engine stops allocating a Collision object " +
                "per reported collision. Only applied while both Snowball guards are installed, and " +
                "restored on patch toggles and shutdown. The game hands the live Collision to mods " +
                "through Bodypart.collisionStayAction and CollisionModifier.onCollide, so a mod that " +
                "keeps one past its callback would start reading a reused instance.");
            GraphicsCapBoneWeights = config.Bind("Graphics", "CapBoneWeights", true,
                "Limit QualitySettings.skinWeights to two bones per vertex. Only ever lowered, never " +
                "raised, and the value found when it was switched on is restored when it is switched " +
                "off. Coarser joint deformation at shoulders and hips.");
            ConfigEntry<bool> detailed = config.Bind("Diagnostics", "DetailedLogging", true,
                "Emit per-reason aggregate repair diagnostics no more frequently than every 30 seconds.");
            return new OptimizerConfig(patches, detailed);
        }

        private static ConfigEntry<bool> BindPatch(ConfigFile config, PatchId id, bool defaultValue) =>
            config.Bind("Fixes", id.ToString(), defaultValue, $"Enable the {id} patch.");

        private static ConfigEntry<bool> BindExperimental(ConfigFile config, PatchId id, bool defaultValue) =>
            config.Bind("Experimental", id.ToString(), defaultValue, $"Enable the {id} patch.");

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
