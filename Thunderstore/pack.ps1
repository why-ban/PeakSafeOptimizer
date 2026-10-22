<#
    Builds the Thunderstore package.

    Layout inside the zip, per the Thunderstore package format:
      manifest.json, icon.png, README.md at the root (required, names are case-sensitive)
      CHANGELOG.md, LICENSE at the root (optional)
      plugins\PeakSafeOptimizer.dll        (BepInEx plugin location)

    The zip must not contain a wrapping folder, which is why the files are staged and
    CreateFromDirectory is called with includeBaseDirectory = false.

    Usage:  pwsh -File Thunderstore\pack.ps1
#>

$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $PSScriptRoot
$thunderstore = $PSScriptRoot
$dll = Join-Path $projectRoot 'bin\Release\netstandard2.1\PeakSafeOptimizer.dll'

# Version comes from the manifest, so the zip name can never disagree with what is published.
$manifestPath = Join-Path $thunderstore 'manifest.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$version = $manifest.version_number
if (-not $version) { throw 'manifest.json has no version_number' }

Write-Host "Building Release..."
dotnet build (Join-Path $projectRoot 'PeakSafeOptimizer.csproj') -c Release | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
if (-not (Test-Path -LiteralPath $dll)) { throw "Missing build output: $dll" }

# The assembly version must match the manifest, otherwise the published page and the
# in-game plugin list disagree about which version is installed.
$dllVersion = (Get-Item -LiteralPath $dll).VersionInfo.FileVersion
if ($dllVersion -ne "$version.0") {
    throw "Version mismatch: manifest says $version, assembly says $dllVersion"
}

# Plugin.cs carries the version BepInEx prints in the plugin list; FileVersion cannot see it.
$pluginSource = Get-Content -LiteralPath (Join-Path $projectRoot 'Plugin.cs') -Raw
if ($pluginSource -notmatch 'PluginVersion\s*=\s*"([^"]+)"') {
    throw 'Plugin.cs has no PluginVersion literal'
}
if ($Matches[1] -ne $version) {
    throw "Version mismatch: manifest says $version, Plugin.PluginVersion says $($Matches[1])"
}

# Thunderstore rejects the upload rather than the pack, so the two format rules are checked here.
$iconPath = Join-Path $thunderstore 'icon.png'
$icon = [System.IO.File]::ReadAllBytes($iconPath)
if ($icon.Length -lt 24 -or $icon[0] -ne 0x89 -or $icon[1] -ne 0x50) {
    throw 'icon.png is not a PNG'
}
$iconW = ([int]$icon[16] -shl 24) -bor ([int]$icon[17] -shl 16) -bor ([int]$icon[18] -shl 8) -bor [int]$icon[19]
$iconH = ([int]$icon[20] -shl 24) -bor ([int]$icon[21] -shl 16) -bor ([int]$icon[22] -shl 8) -bor [int]$icon[23]
if ($iconW -ne 256 -or $iconH -ne 256) {
    throw "icon.png must be 256x256, is ${iconW}x${iconH}"
}
if ($manifest.description.Length -ge 250) {
    throw "manifest description is $($manifest.description.Length) chars, must be under 250"
}

$stage = Join-Path ([System.IO.Path]::GetTempPath()) 'PeakSafeOptimizer_pack'
if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path (Join-Path $stage 'plugins') | Out-Null

Copy-Item (Join-Path $thunderstore 'manifest.json') $stage
Copy-Item (Join-Path $thunderstore 'icon.png') $stage
Copy-Item (Join-Path $thunderstore 'README.md') $stage
Copy-Item (Join-Path $thunderstore 'CHANGELOG.md') $stage
Copy-Item (Join-Path $projectRoot 'LICENSE') $stage
Copy-Item $dll (Join-Path $stage 'plugins')

$buildDir = Join-Path $thunderstore 'build'
New-Item -ItemType Directory -Force -Path $buildDir | Out-Null
$zip = Join-Path $buildDir "PeakSafeOptimizer-$version.zip"
if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }

Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $stage, $zip, [System.IO.Compression.CompressionLevel]::Optimal, $false)
Remove-Item -LiteralPath $stage -Recurse -Force

Write-Host ""
Write-Host "Package: $zip"
Write-Host "SHA256 : $((Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash)"
Write-Host "Size   : $((Get-Item -LiteralPath $zip).Length) bytes"
Write-Host ""
Write-Host "Contents:"
$archive = [System.IO.Compression.ZipFile]::OpenRead($zip)
try { $archive.Entries | ForEach-Object { "  $($_.FullName)" } } finally { $archive.Dispose() }
