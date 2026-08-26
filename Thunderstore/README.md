# PeakSafeOptimizer

A performance mod for PEAK aimed at the case that actually hurts: **a full lobby standing in one place**.

Most of what it does is invisible. 39 of its 45 patches are *equivalence-proven* - they produce bit-for-bit the same result as the vanilla code, just with less work - or they repair a vanilla fault that throws an exception every frame. Anything that trades appearance, feel or animation fidelity for frames is **off by default**, because that trade is yours to make, not the mod's.

There are no numbers to tune. Every threshold is a constant in the code, next to the reasoning that picked it.

## Installation

**Mod manager (recommended):** click Install. BepInEx is pulled in automatically.

**Manual:** install [BepInExPack PEAK](https://thunderstore.io/c/peak/p/BepInEx/BepInExPack_PEAK/) first, then drop `PeakSafeOptimizer.dll` into `PEAK/BepInEx/plugins/`.

Works in multiplayer with vanilla players and with players who do not have the mod. Nothing it does is networked - every patch acts only on your own client's workload.

## What it actually does

**Removes per-frame exception floods.** PEAK throws a few `NullReferenceException`s every single frame in situations that are easy to hit (rope climbing with an empty segment list, the emote wheel while spectating or respawning, hand IK during an item swap, pocket items during the join window). An exception per frame is not free. Eight guards cut those to zero - each one skips exactly the frame whose data is missing and defers to the untouched original otherwise. Measured in one session: 2315 + 1544 + 184 + 135 + 67 exceptions eliminated. The binocular one alone was 1544 - one per frame for as long as anyone held the binoculars up, each costing a throw, a stack capture and a synchronous log write on the main thread.

**Cuts redundant native calls.** A large part of the per-frame cost in a multiplayer lobby is the game writing values that are already what it is about to write, or recomputing something it computed a moment ago. Examples: `Bodypart.Drag` multiplies two velocity vectors by exactly `1f` for every bodypart of every ragdolled character, 50 times a second; `Bodypart.AddMovementForce` adds the zero vector for everyone who is not pressing a movement key; the `LightVolume` lightmap is sampled per character per frame for voice occlusion when clustered players share the same voxel. These are skipped only when the skip is provably an identity.

**Replaces scans and lookups with cheaper equivalents.** Squared distances instead of square roots, string Animator parameters hashed once instead of per frame, a dictionary instead of a linear search, one full player scan instead of two in the same frame.

**Reduces garbage.** `CharacterMovement.OnCollision` evaluated the allocating `Collision.contacts` property seven times per contact; it now reads contact zero once. Optionally (see below) the engine can be told to stop allocating a `Collision` object plus a `ContactPoint[]` for every reported collision at all.

**Scales down explosions.** Vanilla spawns 13 orbs per explosion, each an animated transparent sphere about ten metres across. This caps a single explosion at 7 orbs at its authored size, and when explosions pile up in the same place it drops the count to 2 and shrinks the orbs to 60%. The shrink is the part that matters: with ten sticks detonating inside one metre, the frame is spent filling the screen several times over, not on spawning orbs, so reducing the count alone barely moved it. A clustered explosion therefore looks smaller per orb, but you are seeing the union of many of them, so the fireball stays wider than any single blast. A lone explosion is not shrunk at all. Damage, knockback and blast radius are untouched - those live in a separate component.

**Cheapens the swamp spore volumes.** Every poison-gas volume in the level runs its proximity check every frame with no distance gate of its own - two to four square roots and two to four ragdoll dictionary lookups each, plus a string-keyed dictionary hash while you stand in one. The replacement resolves your position and its distance once per volume and reuses both, and replaces the overlap-priority lookup with a single dictionary probe. The distance is still a real square root, because a squared comparison is not exact at the boundary and the boundary is a damage tick. The damage itself is reproduced line for line. Note this is CPU only: the clouds themselves are authored particle systems, and whether they are covered by the game's own particle culler is a per-prefab fact this mod cannot see.

## Optional switches

All off by default. Turn them on if you want the frames more than the fidelity.

| Switch | What you gain | What it costs |
|---|---|---|
| `Experimental.RemoteClusterAnimationThrottle` | Remote players evaluate their animation graph every other physics tick instead of every tick - roughly halves the single most expensive managed per-character cost in a cluster | Remote animation steps at 25Hz. Speed is unchanged (the accumulated delta time is passed through), but the stepping is noticeable up close |
| `Experimental.DetailBodypartThrottle` | Fingers, jiggle bones, toes and jaw of remote players animate at ~16Hz | Those parts lag slightly. Spine, limbs and held items are untouched |
| `Experimental.RemoteRagdollLod` | Remote players past 60m stop evaluating animation entirely and become passive ragdolls carried by physics and the network | A distant player's pose freezes. One switch covers four patches that are only coherent together |
| `Experimental.CollisionCallbackReuse` | The engine stops allocating a `Collision` object and a `ContactPoint[]` for every reported collision - the largest single source of collision garbage in a cluster | A process-wide engine flag. The game side is fully audited (exactly one place keeps a collision across frames, and it is guarded), but another mod that holds a `Collision` past its callback would start reading a reused instance |
| `Graphics.CapBoneWeights` | Two bones per vertex instead of four or unlimited - a per-vertex cost of every character on screen, and the game offers no setting for it | Joint deformation at shoulders and hips is a little coarser |

Every switch hot-reloads: change it in a config manager and it installs or uninstalls immediately, no restart. The two engine-level lanes restore what they found when switched off, and on game exit.

## What it deliberately does not do

**It does not override your graphics settings.** Shadow distance, render scale, texture quality, LOD bias and anti-aliasing all belong to the in-game menu. An earlier version capped shadow distance and cascade count; that was removed, because it silently overrode a choice the player had already made - and raising the setting appeared to do nothing.

**It contains no Transpiler.** Every patch is a Prefix or Postfix with an exact structure check. IL rewriting is fragile in a heavily modded environment, and the one Transpiler that survived testing was dropped before release rather than shipped.

**It does not touch networking.** No Photon serialization rate, no RPC, no interpolation of remote positions. Two patches whose target is an RPC entry point are held to the strictest validation tier.

**It does not fix other mods.** Faults caused by other mods are out of scope.

## Safety model

Each patch validates the game build itself before installing: declaring type, member type, exact signature, and for the enum-driven ones the member order. If anything does not match, that patch reports `SkippedStructureMismatch` and stays uninstalled. The others are unaffected.

Patches that replace audited behaviour additionally require the game assembly to match the audited build. **After a PEAK update, those skip themselves safely rather than running against code they were not reasoned about.** The startup log names each one. Nothing is ever installed on a "probably fine" basis.

A failure at any stage - factory, initialization, target resolution, Harmony patching - is isolated to that one patch and logged. Uninstall is per method and verified against Harmony's own patch info; `UnpatchSelf` is never called, so other mods' patches on the same methods are never disturbed.

## Verifying it is working

Filter `BepInEx/LogOutput.log` for `PeakSafeOptimizer`. You should see:

```
PeakSafeOptimizer | compatibility mode: certified
PeakSafeOptimizer | totals: registered=45, enabled=39, applied=39, skipped=6, failed=0, module-failures=0
```

`applied` should equal `enabled`. The six skipped lines are the default-off switches. `failed=0` and `module-failures=0` mean no patch hit a problem.

`compatibility mode: structural-validation` instead of `certified` means the game has been updated: structural guards still validate independently and run, behaviour-replacement patches skip themselves.

## If something goes wrong

Turn off the last switch you enabled. If you enabled nothing, the likely candidates in order are `Experimental.CollisionCallbackReuse` (if on), then any of the throttles (if on), then `Experimental.ExplosionScale`. Every switch is independent.

`Diagnostics.DetailedLogging` is on by default. It logs a line the first time each repair reason is hit and then at most once every 30 seconds per reason - it never spams per frame, and it only counts repairs and fallbacks, never the normal optimized path. Those lines are the fastest way to tell which guard is doing something.

A crash, a save/network/scene fault, or anything visually broken is worth reporting with the full log.

## Compatibility

Built against PEAK with BepInEx 5.4.23.3 and tested alongside 20+ other mods. It patches only vanilla methods, so it does not conflict with content mods. Where the game itself writes a property every frame, this mod deliberately uses a different channel so the two cannot fight each other.

MIT licensed.
