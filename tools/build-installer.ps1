# Publishes Altlane (framework-dependent, win-x64) to dist\publish and builds
# dist\Altlane-Setup-<version>.exe with Inno Setup 6.
#
# Code signing is optional and off unless one of these is set:
#   ALTLANE_SIGN_CERT   thumbprint of a code-signing certificate in your certificate store
#   ALTLANE_SIGN_ARGS   signtool arguments that pick the certificate some other way, used instead of
#                       ALTLANE_SIGN_CERT; for example "/f C:\path\cert.pfx /p password", or Azure
#                       Trusted Signing's "/dlib <path>\Azure.CodeSigning.Dlib.dll /dmdf <path>\metadata.json"
#   ALTLANE_TIMESTAMP   RFC 3161 timestamp server (default http://timestamp.digicert.com)
# Signing covers Altlane.exe and Altlane.dll before they're packed, then the setup and its uninstaller.
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

$signArgs = if ($env:ALTLANE_SIGN_ARGS) { $env:ALTLANE_SIGN_ARGS } elseif ($env:ALTLANE_SIGN_CERT) { "/sha1 $($env:ALTLANE_SIGN_CERT -replace '\s', '')" }
if ($signArgs) {
    $signtool = @(
        (Get-Command signtool.exe -ErrorAction SilentlyContinue).Source
        Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue |
            Sort-Object { [version]$_.Directory.Parent.Name } -Descending | ForEach-Object FullName
    ) | Where-Object { $_ } | Select-Object -First 1
    if (-not $signtool) { throw 'signtool.exe not found. It comes with the Windows SDK.' }
    $timestamp = if ($env:ALTLANE_TIMESTAMP) { $env:ALTLANE_TIMESTAMP } else { 'http://timestamp.digicert.com' }
    $signCmd = "sign /fd sha256 /tr $timestamp /td sha256 $signArgs"
    "Signing with $signtool"
}

# A clean folder, so a file removed from the app can't linger in the installer.
if (Test-Path $publish) { Remove-Item -Recurse -Force $publish }
dotnet publish (Join-Path $root 'src\Altlane\Altlane.csproj') -c Release -r win-x64 --self-contained false -o $publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

if ($signArgs) {
    $files = @('Altlane.exe', 'Altlane.dll') | ForEach-Object { Join-Path $publish $_ }
    # Split like a command line, so a quoted path with spaces stays one argument.
    $parts = [regex]::Matches($signCmd, '"[^"]*"|\S+') | ForEach-Object { $_.Value.Trim('"') }
    & $signtool $parts $files
    if ($LASTEXITCODE -ne 0) { throw "signtool failed ($LASTEXITCODE)" }
    # Inno signs the setup and the uninstaller it writes, through the "altlane" sign tool named here.
    # Inno writes quotes as $q; a literal one wouldn't survive Windows PowerShell's argument passing.
    & $iscc '/DSign' "/Saltlane=`$q$signtool`$q $($signCmd.Replace('"', '$q')) `$f" (Join-Path $root 'installer\Altlane.iss')
}
else {
    & $iscc (Join-Path $root 'installer\Altlane.iss')
}
if ($LASTEXITCODE -ne 0) { throw "ISCC failed ($LASTEXITCODE)" }
$setup = Get-ChildItem (Join-Path $root 'dist\Altlane-Setup-*.exe') | Sort-Object LastWriteTime | Select-Object -Last 1
if ($signArgs) {
    & $signtool verify /pa /q $setup.FullName
    "Signature check: $(if ($LASTEXITCODE -eq 0) { 'trusted' } else { 'NOT trusted on this PC (a test certificate, or a missing chain)' })"
}
$setup | Select-Object FullName, Length
