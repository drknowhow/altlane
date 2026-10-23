# Publishes Altlane (framework-dependent, win-x64) to dist\publish and builds
# dist\Altlane-Setup-<version>.exe with Inno Setup 6.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$publish = Join-Path $root 'dist\publish'

$iscc = @(
    (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source,
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 not found. Install it with: winget install JRSoftware.InnoSetup' }

# A clean folder, so a file removed from the app can't linger in the installer.
if (Test-Path $publish) { Remove-Item -Recurse -Force $publish }
dotnet publish (Join-Path $root 'src\Altlane\Altlane.csproj') -c Release -r win-x64 --self-contained false -o $publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

& $iscc (Join-Path $root 'installer\Altlane.iss')
if ($LASTEXITCODE -ne 0) { throw "ISCC failed ($LASTEXITCODE)" }
Get-ChildItem (Join-Path $root 'dist\Altlane-Setup-*.exe') | Sort-Object LastWriteTime | Select-Object -Last 1 FullName, Length
