# PeakSafeOptimizer

A performance mod for **PEAK** (Unity 6, BepInEx 5) aimed at the case that actually hurts: a full
lobby standing in one place. 36 Harmony patches, all on by default, each one either an
equivalence-proven rewrite of a hot vanilla method or a guard for a vanilla fault that throws an
exception every frame.

- Thunderstore: [current page](https://thunderstore.io/c/peak/p/khalil2/) · [old page, frozen at 1.4.1](https://thunderstore.io/c/peak/p/khalil/PeakSafeOptimizer/)
- Release line: 1.5.1, audited against PEAK 3.0.0
- License: MIT

> **This mod has moved to a new Thunderstore account.** The account that owns the old page is bound to a
> GitHub account that has been banned, and Thunderstore sign-in goes through GitHub, so that account can
> no longer be logged into and the old page can no longer be updated. Publishing continues under
> [khalil2](https://thunderstore.io/c/peak/p/khalil2/), which is where this release lives.

## How it works

- **Prefixes and postfixes only.** There is no transpiler in this repository. Four attempts failed in
  this heavily modded environment - IL compile errors, stack imbalance, zero pattern matches - and none
  shipped.
- **Fail closed.** Every patch asserts the exact shape of every game member it reads before it installs.
  A shape that does not match keeps that one patch out (`SkippedStructureMismatch`) and leaves the other
  35 untouched. There is deliberately no build-level gate: an earlier version skipped almost the whole
  mod on any game update before a single patch got to check what it actually reads.
- **No numbers to tune.** Every threshold is a named constant next to the argument for it. Configuration
  is one boolean per patch, or one per patch group where several patches are only correct together.
- **Two engine-level lanes** (`Physics.reuseCollisionCallbacks`, `QualitySettings.skinWeights`) snapshot
  the value they find and restore it on toggle and on shutdown, and only while it is still the value
  they wrote.

## Install

[Thunderstore](https://thunderstore.io/c/peak/p/khalil2/) via a mod manager, or drop
`PeakSafeOptimizer.dll` into `PEAK/BepInEx/plugins/`.

## Build from source

Requires a .NET SDK (8 or newer) and a PEAK install with BepInEx 5 in it. The project references the
game's assemblies through `$(GameRoot)`, which defaults to `D:\SteamLibrary\steamapps\common\PEAK`.
Override it without editing the project file:

```powershell
dotnet build -c Release -p:GameRoot="C:\Program Files (x86)\Steam\steamapps\common\PEAK"
```

Package the Thunderstore zip:

```powershell
pwsh -File Thunderstore\pack.ps1
```

`pack.ps1` builds first, then fails loudly if `manifest.json`'s `version_number` does not match the
assembly's `FileVersion`, so the published page and the in-game plugin list can never disagree.

## Layout

| Path | What lives there |
|---|---|
| `Patches\Stability\` | guards for vanilla faults: per-frame exceptions, null singletons, missing prefab nodes |
| `Patches\Equivalent\MathAndScan\` | exact-equivalence rewrites of scan and math loops |
| `Patches\Scale\` | redundant native-call and allocation removal |
| `Patches\ScaleExperimental\` | the distance and tick-rate tiers, plus the explosion count cap |
| `Infrastructure\` | patch catalogue, installer, fail-closed validation, startup report |
| `Config\` | one `PatchId` per patch and its config binding |
| `Graphics\` | the two non-Harmony engine lanes |
| `Diagnostics\` | rate-limited repair accounting |
| `Thunderstore\` | manifest, icon, user-facing README and CHANGELOG, `pack.ps1` |

## Verifying a build

There is no test project. The startup log is the contract: filter `BepInEx/LogOutput.log` for
`PeakSafeOptimizer`. A healthy default install prints

```
compatibility mode: certified
totals: registered=36, enabled=36, applied=36, skipped=0, failed=0, module-failures=0
```

`applied` should equal `enabled`, and `failed=0` with `module-failures=0` means no patch hit a problem.
`compatibility mode: structural-validation` instead of `certified` only means the running game build is
not the one this release was last audited against; every patch still validates on its own merits, and
the log names the game assembly's MVID so you can tell whether anybody has re-read that build.

## Auditing a game update

Decompile the new build next to the old one and hash-diff the two trees. An unchanged assembly
decompiles to byte-identical text, so the diff names exactly which types changed:

```powershell
$a = 'old-decompile'; $b = 'new-decompile'
$fa = Get-ChildItem $a -Recurse -Filter *.cs | ForEach-Object { @{ rel = $_.FullName.Substring($a.Length + 1); h = (Get-FileHash $_.FullName).Hash } }
$fb = Get-ChildItem $b -Recurse -Filter *.cs | ForEach-Object { @{ rel = $_.FullName.Substring($b.Length + 1); h = (Get-FileHash $_.FullName).Hash } }
$aMap = @{}; $fa | ForEach-Object { $aMap[$_.rel] = $_.h }
$fb | Where-Object { $aMap[$_.rel] -ne $_.h } | Select-Object -ExpandProperty rel
```

Then check whether any of the 36 patch targets is in that list. If none is, the update needs no
adaptation; if one is, the patch whose target changed is the one to re-read.

## Contributing

Bug reports with a full `LogOutput.log` are the most useful thing you can send. Patch behaviour is
documented in the patch source itself, not in a separate document: each file opens with the argument for
why it is equivalent, and that argument is what a change has to keep true.

## License

MIT, see [LICENSE](LICENSE).
