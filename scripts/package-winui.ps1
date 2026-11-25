# SPDX-License-Identifier: GPL-3.0-or-later
<#
.SYNOPSIS
  Builds the NectarLink WinUI 3 app and packs it into an NSIS Windows installer.

.DESCRIPTION
  1. Generates WinUI icon assets (`cargo xtask icons`) and builds native Rust
     libraries + agent binary (unless -SkipBuild).
  2. Publishes the self-contained WinUI 3 app into `target\package\Nectarlink`.
  3. Copies license/changelog files and Visual C++ runtime DLLs if present.
  4. Packs the staged folder into `target\package\Nectarlink-<version>-<arch>-setup.exe`
     using NSIS (`makensis`), unless -StageOnly is specified.

.EXAMPLE
  .\scripts\package-winui.ps1
  .\scripts\package-winui.ps1 -StageOnly
  .\scripts\package-winui.ps1 -Version 0.1.5-canary.12 -AssemblyVersion 0.1.5.12
#>
param(
    [ValidateSet('x64', 'arm64')]
    [string]$Arch = 'x64',
    [string]$Version,
    [string]$AssemblyVersion,
    [string]$OutDir,
    [switch]$SkipBuild,
    [switch]$StageOnly
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

function Step($name) { Write-Host "`n==> $name" -ForegroundColor Cyan }

function Invoke-Native([string]$what, [scriptblock]$command) {
    $saved = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & $command 2>&1 | ForEach-Object { Write-Host "$_" } }
    finally { $ErrorActionPreference = $saved }
    if ($LASTEXITCODE -ne 0) { throw "$what failed (exit code $LASTEXITCODE)" }
}

# ---- Version ----
$cargoVersion = (Select-String -Path Cargo.toml -Pattern '(?m)^version\s*=\s*"([^"]+)"' | Select-Object -First 1).Matches[0].Groups[1].Value
if (-not $cargoVersion) { throw "Cannot read version from Cargo.toml" }
if (-not $Version) { $Version = $cargoVersion }
$numeric = ($Version -split '[-+]')[0]

$build = if ($Version -match '-(?:nightly|canary)\.(\d+)(\+canary\.[0-9a-f]+)?$') {
    [Math]::Min([int]$Matches[1], 65534)
} elseif ($Version -notmatch '-') {
    999
} else {
    0
}

if (-not $AssemblyVersion) {
    $AssemblyVersion = if ($build -gt 0 -and $build -ne 999) { "$numeric.$build" } else { "$numeric.0" }
}

$isArm64 = $Arch -eq 'arm64'
$rustTarget = if ($isArm64) { 'aarch64-pc-windows-msvc' } else { $null }
$rustTargetDir = if ($isArm64) {
    Join-Path $root 'target\aarch64-pc-windows-msvc\release'
} else {
    Join-Path $root 'target\release'
}
$rid = if ($isArm64) { 'win-arm64' } else { 'win-x64' }
$dotnetPlatform = if ($isArm64) { 'ARM64' } else { 'x64' }

# ---- Build native Rust & WinUI assets ----
if (-not $SkipBuild) {
    Step "Icons"
    Invoke-Native "cargo xtask icons" { cargo xtask icons }

    Step "Build native Rust binaries ($Arch)"
    if ($isArm64) {
        Invoke-Native "cargo build" {
            cargo build --locked --release --target $rustTarget -p nectarlink-winui-core -p nectarlink-vcam -p nectarlink-credprov -p nectarlink-mcp
        }
    } else {
        Invoke-Native "cargo build" {
            cargo build --locked --release -p nectarlink-winui-core -p nectarlink-vcam -p nectarlink-credprov -p nectarlink-mcp
        }
    }
}

$coreDll = Join-Path $rustTargetDir 'nectarlink_winui_core.dll'
if (-not (Test-Path $coreDll)) { throw "Missing native core DLL at $coreDll" }
$vcamDll = Join-Path $rustTargetDir 'nectarlink_vcam.dll'
$credprovDll = Join-Path $rustTargetDir 'nectarlink_credprov.dll'
$agentExe = Join-Path $rustTargetDir 'nectarlink-agent.exe'

# ---- Stage WinUI self-contained publish ----
Step "Stage WinUI ($Arch)"
$packageDir = if ($OutDir) { $OutDir } else { Join-Path $root 'target\package' }
New-Item -ItemType Directory -Force $packageDir | Out-Null
$stage = Join-Path $packageDir 'Nectarlink'
if (Test-Path $stage) {
    $stagedVcam = Join-Path $stage 'nectarlink_vcam.dll'
    if (Test-Path $stagedVcam) {
        & regsvr32.exe /s /u $stagedVcam 2>$null | Out-Null
        try {
            Remove-Item $stagedVcam -Force -ErrorAction Stop
        } catch {
            $trashDir = Join-Path $root 'target\package-locked'
            New-Item -ItemType Directory -Force $trashDir | Out-Null
            Move-Item $stagedVcam (Join-Path $trashDir "nectarlink_vcam.$([DateTime]::UtcNow.Ticks).dll") -Force -ErrorAction SilentlyContinue
        }
    }
    Remove-Item $stage -Recurse -Force
}

Invoke-Native "dotnet publish" {
    dotnet publish (Join-Path $root 'winui\NectarLink.WinUI\NectarLink.WinUI.csproj') `
        -c Release `
        -p:Platform=$dotnetPlatform `
        -r $rid `
        --self-contained true `
        -p:Version=$AssemblyVersion `
        -p:InformationalVersion=$Version `
        -o $stage
}

# Ensure native DLLs & optional agent exe are present in the stage folder
Copy-Item -Force $coreDll $stage
if (Test-Path $vcamDll) { Copy-Item -Force $vcamDll $stage }
if (Test-Path $credprovDll) { Copy-Item -Force $credprovDll $stage }
if (Test-Path $agentExe) { Copy-Item -Force $agentExe $stage }

Copy-Item -Force (Join-Path $root 'LICENSE') (Join-Path $stage 'LICENSE.txt')
Copy-Item -Force (Join-Path $root 'CHANGELOG.md') (Join-Path $stage 'CHANGELOG.md')

# Sync Sparse Package manifest version & arch
$manifestPath = Join-Path $stage 'AppxManifest.xml'
if (Test-Path $manifestPath) {
    $manifest = Get-Content $manifestPath -Raw
    $manifest = $manifest -replace '(<Identity\b[^>]*?\s)Version="[^"]*"', "`${1}Version=`"$numeric.$build`""
    $manifest = $manifest -replace 'ProcessorArchitecture="[^"]*"', "ProcessorArchitecture=`"$Arch`""
    Set-Content -Path $manifestPath -Value $manifest -Encoding UTF8 -NoNewline
}

# Ship app-local MSVC runtime DLLs when Visual Studio Build Tools are available
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$vs = if (Test-Path $vswhere) {
    $component = if ($isArm64) { 'Microsoft.VisualStudio.Component.VC.Tools.ARM64' } else { 'Microsoft.VisualStudio.Component.VC.Tools.x86.x64' }
    & $vswhere -latest -products * -requires $component -property installationPath
} else { $null }
$crtArch = if ($isArm64) { 'arm64' } else { 'x64' }
$redistDirs = @()
if ($vs) { $redistDirs += (Join-Path $vs 'VC\Redist\MSVC') }
if ($env:VCToolsRedistDir -and (Test-Path $env:VCToolsRedistDir)) { $redistDirs += $env:VCToolsRedistDir }
$crt = $null
foreach ($dir in $redistDirs) {
    if (Test-Path $dir) {
        $crt = Get-ChildItem $dir -Directory -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -match '^\d' } | Sort-Object { [version]$_.Name } -Descending |
            ForEach-Object { Get-ChildItem (Join-Path $_.FullName $crtArch) -Directory -Filter 'Microsoft.VC*.CRT' -ErrorAction SilentlyContinue } |
            Select-Object -First 1
        if (-not $crt) {
            $crt = Get-ChildItem (Join-Path $dir $crtArch) -Directory -Filter 'Microsoft.VC*.CRT' -ErrorAction SilentlyContinue | Select-Object -First 1
        }
        if ($crt) { break }
    }
}
if ($crt) {
    Copy-Item (Join-Path $crt.FullName '*.dll') $stage -Force
} else {
    Write-Warning "Visual C++ CRT ($crtArch) not found; skipping app-local CRT DLL copy"
}

$stagedSizeMb = (Get-ChildItem $stage -Recurse -File | Measure-Object Length -Sum).Sum / 1MB
Write-Host ("Staged {0:N1} MB in {1}" -f $stagedSizeMb, $stage)
if ($StageOnly) { return }

# ---- Installer ----
Step "Installer ($Arch)"
$makensis = (Get-Command makensis -ErrorAction SilentlyContinue).Source
if (-not $makensis) { $makensis = Join-Path ${env:ProgramFiles(x86)} 'NSIS\makensis.exe' }
if (-not (Test-Path $makensis)) {
    throw "NSIS (makensis.exe) not found. Install NSIS or pass -StageOnly."
}
$iconPath = Join-Path $root 'winui\NectarLink.WinUI\Assets\nectarlink.ico'
if (-not (Test-Path $iconPath)) { throw "Missing app icon at $iconPath" }
$licensePath = Join-Path $stage 'LICENSE.txt'
$outExe = Join-Path $packageDir "Nectarlink-$Version-$Arch-setup.exe"

Invoke-Native "makensis" {
    & $makensis /V2 `
        "/DVERSION=$numeric" `
        "/DBUILD=$build" `
        "/DDISPLAY_VERSION=$Version" `
        "/DARCH=$Arch" `
        "/DSTAGE=$stage" `
        "/DICON=$iconPath" `
        "/DLICENSE_FILE=$licensePath" `
        "/DOUTFILE=$outExe" `
        (Join-Path $root 'native\installer\nectarlink.nsi')
}
Write-Host ("Installer: {0} ({1:N1} MB)" -f $outExe, ((Get-Item $outExe).Length / 1MB))
