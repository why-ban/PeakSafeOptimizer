# Changelog

## 1.2.0

44 patches and 40 switches, down one from 1.1.0: `ParticleCountCullerWrite` was removed. A third
audit pass, this time with the shipped game assets decoded rather than only the decompiled code,
which corrected several claims this mod had been making since 1.0.0 - including the one that made
that patch pointless. One of the fixes is a behaviour regression 1.1.0 introduced; the rest are
correctness, blast-radius and honesty repairs.

### Removed

- `ParticleCountCullerWrite`. Decoding every `.assets` file and every level shows the component it
  patches, `ParticleCountCuller`, has **zero instances** in the shipped game - the class is compiled
  into the assembly but nothing carries it. Its target method is therefore never called, so the patch
  could only ever cost a detour. It followed the rule the reserved config keys and `JointProjection`
  were dropped under: a patch whose win is provably zero does not ship.

### The regression, first

- `EmoteWheelGuard` could leave the emote wheel stuck open. Vanilla short-circuits in two places -
  `canEmote` returns early for a dead character without reading `refs.stats`, and
  `canEmote && input.emoteIsPressed` never touches `input` when `canEmote` is false - so on those
  frames vanilla does not throw at all; it falls through and *closes* an open wheel. The guard was
  skipping the whole method there, suppressing that close and latching `usingEmoteWheel`, which also
  latches the pause-menu gate, the cursor lock and a movement gate. The guard now mirrors both short
  circuits and only skips a frame where vanilla would genuinely dereference a null. Introduced by the
  `input` check added in 1.1.0.

### What the assets said

- An explosion was never 13 orbs. The `4 / 2` defaults in `ExplosionEffect` are overridden by the
  serialized values on the three prefabs that actually carry the component: the main dynamite is
  `3 / 2`, i.e. **10** orbs, and the small and fae variants are `3 / 0`, i.e. **4**. The old cap of
  `3 / 1` therefore did nothing at all to the first number. The cap is now `2 / 1`: 5 orbs on the
  main dynamite, 3 on the variants.
- `ExplosionScale` does not cover every explosion. Reverse-resolving every `explosionPrefab`
  reference in the asset library finds exactly three `ExplosionEffect` instances, all dynamite. The
  rocket, rocket-pack and ghost-ball explosions instantiate prefabs whose cost is in particle
  systems and which carry no `ExplosionEffect` at all, so they are untouched. Said plainly in the
  README now instead of being listed as covered.
- Fewer orbs also means a softer camera shake. Every orb prefab carries two self-firing
  `AddScreenshake` children, and the game *stacks* those rather than replacing them, so orb count and
  shake strength move together. That is a second visual cost this mod was not declaring. It is
  declared now; the switch stays on by default, because the explosion case is the one where the
  default-off rule would leave the worst stutter in the mod unaddressed.
- The claim that shrinking the orbs is where most of the win comes from is withdrawn, and this one is
  now settled rather than merely unproven: both particle systems inside an orb are authored with a
  *local* scaling mode, so the root scale the shrink writes never reaches them. Their particle sizes,
  burst counts and lifetimes are unaffected. The shrink only reduces the orb's sphere mesh, so the
  count reduction is what cuts the particle work, the mesh work and the screen shake together.

### Two more asset corrections, no code change

- The spore volumes really do run unthrottled: 1571 `StatusEmitter` plus 108
  `WindAffectedStatusEmitter` instances exist and nothing distance-gates them. The game's own script
  disabler, `GenericOptimizer`, is only on arrow shooters and venus fly traps. The spore clouds do
  carry a `ParticleCuller` on their particle child, but it only starts and stops the particles - it
  cannot touch the damage volume, and the manager that drives it visits three cullers per frame.
- `GenericOptimizerRange` is worth more than its own comment claimed, not less. All 2133
  `GenericOptimizer` instances override the range with a finite value (82m or 60m); the
  `float.PositiveInfinity` in the class is only the field default, and serialized values win.

### Declared, not changed

- `TumbleWeedTargetScan` is now written down as the second declared exception to the default-on rule,
  alongside `ExplosionScale`. It has shipped on by default since 1.0.0 while not being behaviourally
  equivalent, and that was never said out loud. What it gives up, exactly: the target solve runs once
  every 5 physics ticks (0.1s) instead of every tick, so a newly appearing closer or better-angled
  character is adopted up to 0.1s late. The force is still applied every tick, and its direction is
  recomputed from the cached target's current centre, so the lag only affects *which* character is
  chased, never the tracking of the one already chosen; a target that died or was destroyed forces an
  immediate rescan. Exposure is narrow - tumbleweeds only exist in the desert / mesa biome. The switch
  stays on by default, because that is the map the framerate complaint came from. No code changed.

### Correctness

- `StatusEmitterScan` restored vanilla's ordering in the warning-end branch. It cleared the
  in-zone flag *after* calling the screen effect, so an effect that threw left the flag set and the
  branch re-entered - and re-threw - every following frame, where vanilla's single throw is
  self-limiting.
- `StatusEmitterScan` now reads `__runOriginal`. It is a full replacement with side effects - it
  applies spore damage and mutates a static dictionary - and without that parameter another mod's
  decision to suppress the original was silently ignored.
- `StatusEmitterScan` no longer reaches its static overlap dictionary through reflection on the hot
  path, and its two deferral paths were reworked: an unavailable ragdoll centre is now *handled*
  rather than handed back for the original to throw on again, and the afflictions check moved to the
  point where vanilla actually dereferences it, which is a damage tick rather than every frame.
- The distance tier of `RemoteRagdollLod` measures from whoever you are watching, not from your own
  character. While spectating or after a warp those are different points, and the old basis could
  freeze the pose of someone standing right in front of the camera.
- A downgraded remote character no longer keeps a stale crouch or sprint height. The two target
  heights are written only by the method the tier skips, and they feed the standing force and the
  ground-check ray length, so a pose change now returns the character to full behaviour.
- The warm-up before a first downgrade counts physics steps instead of rendered frames. It exists to
  guarantee the animation targets have been written at least once, and those are produced per physics
  step, so at a high frame rate the old count could elapse before a single one had run.
- The physics-step index is derived from `Time.fixedTimeAsDouble`. The float it used accumulates from
  process start and eventually stops being able to separate two adjacent steps.
- The phase that staggers throttled characters no longer uses the low bits of the Unity instance id.
  Objects created in one batch get consecutive ids, so with an interval of two every remote character
  could land on the same tick - turning the flat cost the stagger exists to spread back into a spike.
- A failing replacement in `RemoteClusterAnimationThrottle` now disables itself for that one
  character rather than for the whole session, with a process-wide cap for genuinely systemic
  failures.

### Blast radius

- A switch that covers several patches is now installed all-or-nothing. If one member cannot install,
  the ones that did are withdrawn and reported, because those sets exist precisely because a
  half-installed combination is a state nothing was reasoned about.
- Turning off the Snowball guard no longer leaves `Physics.reuseCollisionCallbacks` on for a frame.
  The flag is lowered before any patch toggle and re-armed after, instead of waiting for the next
  frame's poll - that window ran vanilla Snowball against exactly the reused instance the flag's
  safety argument depends on it not seeing.
- Both engine-level lanes now restore only their own write, and only while it is still the live
  value, and they re-sample on every enable. Previously the collision flag was written back
  unconditionally, which would clobber another mod that had set it, and both lanes could restore a
  stale snapshot after an off/on cycle.
- The two Snowball patches finally register their `Reset`; it had been dead code.
- A patch whose `Reset` threw is no longer reported as installed forever. It used to answer "already
  installed" to every later enable while its Harmony methods were long gone.
- A failed startup now shuts the engine-level lanes down even when the Harmony uninstall did not
  fully succeed. The component keeps receiving `Update` either way.
- `BinocularSunGuard` also guards the ragdoll centre the method dereferences past its own guard
  clause, and it now reproduces vanilla's short-circuit before checking anything behind it, so a
  non-Mesa or night-time frame no longer books repair reasons vanilla never earned. Those counters
  are quoted as evidence of vanilla defects, so a false positive costs more than the branch.

### Documentation corrections

- `CharacterMovement.OnCollision` reads the allocating `Collision.contacts` property **six** times, not
  seven: `CharacterMovement.cs:928`, `:948`, `:953` twice and `:955` twice. The docs and the patch's own
  comment had claimed seven since 1.0.0. The patch itself is unchanged - it still reads `GetContact(0)`
  once - only the count quoted for the vanilla method was wrong.
- `IKItemGuard` now books its repair reason separately for a missing right-hand node and a missing
  left-hand node, so the log says which side is absent instead of only that one of them is.
- `WeightRefreshCoalescing` no longer books a diagnostic reason on its success path. Booking one there
  broke the project rule that repair counters only ever record repairs and fallbacks, which is what
  makes a non-zero counter evidence of a defect.

None of the above is verified in game yet. See TESTING.md.

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

Eight guards that remove per-frame exception floods. Each skips only the frame whose data is missing and defers to the untouched original otherwise.

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
