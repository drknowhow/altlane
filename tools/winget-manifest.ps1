# Writes the winget manifests for a built installer to dist\winget\<version>, in the folder layout
# microsoft/winget-pkgs uses, and checks them with "winget validate". Nothing is submitted.
#
#   tools\winget-manifest.ps1                 the newest dist\Altlane-Setup-*.exe
#   tools\winget-manifest.ps1 -Version 0.4.0  a given one
#
# The hash is of the local file, so build (and sign) the installer you'll upload first: the release
# asset has to be byte for byte the same file, at the URL below.
param([string]$Version, [string]$ReleaseDate = (Get-Date -Format 'yyyy-MM-dd'))
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

$setup = if ($Version) { Get-Item (Join-Path $root "dist\Altlane-Setup-$Version.exe") }
         else { Get-ChildItem (Join-Path $root 'dist\Altlane-Setup-*.exe') | Sort-Object LastWriteTime | Select-Object -Last 1 }
if (-not $setup) { throw 'No dist\Altlane-Setup-*.exe. Run tools\build-installer.ps1 first.' }
$Version = $setup.BaseName -replace '^Altlane-Setup-', ''
$sha = (Get-FileHash $setup.FullName -Algorithm SHA256).Hash

$id = 'DrKnowHow.Altlane'
$schema = '1.10.0'
# Inno Setup's uninstall key: the AppId in installer\Altlane.iss plus "_is1".
$productCode = '{D4EA3F34-860B-45EF-90D6-AA46FEC2CD36}_is1'
$header = "# Written by tools\winget-manifest.ps1 from $($setup.Name)"
$out = Join-Path $root "dist\winget\manifests\d\DrKnowHow\Altlane\$Version"
New-Item -ItemType Directory -Force $out | Out-Null

@"
$header
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.version.$schema.schema.json
PackageIdentifier: $id
PackageVersion: $Version
DefaultLocale: en-US
ManifestType: version
ManifestVersion: $schema
"@ | Set-Content -Encoding utf8 (Join-Path $out "$id.yaml")

@"
$header
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.installer.$schema.schema.json
PackageIdentifier: $id
PackageVersion: $Version
InstallerType: inno
Scope: user
InstallModes:
- interactive
- silent
- silentWithProgress
UpgradeBehavior: install
Dependencies:
  PackageDependencies:
  - PackageIdentifier: Microsoft.DotNet.DesktopRuntime.10
ReleaseDate: $ReleaseDate
Installers:
- Architecture: x64
  InstallerUrl: https://github.com/drknowhow/altlane/releases/download/v$Version/$($setup.Name)
  InstallerSha256: $sha
  ProductCode: '$productCode'
ManifestType: installer
ManifestVersion: $schema
"@ | Set-Content -Encoding utf8 (Join-Path $out "$id.installer.yaml")

@"
$header
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.defaultLocale.$schema.schema.json
PackageIdentifier: $id
PackageVersion: $Version
PackageLocale: en-US
Publisher: Dr. Know How
PublisherUrl: https://github.com/drknowhow
PublisherSupportUrl: https://github.com/drknowhow/altlane/issues
PackageName: Altlane
PackageUrl: https://github.com/drknowhow/altlane
License: MIT
LicenseUrl: https://github.com/drknowhow/altlane/blob/main/LICENSE
ShortDescription: Alt+Tab and Win+Tab for Windows 11, grouped by the kind of window.
Description: |-
  Altlane replaces Alt+Tab and Win+Tab with views grouped by the kind of window. Your three most recent
  windows sit in a Recent row and the rest go into seven numbered groups in a fixed order: Agents,
  Terminals, Web, Creative, Files, Docs & notes, Other. A quick Alt+Tab still flips straight back to your
  last window. Win+Tab opens a full-screen overview with one lane per group and a search box.
Tags:
- alt-tab
- task-switcher
- window-switcher
- windows-11
- productivity
ReleaseNotesUrl: https://github.com/drknowhow/altlane/releases/tag/v$Version
ManifestType: defaultLocale
ManifestVersion: $schema
"@ | Set-Content -Encoding utf8 (Join-Path $out "$id.locale.en-US.yaml")

winget validate --manifest $out
if ($LASTEXITCODE -ne 0) { throw "winget validate failed ($LASTEXITCODE)" }
"Manifests in $out (sha256 $sha)"
