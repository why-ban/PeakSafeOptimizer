# Changelog

## 1.0.0

First public release. 45 patches, 41 switches, 39 patches enabled by default.

### Vanilla fault repairs

Seven guards that remove per-frame exception floods. Each skips only the frame whose data is missing and defers to the untouched original otherwise.

- `RunBasedValues` - null run-data dictionaries during serialization
- `RopeSegmentGuard` - empty rope segment list while climbing (184 exceptions in one session)
- `IKItemGuard` - item prefab missing hand nodes during an equip (135)
- `EmoteWheelGuard` - incomplete local character while spectating, respawning or quitting (2315)
- `PocketBehaviorGuard` - unlinked `Character.player` during the join window (14)
- `BinocularSunGuard` - two unguarded singletons in the astronomy-badge check, one throw per frame while binoculars are raised (1544)
- `ItemOptimizerDeregisterGuard` - vanilla passes a null key to `Dictionary.Remove` from its own disable-on-null path (67)
- `Parachute` - conservative cache clear on incomplete character/slot/backpack state

### Equivalence-proven optimizations

Same result as vanilla, less work. Squared distances, hashed Animator parameters, dictionary lookups, merged scans, and identity filters that skip a write only when the write provably changes nothing.

`Campfire`, `CampfireProtectionMerge`, `HeatEmissionScan`, `RagdollPhysicsMats`, `ItemCollisionMode`, `GenericOptimizerRange`, `ItemDatabaseNameLookup`, `IsLookedAtScan`, `ItemAudioManagerHash`, `BarAfflictionLayout`, `AnimatedMouthMaterial`, `CollisionCharacterLookup`, `AnimatorValuesHash`, `PlayerNameUiWrites`, `BodypartDragIdentity`, `BodypartMovementForceIdentity`, `LightVolumeSampleCache`, `CollisionContactsNoAlloc`, `SnowballContactGuard`, `ZombieScanRange`, `ZombieSpawnScanRange`, `SleepingZombieScan`, `WeightRefreshCoalescing`, `ItemScaleRedundantWrite`, `RemoteItemInterpolationThreshold`, `EyeLookComponentCache`, `TumbleWeedTargetScan`, `StatusEmitterScan`, `ParticleCountCullerWrite`

### Visual reduction, on by default

- `ExplosionScale` - 13 orbs per explosion down to 7, with further reduction for clustered and distant explosions. Explosions are visibly smaller; this is the difference between a playable and an unplayable moment when a lobby throws dynamite together.

### Opt-in, off by default

- `RemoteClusterAnimationThrottle` - remote animation graph evaluated every other physics tick (25Hz stepping, unchanged speed)
- `DetailBodypartThrottle` - remote fingers, jiggle bones, toes and jaw at ~16Hz
- `RemoteRagdollLod` - remote players past 60m become passive ragdolls (one switch, four patches)
- `CollisionCallbackReuse` - engine stops allocating a `Collision` plus `ContactPoint[]` per reported collision
- `Graphics.CapBoneWeights` - two bone weights per vertex

### Notes on what was removed before release

- The only Transpiler was dropped. IL rewriting proved unreliable alongside IL preprocessors, and its win only existed while someone stood on a bridge.
- A shadow distance and cascade cap was dropped: it silently overrode the in-game Shadow Distance setting, and forcing a single cascade made distant shadow edges shift while walking.
- An MSAA override was dropped: turning anti-aliasing off is a visual decision that belongs to the player.
- A Mesa heat-haze suppressor was dropped: the value it targeted measured 0.016 at runtime, i.e. effectively already off.
- Every numeric config entry was replaced by a switch. A number in a config file cannot be validated by the person editing it, and a wrong one fails quietly.
