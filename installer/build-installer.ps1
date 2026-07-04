# Copyright (c) 2026 LumaCoreTech
# SPDX-License-Identifier: MIT
# Project: https://github.com/LumaCoreTech/OllamaProxy

<#
.SYNOPSIS
    Builds the OllamaProxy Windows MSI end to end: publishes the self-contained app, then packages it.

.DESCRIPTION
    The installer is a deliberate two-step pipeline, and both the order and the publish *profile* matter:

      1. `dotnet publish -p:PublishProfile=win-x64` produces the self-contained payload the MSI harvests.
      2. `dotnet build` on the WiX project packages that payload into OllamaProxy.msi.

    The win-x64 profile (src\OllamaProxy\Properties\PublishProfiles\win-x64.pubxml) is what redirects the
    publish output to artifacts\publish\win-x64\ - exactly where the installer looks for it. Publishing
    WITHOUT the profile drops the output into the artifacts *bin* layout instead, so the installer finds
    no payload and you get a silent "no MSI". This script encodes that knowledge so nobody has to remember
    it.

    With UseArtifactsOutput enabled (src\Directory.Build.props) all build and publish output is redirected
    to the artifacts folder, so the project folder stays source-only and no stale files can collide with
    the next publish.

.PARAMETER Version
    Value stamped into the MSI ProductVersion (only the first three fields are honored, e.g. 1.2.3). When
    omitted, the WiX project's own default applies, so this script never duplicates that default. The MSI is
    always relinked (see the build step) so the emitted package reliably carries the requested version
    rather than a stale incremental one.

.PARAMETER Configuration
    MSBuild configuration for both the publish and the MSI build. Defaults to Release.

.PARAMETER Clean
    Also delete artifacts\installer before building. The MSI itself is relinked on every run regardless, so
    this is rarely needed; reach for it only to force a from-scratch rebuild of the custom-action assembly
    too, or when a previous run left the output tree in a weird state.

.EXAMPLE
    .\installer\build-installer.ps1
    Builds the MSI with the WiX project's default version.

.EXAMPLE
    .\installer\build-installer.ps1 -Version 1.2.3
    Builds and stamps ProductVersion 1.2.3.

.EXAMPLE
    .\installer\build-installer.ps1 -Version 1.2.3 -Clean
    Wipes prior installer artifacts, then builds a fresh, version-stamped MSI.
#>
[CmdletBinding()]
param(
    [string]$Version,

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [switch]$Clean
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# The MSI is win-x64 only (see OllamaProxy.Installer.wixproj). Platform is fixed rather than a parameter
# because the publish profile, the harvest glob, and the WiX Package element all assume x64.
$Platform = 'x64'

# Resolve every path from the script's own location, so it works regardless of the caller's working dir.
$RepoRoot   = Split-Path -Parent $PSScriptRoot
$AppProject = Join-Path $RepoRoot 'src\OllamaProxy\OllamaProxy.csproj'
$WixProject = Join-Path $RepoRoot 'installer\OllamaProxy.Installer.wixproj'
$PublishDir = Join-Path $RepoRoot 'artifacts\publish\win-x64'
$MsiPath    = Join-Path $RepoRoot "artifacts\installer\OllamaProxy.Installer\bin\$Platform\$Configuration\OllamaProxy.msi"

# Native commands don't throw on non-zero exit even under $ErrorActionPreference='Stop'; check explicitly.
function Assert-LastExitCode {
    param([Parameter(Mandatory)][string]$What)
    if ($LASTEXITCODE -ne 0) {
        throw "$What failed with exit code $LASTEXITCODE."
    }
}

function Write-Step {
    param([Parameter(Mandatory)][string]$Message)
    Write-Host ''
    Write-Host "==> $Message" -ForegroundColor Cyan
}

Push-Location $RepoRoot
try {
    Write-Host 'OllamaProxy installer build' -ForegroundColor Green
    Write-Host "  repo:          $RepoRoot"
    Write-Host "  configuration: $Configuration"
    Write-Host "  platform:      $Platform"
    if ($Version) { Write-Host "  version:       $Version" }

    if ($Clean) {
        Write-Step 'Removing previous installer artifacts (-Clean)'
        $installerArtifacts = Join-Path $RepoRoot 'artifacts\installer'
        if (Test-Path $installerArtifacts) {
            Remove-Item $installerArtifacts -Recurse -Force
            Write-Host '  removed artifacts\installer\'
        }
    }

    Write-Step 'Publishing self-contained win-x64 app'
    # The profile is mandatory: it sets SelfContained + RID and redirects PublishDir to the harvest folder.
    dotnet publish $AppProject -c $Configuration -p:PublishProfile=win-x64
    Assert-LastExitCode 'dotnet publish'

    if (-not (Test-Path (Join-Path $PublishDir 'OllamaProxy.exe'))) {
        throw "Publish reported success but $PublishDir\OllamaProxy.exe is missing - " +
              'the installer would have nothing to harvest.'
    }

    Write-Step 'Building the MSI'
    # Always force a full link of the MSI (-t:Rebuild). The WiX linker's incremental up-to-date check does
    # NOT track two of its most important inputs: the preprocessor variables (ProductVersion flows in via
    # DefineConstants -> $(var.ProductVersion)) and the freshly harvested publish payload. An incremental
    # build can therefore emit a previously linked MSI that no longer matches the current version or files —
    # a stale ProductVersion, for instance, installs side-by-side instead of upgrading (no MajorUpgrade
    # match). Relinking unconditionally makes "just run the script" correct by default; the extra cost is
    # small because the publish step above already runs every time and dominates the runtime.
    $buildArgs = @($WixProject, '-c', $Configuration, "-p:Platform=$Platform", '-t:Rebuild')
    if ($Version) { $buildArgs += "-p:ProductVersion=$Version" }
    dotnet build @buildArgs
    Assert-LastExitCode 'dotnet build (installer)'

    if (-not (Test-Path $MsiPath)) {
        throw "Build reported success but the MSI is missing at $MsiPath."
    }

    $sizeMb = [math]::Round((Get-Item $MsiPath).Length / 1MB, 1)
    Write-Host ''
    Write-Host 'Build succeeded.' -ForegroundColor Green
    Write-Host "  MSI:  $MsiPath"
    Write-Host "  Size: $sizeMb MB"
}
finally {
    Pop-Location
}
