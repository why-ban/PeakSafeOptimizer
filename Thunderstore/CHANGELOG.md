# Changelog

## 1.1.0

Same 45 patches and 41 switches as 1.0.0, no additions and no removals. This release is a second audit pass that corrects four equivalence defects, widens two guards, and fixes `ExplosionScale`, whose cluster reduction turned out not to trigger in the case it was written for.

### Equivalence repairs

- `StatusEmitterScan` - the replacement returned early from the warning branch, where vanilla falls through into the tail that clears the tick timer. A poison volume you walked out of therefore kept its accumulated tick time instead of resetting it, so re-entering could deal a tick sooner than vanilla. The branch now falls through exactly as the audited method does, and the warning FX is resolved before any field is written so a deferral leaves the emitter untouched.
- `StatusEmitterScan` - the range test is back to a real square root. Comparing squared magnitudes is not exact: the two forms disagree inside the rounding band around the threshold, and the in-range side of that band is a live damage tick. The saving that remains is computing the distance once per call instead of the two to four times vanilla computes it, plus one `TryGetValue` in place of a `ContainsKey` and three string-keyed indexer lookups.
- `ParticleCountCullerWrite` - the redundant-write filter remembered the last value this patch wrote. That is only equivalent while nothing else writes the same emission module, and the game does write one elsewhere (the bee swarm decrements its own emission multiplier every frame while dispersing). The filter now compares against the live value on the particle system, which stays exact no matter who else writes it.
- `RemoteClusterAnimationThrottle` - the explicit `RigBuilder.SyncLayers()` call was removed on the grounds that `Evaluate` performs it. It does, but only when the builder owns a valid graph of its own, and PEAK builds the rig into the animator's graph instead, which leaves that property unset. `Evaluate` was therefore a no-op and the IK constraint data stopped being pushed into the jobs. Both calls are restored, as in the audited method.

### Wider guards

- `BinocularSunGuard` - also guards `MainCamera.instance` and the achievement manager singleton, which the same method dereferences unguarded past its own guard clause.
- `EmoteWheelGuard` - also guards `Character.input`, the one remaining unguarded link in the chain the method walks, and reports `localCharacter` separately from the rest.

### Behaviour on failure

- `RemoteClusterAnimationThrottle` - a replacement that fails on the same physics tick it was handed no longer yields that tick to the original, because doing so would run `RotateCharacter` twice on one tick and leave the rig root rotated. The tick stays skipped; every later tick goes to the original.

### `ExplosionScale` now attacks the right cost

Reported: ten sticks of dynamite detonating inside one metre still ran at about five frames per second. Confirmed as a design error rather than a failure to apply.

- The cluster tier could not trigger in that scenario. It needed two prior explosions within 1.2s, but a single explosion keeps spawning orbs for a full second after it starts, and its neighbours are what the new orbs draw over - so the window was shorter than the overlap it was meant to detect. The window is now 2.5s and the light tier starts at one neighbour instead of two, so every explosion after the first in a pile takes a reduction.
- Reducing the orb count was never going to fix it on its own. An orb is an animated transparent sphere roughly ten metres across, so an observer inside the pile has every surviving orb covering the whole screen; the frame cost is orb count times screen area and only the count was being reduced. The cluster tiers now also shrink the orbs, to 85% and 60% of their authored size, which is where most of the saving comes from - screen area goes with the square of the radius.
- A lone explosion is unaffected: it sees no neighbours, so its size is exactly what the prefab authored. The size field is only ever lowered, feeds no damage path, and is read by exactly one line in the whole assembly.

None of the above is verified in game yet. See TESTING.md, steps A6, A8, A9, B13, E1 and E2.

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
