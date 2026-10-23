# PeakSafeOptimizer

A performance mod for PEAK. 36 patches, all on by default, each one either proven to do the same thing
the game does with less work, or a fix for a game bug that throws an exception every frame. They are
controlled by 34 switches: a few groups of patches only make sense together, so they share one.

> **This mod has moved to a new Thunderstore account.** The account that owns the old page is bound to a
> GitHub account that has been banned, and Thunderstore sign-in goes through GitHub, so that account can
> no longer be logged into and the old page can no longer be updated. It stays frozen at 1.4.1.
> Publishing continues under [khalil2](https://thunderstore.io/c/peak/p/khalil2/) - this release and
> every one after it. If you installed from the old page, switch to the new one to keep getting updates.

## Links

- Thunderstore, current page: <https://thunderstore.io/c/peak/p/khalil2/>
- Thunderstore, old page (frozen at 1.4.1): <https://thunderstore.io/c/peak/p/khalil/PeakSafeOptimizer/>
- Source code and issue tracker: <https://github.com/why-ban/PeakSafeOptimizer>

## Installation

**Mod manager (recommended):** click Install. BepInEx is pulled in automatically.

**Manual:** install [BepInExPack PEAK](https://thunderstore.io/c/peak/p/BepInEx/BepInExPack_PEAK/) first, then drop `PeakSafeOptimizer.dll` into `PEAK/BepInEx/plugins/`.

Works with vanilla players and with players who do not have the mod. Nothing here is networked - every
patch only changes your own client's workload.

## What the patches do

Switch names are the ones you see in the config file. `Fixes.*` are in the `Fixes` section,
`Experimental.*` in `Experimental`, `Graphics.*` in `Graphics`.

### 1. Stops the per-frame exception floods (7 patches)

PEAK throws in several easy-to-hit situations, every single frame. One exception per frame is not free:
each one is a throw, a stack capture and a log write on the main thread. Measured in one session before
these existed: 2315 emote-wheel, 1544 binocular, 184 rope, 135 IK and 67 item-optimizer exceptions.
Each one either skips only the frame whose data is missing, or reproduces the original statement for
statement, so no frame the game would have survived is changed.

- **`Fixes.RunBasedValues`** - stops the throw while saving, when the run-stat dictionaries were never filled in. The game still writes its payload.
- **`Fixes.RopeSegmentGuard`** - stops the throw when you climb a rope whose segment list is empty.
- **`Fixes.IKItemGuard`** - stops the throw while you hold an item whose prefab has no hand node.
- **`Fixes.EmoteWheelGuard`** - stops the throw while the emote wheel is open with an incomplete local player.
- **`Fixes.BinocularSunGuard`** - stops the throw in the astronomy-badge check while binoculars are raised.
- **`Fixes.ItemOptimizerDeregisterGuard`** - stops the game removing a null key from its own item-optimizer dictionary.
- **`Fixes.Parachute`** - rebuilds the parachute cache in the game's own order, so a half-built character can never keep a stale "has parachute".

### 2. Skips writes that change nothing (6 patches)

Writing a value that is already there still costs: a native call, a dirty UI layout, a physics body
update. These skip the write only when the result would be bit-for-bit identical.

- **`Fixes.ItemCollisionMode`** - skips the collision-mode write when the rigidbody already has that mode.
- **`Fixes.BarAfflictionLayout`** - skips the bar width write when the width would not move. The setter dirties the whole canvas layout.
- **`Fixes.BodypartDragIdentity`** - skips the velocity multiply when the effective drag is exactly 1, which is every body part of a fully animated character.
- **`Fixes.BodypartMovementForceIdentity`** - skips adding a zero movement force, which is every body part of everyone not pressing a key.
- **`Fixes.ItemPromptDuplicateRefresh`** - drops an item-prompt refresh that the same frame repeats a few lines later.
- **`Fixes.InventoryNameTextRefresh`** - drops an inventory name rebuild when the label already holds that exact string.

### 3. Cheaper scans, lookups and allocations (15 patches)

Same answer, less work: squared distances instead of square roots, one pass instead of two, a
dictionary instead of a linear search, and no garbage.

- **`Fixes.Campfire`** - one pass over the player list per lit campfire instead of two.
- **`Fixes.CampfireProtectionMerge`** - one roster scan and one distance test per player, instead of two of each, in the same frame.
- **`Fixes.HeatEmissionScan`** - squared distance instead of a square root, per character per frame.
- **`Fixes.IsLookedAtScan`** - distance test before the expensive angle, and each transform read once.
- **`Fixes.ItemAudioManagerHash`** - Animator parameter names hashed once at install instead of every frame.
- **`Fixes.ItemDatabaseNameLookup`** - a dictionary instead of a linear scan for item names.
- **`Fixes.CollisionCharacterLookup`** - a dictionary instead of walking the hierarchy to find out who you stood on.
- **`Fixes.PlayerNameUiWrites`** - writes a nameplate's sprite, active state and alpha only when the value actually changed.
- **`Fixes.AnimatedMouthMaterial`** - skips the mouth material writes while the material already holds the value.
- **`Fixes.LightVolumeSampleCache`** - caches the lightmap sample per voxel instead of one texture read per character per frame for voice occlusion.
- **`Fixes.CollisionContactsNoAlloc`** - reads the collision contact once instead of six times. Only active while `Experimental.CollisionCallbackReuse` is on.
- **`Experimental.ZombieScanRange`** - squared distance in the mushroom zombie's disable check.
- **`Experimental.ZombieSpawnScanRange`** - squared distance, and the transform read once instead of per iteration, in the spawner check.
- **`Experimental.SleepingZombieScan`** - squared distance, and the angle computed only after the distance test passes. The squared form rounds once in float, so it can differ from the original by one rounding step at the threshold; it errs very slightly permissive.
- **`Experimental.WeightRefreshCoalescing`** - one carry-weight refresh per frame instead of one per call.

### 4. Trades a little fidelity for frames (6 patches, 3 switches)

These change what you see or how smooth a remote player looks. They are on by default because that is
the configuration that has been played, but they are the ones to turn off first if you dislike them.

- **`Experimental.ExplosionScale`** - dynamite explosions spawn 5 orbs instead of 10 (3 instead of 4 for the small variants), and when explosions pile up the count drops to 2 and the orbs shrink to 60%. A single explosion is untouched. Fewer orbs also means a softer camera shake, because each orb fires two shakes the game stacks. Damage, knockback and blast radius are unaffected.
- **`Experimental.RemoteRagdollLod`** (4 patches, 1 switch) - a remote player past 60 m stops evaluating animation, IK and movement force, and becomes a passive ragdoll carried by physics and the network. A distant player's pose visibly freezes. Distance is measured from whoever you are watching, so spectating does not freeze the lobby around you.
- **`Experimental.RemoteClusterAnimationThrottle`** - remote players evaluate their animation every other physics tick instead of every tick. Movement speed is unchanged, but the stepping is noticeable up close.

### 5. Engine-level changes (2 switches)

- **`Experimental.CollisionCallbackReuse`** - tells the engine to stop allocating a `Collision` object for every reported collision, the largest single source of collision garbage in a crowded lobby. Two patches, `Fixes.SnowballContactGuard`, exist only for this: Snowball is the one place in the game that keeps a collision past its callback, so it keeps a copy instead. This is a process-wide flag, and the game hands the live collision to mods through two public hooks, so another mod that holds one past its callback would start reading a reused object.
- **`Graphics.CapBoneWeights`** - two bone weights per vertex instead of four or unlimited. Joint deformation at shoulders and hips is a little coarser. The game offers no setting for this.

## Turning things off

Every switch is independent and hot-reloads: change it in a config manager or in
`BepInEx/config/com.peak.safeoptimizer.cfg` and it applies immediately, no restart. A switch that covers
several patches is installed all-or-nothing - if one member cannot install on your build, the others are
withdrawn and the log says which and why. Both engine-level switches put back the value they found when
they are switched off and on game exit, and only while the value in force is still the one they wrote,
so a mod that changed it afterwards keeps its own.

## Verifying it is working

Filter `BepInEx/LogOutput.log` for `PeakSafeOptimizer`. You should see:

```
PeakSafeOptimizer | compatibility mode: certified
PeakSafeOptimizer | totals: registered=36, enabled=36, applied=36, skipped=0, failed=0, module-failures=0
```

`applied` should equal `enabled`, and with a default config both are 36. A lower `enabled` means you
turned a switch off. `failed=0` and `module-failures=0` mean no patch hit a problem.

`compatibility mode: structural-validation` instead of `certified` only means the game build is not the
one this release was audited against. Every patch still checks itself and installs on its own merits;
the label just tells you nobody has re-read that build yet.

## If something goes wrong

Turn off the last switch you enabled. If you enabled nothing, the likely candidates in order are
`Experimental.CollisionCallbackReuse`, `Graphics.CapBoneWeights`, then the three fidelity switches in
section 4. Every switch is independent, so you can bisect by turning half of them off.

`Diagnostics.DetailedLogging` is on by default. It logs the first time each repair reason is hit and
then at most once every 30 seconds per reason, never per frame, and it only counts repairs and
fallbacks, never the normal path. Those lines are the fastest way to see which guard is doing
something.

A crash, a save/network/scene fault, or anything visually broken is worth reporting with the full log.

## Compatibility

- Game version: 3.0.0.
- Built against BepInEx 5.4.23.3, and tested alongside 20+ other mods.
- Patches only vanilla methods, and uninstalls per method, so other mods' patches on the same methods
  are never disturbed.
- No Transpilers. Every patch is a Prefix or Postfix with an exact structure check, and a patch whose
  checks fail simply does not install - it cannot half-apply. A failure at any stage is isolated to
  that one patch, or to that one switch when the switch covers several patches that are only correct
  together.
- Does not touch networking: no Photon serialization rate, no RPC, no remote position interpolation.
- Does not override your graphics settings. Shadow distance, render scale, texture quality, LOD bias
  and anti-aliasing all belong to the in-game menu.
- Does not fix faults caused by other mods.
- MIT licensed.
