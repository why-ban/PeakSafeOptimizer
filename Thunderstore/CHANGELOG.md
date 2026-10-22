# Changelog

## 1.5.0

Game 2.6.b (`Assembly-CSharp` MVID `60e20675-...`). Scoping the update by hash-diffing the two
decompiled trees: 23 of 24,489 source files differ, all in `Assembly-CSharp`, and not one type was
added or removed. None of the 39 patch targets was among them, so the update itself needed no
adaptation - the 1.4.1 build installed all 39 on 2.6.b unchanged.

What this release contains is the result of a full re-audit of every patch against a fresh decompile
and a fresh asset census, plus an optimisation hunt across the whole game. The audit found no crash
and no wrong-pixel bug, but it did find four patches that had stopped earning their place, two real
divergences, one cache that could go stale across level loads, and a set of missing structure
assertions.

### Four patches deleted

A patch that cannot pay for its own Harmony wrapper is a liability, and three of these could not.

`DetailBodypartThrottle` only ever skipped the 59 "detail" body part types - subdivided fingers,
jiggle bones, toes, mouth and jaw. A census of the shipped assets shows 472 body part instances using
exactly 18 part types, all core ones: no finger, jiggle, toe or jaw part exists in the game at all, so
the patch never skipped anything.

`TumbleWeedTargetScan` re-solved a tumbleweed's chase target every fifth physics tick. The whole
shipped game contains exactly one tumbleweed prefab, no code spawns it, and the saving was one angle
plus one distance compare per tick. It was also the only remaining patch that deliberately traded
behaviour (up to 0.1s of target lag) for that.

`ItemScaleRedundantWrite` targeted `ItemScaleSyncer.Update`, and `ItemScaleSyncer` is attached to
exactly one prefab in the game (the snowball). Nothing adds it at runtime.

`RemoteItemInterpolationThreshold` skipped the remote ground-item interpolation only when the
interpolated target was bitwise equal to the body's position. Vanilla moves the body half the
remaining error per physics step, so it converges geometrically and never lands bitwise on the
target: the skip was unreachable in steady state, and every remote item paid the gate chain twice.

### Two patches added

`ItemPromptDuplicateRefresh` and `InventoryNameTextRefresh`, both in the scale lane and both
equivalence-proven. The first drops a per-frame item-prompt refresh that the same frame repeats
unconditionally a few lines later; the second drops a per-frame inventory name rebuild whose only
effect is writing back the string that is already in the label. Together they remove about 13
allocations and 16 dictionary lookups per frame. The second one records the string reference the
original wrote and only skips while that reference is still the live label, so a third-party mod that
writes its own text forces a real refresh.

### Fixes

`LightVolumeSampleCache` keyed its lightmap cache generation on `(Texture3D instance id,
Texture.updateCount)`. Every pixel install in the game destroys the old `Texture3D` and immediately
creates a new one, so `updateCount` is always 1 and the key degenerated to the instance id - which
Unity may recycle after a destroy, and that is exactly the pattern here. A level change could then
serve the previous level's lightmap alpha forever. The generation is now the texture reference itself,
which also removes two native reads from every sample.

`AnimatedMouthMaterial` read the push-to-talk setting before vanilla's `character.IsLocal` short
circuit and without vanilla's aliveness test. Remote mouths therefore paid an extra field read and
delegate call every frame, and a null setting threw on mouths vanilla never touches. It now evaluates
exactly where vanilla does.

`RopeSegmentGuard` skipped the frame on a rope with no climbing API or no segments, but vanilla writes
`ropeClimbWorldNormal = ropeClimbNormal` before it throws on either. The guard now lands that write
before deferring, so the skipped frame leaves the same state vanilla leaves.

`CollisionContactsNoAlloc` and the two Snowball contact guards are only worth anything while
`Physics.reuseCollisionCallbacks` is on: with it off, `Collision` already preallocates its contact
array and the guards guard a bug that cannot happen. All three are now inert unless that lane is
active, so turning the lane off costs nothing instead of paying for three Harmony wrappers.

`BodypartDragIdentity` compared a character reference with Unity's null operator on a path where
vanilla only reads managed fields, paying a native liveness check per body part per physics step.

Missing structure assertions were added to `Campfire` (the `transform` property and the target's
parameter name), `BodypartMovementForceIdentity` (`mass`, `isKinematic`), `RemoteRagdollLodAnimator`
(`CharacterRefs.hip`), `SleepingZombieScan` (`Bodypart.transform`) and `WeightRefreshCoalescing`
(`Ascents.etcDamageMultiplier`). `RemoteRagdollLod` lost a dead overload, and
`WeightRefreshCoalescing` lost a condition that was provably always false.

### Every patch is on by default

A default install should be the configuration that was actually validated. The full surface - all 37
patches plus both engine-level lanes, `Experimental.CollisionCallbackReuse` and
`Graphics.CapBoneWeights` - has been played for many sessions on 1.4.1 without a visible problem, so
1.5.0 ships with everything enabled and every switch still independent. The startup line is now
`registered=37, enabled=37, applied=37, skipped=0`.

### The comments are the code's

The repository had accumulated several thousand lines of commentary that narrated code, re-told the
history of each fix and cited line numbers that no longer existed. It was also where stale claims hid:
this pass found docstrings asserting that the current build had changed a method that is byte-identical
to the previous one. All of it was cut back to the load-bearing minimum - invariants, vanilla ordering
constraints, null-semantics choices, and the one sentence per patch that says why it is equivalent.
About 5,400 lines of commentary are gone; the code is unchanged by that pass.

## 1.4.1

The game shipped another update (Assembly-CSharp MVID `c88cc526-…` -> `e053d265-…`). Only
`Assembly-CSharp.dll` changed - every other game assembly is byte-identical - and no method this mod
patches was rewritten. So this release contains no adaptation work. What it contains is three
long-standing accounting errors, one of which was a real regression.

### `IKItemGuard` reproduced the wrong hand (real regression, fixed)

This patch exists because `CharacterAnimations.ConfigureIK` raises a `NullReferenceException` every
physics frame while you hold an item whose prefab lacks a `Hand_L` or `Hand_R` child. The two cases
are not symmetric: whichever hand the method evaluates *first* throws with no writes landed, and the
other hand has already been written twice by the time it throws, so the patch has to reproduce those
writes before skipping. Getting that assignment wrong freezes one IK hand target or performs writes
vanilla never reaches.

It was wrong. The patch was written against the 1.20 body, which evaluated the right hand first;
from `c88cc526` onward the game evaluates the **left** hand first. The bookkeeping was never flipped
with it, so for two releases a missing `Hand_L` froze the left IK target and a missing `Hand_R`
performed two right-hand writes the original does not reach. Verified at the IL level
(`GetItemPosLeft` at IL_0063, the rotation setter at IL_009f, `SetPositionAndRotation` at IL_00a5,
then `GetItemPosRight` at IL_00da) and fixed: the patch now reproduces the left-hand pair and treats
a missing `Hand_L` as the zero-write case. The two repair reasons swapped names to match.

### Two patches removed

- **`PocketBehaviorGuard`.** It claimed vanilla dereferenced `character.player.itemSlots` unguarded.
  The game added `character?.player == null` plus an `itemSlots` null/length check in `c88cc526` -
  the release this was last audited against - so the guard has been redundant since 1.4.0. Because
  its prefix returned true and let the original run, it was pure per-character per-frame overhead.
  This is the same class of mistake as the three patches 1.4.0 removed; this one was missed.
- **`AnimatorValuesHash`.** A census of every asset file in the shipped game (all `.assets`, every
  `level*`, every `sharedassets*`, `globalgamemanagers`, 810 MonoBehaviour classes with instances)
  finds **zero** `AnimatorValues` instances - against 23 `UIPlayerNames`, 1 `Snowball` and 3
  `ExplosionEffect`, all of which the same sweep resolves. Nothing in the game or in any installed
  mod adds the component in code either. Its target method can never run, which makes it the same
  situation as the `ParticleCountCullerWrite` patch deleted in 1.2.0.

Counts: **39 patches / 35 switches, 33 on by default**, down from 41 / 37 / 35.

### Three comments whose conclusions had gone stale

No behaviour change, but they were load-bearing arguments and are now correct:

- `ExplosionScale` justified leaving nine prefab fields unasserted partly on the grounds that
  `requireCurrentMvid` would skip the whole patch on an unrecognised build. 1.4.0 deliberately
  removed that gate, so the backstop it leaned on no longer exists. The comment now says plainly
  that those nine fields are protected only by re-auditing a fresh decompile after a game update.
- `SleepingZombieScan` called its squared-distance replacement "provably equivalent". It is not
  bit-exact: `t*t` rounds once in float and the comparison moves onto a different quantum, leaving a
  band at most one ULP wide where the two disagree (roughly half of plausible thresholds have such a
  band, and the patch errs slightly permissive). The probability of a real zombie landing in it is
  around 1e-10 per test, so the behaviour is fine, but the comment now says "within one ULP" instead
  of "identical".
- `RemoteRagdollLodAnimator` described a vanilla `Debug.LogWarning` about swapped animator
  controllers. The current build deleted that log line and kept the check, so the comment now
  describes the check without the log.

### Documentation

`README.md` and `TESTING.md` are gone. Their per-patch tables and test checklists duplicated what the
patch docstrings already state, and every code change needed them rewritten, which is a worse use of
attention than the code itself. The source docstrings are the specification. This changelog and the
Thunderstore README cover only what a user needs.

## 1.4.0

The game shipped a low-level update: it now does a batch of its own performance work, plus a console
port. Assembly-CSharp's MVID moved, and three of this mod's patches were made redundant by the game
itself. 41 patches and 37 switches, 35 on by default, down from 44 / 40 / 38.

### The MVID gate is gone, and that is the headline

Until 1.3.0 the installer had this, ahead of every patch's own validation:

    if (definition.RequiresCertifiedBuild && !fingerprint.IsCurrent) return;

43 of the 44 patch modules set that flag. So one game update silently skipped almost the entire mod -
*before* a single patch got to check whether the members it actually reads had changed shape. That is
the opposite of what the per-patch structural validation exists for, and it turned every game update
into a release event. Removed.

The only gate now is each patch's own `Initialize` assertions plus its target signature check: shapes
match and it installs, shapes differ and that one patch reports `SkippedStructureMismatch`. The MVID
is demoted to a log label and `CompatibilityLevel` is demoted to a re-audit priority.
`SkippedUncertifiedBuild` is no longer produced by anything.

The cost, stated plainly: structural validation proves that member shapes are unchanged, not that
method bodies still mean the same thing. Only re-reading a fresh decompile catches the second kind, so
`structural-validation` in the log should be read as "nobody has re-read this build yet - start with
the Fingerprint and StrictIl patches".

### Three patches removed, because the game now does their job

- **GenericOptimizerRange.** `AnyCharacterWithinRange` now squares the range, indexes the list and
  hoists `transform.position` out of the loop - exactly the three things this patch did. Worse, the
  game gave `GenericOptimizer` a *private* cached transform property, which this assembly cannot bind
  to, so C# member lookup falls through to `Component.transform`: the patch became one extra native
  call per instance per frame in exchange for nothing. There are 2133 instances.
- **StatusEmitterScan.** `StatusEmitter` now caches its transform and computes the distance once;
  `Update()` does not even call `InRange()` any more. Counted call for call, the replacement makes the
  same number of native calls and the same one square root as vanilla, and adds a Harmony wrapper
  frame, three delegate null checks, a try/catch region and two FieldRef invocations. Net negative.
- **RagdollPhysicsMats.** `CharacterRagdoll.FixedUpdate` now guards `SetPhysicsMats()` with
  `if (character.IsLocal)`, so this redundancy filter can only save one character's worth of native
  material writes instead of every character's. It was the most complex patch in its lane (a
  ConditionalWeakTable, a two-phase pending/promote signature and a 300-frame / 5-second material
  audit); that trade no longer holds.

### Adapted to the new build

Four real behavioural deviations and one logic gap:

- **RemoteClusterAnimationThrottle** called `SetPhysicsMats` on every tick, evaluating or not. Vanilla
  no longer calls it for remote characters, and remote characters are the only ones this patch touches,
  so continuing to call it would be *adding* per-step native `col.sharedMaterial` writes the game no
  longer performs. Both call sites, the accessor, the delegate and the
  `set-physics-mats-failed` diagnostic are gone.
- **IsLookedAtScan.** `UIPlayerNames.Init` changed from returning an `int` to taking an `Action<int>`
  callback - the one assertion in the whole plugin that would now fail, which after the gate removal
  means the patch would be skipped rather than silently wrong. `Update()` also hoists the
  blind/struggling test to the top and returns early, so the replacement follows, and the camera null
  check moved below that gate: otherwise a null camera while blind would hand the method back, where
  vanilla never touches the camera and cannot throw.
- **BarAfflictionLayout.** The game added `size == 0f -> width = 0f` and an `Approximately` snap. The
  old predicate only forecast the `Lerp`, so it would skip a write that should have snapped `width` to
  `size`. Rewritten branch for branch, and the float equality is now a bit comparison, because
  `-0f == +0f` would drop a real write.
- **GenericOptimizerRange** (confirmed on the way out): the patch's `candidate == null -> continue`
  was itself a deviation. Both the old and the new vanilla loop dereference `character.data`
  unguarded, so a null element throws in both.
- **RemoteRagdollLod** gained `data.carrierCondor != null` in its never-downgrade list. That field is
  new: a character being carried by a condor keeps `currentRagdollControll` at 1, so it passes the
  control threshold, while the condor takes it far past 60m - and none of the three existing carry
  conditions could see it. Such a character would have gone limp mid-flight.

Verified as harmless: `Bodypart`'s `SetPositionAndRotation` merges and the dropped `isKinematic` guard
in `FollowRotation_Rotation` (that guard was dead code - its only caller returns early when kinematic);
`CharacterMovement.FixedUpdate` hoisting four `currentRagdollControll` multiplications out of the loop;
the new `AdjustDrag` (`AdjustDrag(1f) == 1f` exactly, and its preimage is unique, so
`BodypartDragIdentity`'s identity test is unaffected); `ConfigureIK` evaluating the left hand first now
(so `IKItemGuard`'s two per-side reasons swap roles); the `ItemPhysicsSyncer.FixedUpdate` rewrite; and
the squared-distance rewrites in `TumbleWeed.GetTarget` and `GenericOptimizer`.

Every remaining patch's `Initialize` assertions were checked against the shipped DLL with
`System.Reflection.Metadata` rather than against decompiled text. Apart from that one `Init` signature,
all of them still pass.

### Comment convention change

There were roughly 450 `File.cs:NNN` references in the comments. A game update invalidates all of them
at once, and re-anchoring them had grown more expensive than the code fixes. New convention: **name the
type and the method, not the line**. Existing line references are left as historical coordinates rather
than re-anchored one by one.

None of the above is verified in game. See TESTING.md; the 1.4.0 acceptance points are at the top of
its final section.

## 1.3.0

Still 44 patches and 40 switches, 38 on by default. Seven concurrent read-only audits over the whole
plugin, then a repair pass over everything they turned up that could be settled by reading code
rather than by playing. One behaviour regression, three real defects in the group installer, one
threshold that was a magic number and is now derived from the assets, and a long list of comments
that were confidently wrong.

### The regression, first

`IKItemGuard` froze the right IK hand target on any held item whose prefab has a `Hand_R` child but
no `Hand_L`. C# evaluates `a.position = f()` receiver-first, so on such an item vanilla completes
*both* right-hand writes (`CharacterAnimations.cs:397-398`) and only then raises at `:399`. The guard
skipped the whole item branch, so the right hand stopped tracking the item - every physics frame, for
as long as it was held. It now reproduces those two writes through delegates bound to the two
`internal` helpers before skipping, and books the two cases separately
(`right-hand-node-missing` vs `left-hand-node-missing`). This is the same class of mistake as the
1.1.0 emote-wheel regression: skipping the writes vanilla performs *before* it throws.

### Group installs were not all-or-nothing

Three defects, all in the machinery that makes one switch install several patches as a unit.

- HarmonyX registers a prefix into the patch info **before** generating the wrapper
  (`PatchProcessor.Patch`), so when wrapper generation throws, `GetPatchInfo` already lists our
  method. The installer recorded that as an installed patch, `IsInstalled` returned true for a patch
  that had *failed*, and the group rollback was skipped entirely - leaving a half-installed switch
  with nothing in the log to say so. A record in that state is now flagged separately: never live,
  never reinstalled (that would attach the prefix twice), still cleaned up on uninstall.
- Rolling a group back claimed members had been withdrawn cleanly when their `Reset` threw. It asked
  a question that excludes exactly that case, took the success branch, and discarded the real reason.
  It now reports which of the two failure shapes happened.
- A member that was already live before a failed install kept running, unreported. It is now named as
  a deliberately half-installed switch.

Also: the failed-startup rollback shut the engine-global lanes down *after* detaching the patches,
the opposite order from a normal shutdown, leaving a window where `Physics.reuseCollisionCallbacks`
was still set with the Snowball guards already gone.

### The cluster radius is no longer a magic number

`ExplosionScale` used a fixed 3.5m to decide whether an explosion overlaps a recent one. That is
smaller than the main dynamite's own fireball radius, so two detonations 5m apart - visually almost
fully overlapped, which is precisely the fill this patch exists to cut - counted as isolated and each
kept five full-size orbs. The threshold is now derived from the orb geometry the assets carry: the orb
root's localScale (4.4401164) times the Icosphere mesh AABB extent (1.372998), i.e.
`baseScale * 6.0962` - about 6.10m for the main dynamite and 3.05m for the Small/Fae variants. A NaN,
zero, negative or infinite `baseScale` falls back to the old constant. The test uses the *current*
explosion's radius, because what is being decided is whether this fireball lands on screen that is
already covered.

Two more things in that patch:

- The scene-handle debounce is gone. It guarded against last scene's positions being counted as
  neighbours, but the 2.5s window already does that: every scene load in this game is
  `LoadSceneMode.Single` behind a loading screen that also waits for the local character to spawn,
  and segment changes are `SetActive` within one scene. Its only observable behaviour was a false
  `scene-handle-thrash` report, so that reason no longer exists.
- The light tier's caps were `Math.Min(pointCap, 2)` and `Math.Min(subPointCap, 1)` against a 2/1
  base - identity - while the comment claimed it cut counts from one neighbour on. Those caps are now
  named constants of their own, so the no-op is explicit and giving that tier a real reduction is a
  one-constant edit.

### Diagnostics that were measuring the wrong thing

The repair counters are cited as evidence of vanilla faults, so a counter that fires when nothing is
wrong is worse than no counter.

- `WeightRefreshCoalescing` counted a repair reason on the path where the patch *succeeds*.
  `same-frame-duplicate-skipped` is removed.
- `PocketBehaviorGuard` counted one per bot per frame. `Character.player` is null for bots by design,
  so skipping the call is right and the accounting was not - the same exclusion `Parachute` already
  had.
- `AnimatedMouthMaterial` tested the renderer and the texture array before the audio source, but
  vanilla's guard short-circuits on the audio source and runs no body at all, so a mouth with no
  audio source was reported as a fault every frame.
- `RemoteClusterAnimationThrottle` silently swallowed a null rig builder and an invalid animation
  graph and reported success. Vanilla raises there every tick. The guards stay - reintroducing a
  per-step exception would be worse - but a malformed rig is now visible as
  `rig-builder-unavailable` / `animator-graph-invalid`.

### Hot paths

- `LightVolumeSampleCache` allocated a carrier object on every cache miss, the last per-call
  allocation in the plugin. The Postfix now recomputes the voxel key from the same argument through
  the same pure function, so nothing is carried across the call.
- `RemoteRagdollLod` read a static property behind a native alive check *before* its per-frame
  decision cache - useless on the cache-hit path, which is where the roughly twenty thousand calls a
  second land.
- `AnimatedMouthMaterial` read two material properties by string name every frame; the ids are
  resolved once at install.
- `RemoteItemInterpolationThreshold` paid three native calls before four managed checks that could
  reject the frame. Single-player and non-synced items now cost none of them.
- `DetailBodypartThrottle` computed the physics tick twice per detail part per step, and never checked
  `Time.fixedDeltaTime`: a zero step would freeze the tick and permanently skip some parts, the one
  direction in which a stuck tick is not safe.
- `PlayerNameUiWrites` resolved the same references two to four times each.
- `ItemScaleRedundantWrite` did three redundant destroyed-object checks per call.

### Arguments and assertions that were wrong

- `StatusEmitterScan` calls `ScreenVFX.StartFX()`, whose `0.5f` default is compiled into this DLL.
  Nothing asserted the overlay chain at all; all four members are asserted now, plus that the default
  is still `0.5f` - which no signature check can see.
- `RopeSegmentGuard` justified skipping a frame with "idempotent copy, recomputed next frame". That
  value is written once per grab and is constant for the climb, so the copy only ever matters on the
  first successful frame. The real argument is that the rotation it feeds is restored within the same
  physics step.
- `BoneWeightCap` claimed URP rewrites `antiAliasing` every frame; that write is in the pipeline
  constructor. Nothing in the game or URP writes `skinWeights` and nothing changes quality level, so
  the periodic reassert is kept only as insurance against writers that cannot be seen from here, and
  now says so. Its restore also cleared its bookkeeping after the write-back rather than in a
  `finally`, so a throwing restore left a stale snapshot that the next disable would write over the
  player's newer value.
- `RemoteRagdollLodAnimator` listed an accepted cosmetic cost - a stale water flag - that does not
  exist, because the replacement does write the parameter that flag reads.
- `RemoteRagdollLodPhysics` rejected re-bounding the friction-audit window on the grounds that it
  could not be shown whether a delegate call is seen by a Harmony detour. MonoMod's own startup
  selftest does exactly that and asserts the hook answered, so BepInEx could not start if it were
  not. The real reason is stronger: `Collider.sharedMaterial` has exactly four writers in the whole
  game and all four are inside the method being skipped, so in vanilla there is nothing for the
  missed audits to correct.
- `RemoteRagdollLod` claimed the cluster lane validates the fixed timestep for both lanes. It
  validates it for itself.
- `Acquire()` sat before two throwing statements while the comment asserted nothing below could
  throw.

### Subtraction

`DetailBodypartThrottle` kept its own copy of the eligibility list and now calls the shared one.
`RemoteRagdollLodAnimator` emitted two dynamic methods to reach Animator members that are public in
an assembly this project already references. Nine assertions on fields `ExplosionScale` neither reads
nor writes are dropped - a shape check cannot protect an argument that depends on *serialized*
values, which is what those arguments depend on. Plus: two config/report methods whose docstrings
claimed hot reload used them (it does not), an unused module-cleanup channel, three unused emit
helpers, and twelve unused using directives.

### Documentation

The README claimed 13 orbs per explosion in one paragraph and 10 in another, and that the patch only
cuts counts (it cuts counts and shrinks them). `Collision.contacts` is evaluated six times, not seven
or four - four places said three different numbers. `TumbleWeedTargetScan` is now a declared
exception to the default-on rule with its cost written out. Five section subtotals in the test guide
disagreed with their own tables, the results template omitted five items, and the README's
per-section directory labels were wrong three times out of five, because the sections are organised by
the kind of optimisation and the directories are not.

None of the above is verified in game. See TESTING.md; its acceptance order is unchanged.

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
