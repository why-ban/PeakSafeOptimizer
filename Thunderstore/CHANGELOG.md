# Changelog

## 1.5.1

- Updated for game 3.0.0.
- Removed `EyeLookComponentCache`. The game now does the same optimisation itself, and does it faster than this patch did.
- Fixed `AnimatedMouthMaterial`, which could throw every frame on a remote player's mouth.
- Added four missing structure checks, so a future game update drops the affected patch instead of misbehaving.

## 1.5.0

- Updated for game 2.6.b. No patch needed changing.
- Removed four patches that had stopped earning their place: `DetailBodypartThrottle`, `TumbleWeedTargetScan`, `ItemScaleRedundantWrite`, `RemoteItemInterpolationThreshold`.
- Added two: `ItemPromptDuplicateRefresh` and `InventoryNameTextRefresh`. Both drop a refresh the same frame repeats anyway.
- Fixed the lightmap cache serving the previous level's data after a load, and the mouth patch reading the push-to-talk setting before the game's own checks.
- Every switch is on by default now. That is the configuration that has actually been played.
- New icon.

## 1.4.1

- Removed `PocketBehaviorGuard`, which had been guarding a null the game had already fixed, and `AnimatorValuesHash`, whose component does not exist in the game at all.
- Fixed `IKItemGuard` skipping the wrong hand. For two releases it froze the left IK hand on items missing a left hand node.

## 1.4.0

- Removed the "certified build" gate. It used to skip almost the whole mod on any game update, before a single patch got to check whether the things it reads had changed. Each patch now decides for itself.
- Removed three patches the game had made redundant: `GenericOptimizerRange`, `StatusEmitterScan`, `RagdollPhysicsMats`.
- Adapted four patches to the new build, and taught `RemoteRagdollLod` about characters carried by a condor.

## 1.3.0

- Fixed `IKItemGuard` freezing the right IK hand on items that have a right hand node but no left one.
- Fixed three bugs in the machinery that installs a multi-patch switch as a unit, which could leave half a switch installed with nothing in the log.
- `ExplosionScale`'s overlap radius is now derived from the shipped assets instead of being a hard-coded 3.5 m.
- Fixed several repair counters that were counting normal operation as a repair.
- Removed a per-call allocation and a handful of repeated native reads from hot paths.

## 1.2.0

- Removed `ParticleCountCullerWrite`. Its component has zero instances in the shipped game, so the patch could only ever cost a wrapper.
- Fixed `EmoteWheelGuard` being able to leave the emote wheel stuck open.
- Corrected the orb numbers: an explosion is 10 orbs, not 13. Fewer orbs also means a softer camera shake, which is now declared.
- A switch that covers several patches now installs all-or-nothing.
- The two engine-level lanes now restore only their own value, and only while it is still the value they wrote.

## 1.1.0

- Second audit pass: four patches fixed where the replacement did not quite match the original.
- `ExplosionScale` now actually helps with a pile of dynamite, by shrinking the orbs as well as reducing how many there are.
- `BinocularSunGuard` and `EmoteWheelGuard` widened to cover more of the null chain.

## 1.0.0

First release.
