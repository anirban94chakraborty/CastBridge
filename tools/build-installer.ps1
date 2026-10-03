# Builds CastBridge-<version>-Setup.exe in dist: the single file to copy to another machine.
#
# The setup executable embeds the published CastBridge.exe and the uninstaller stub, so nothing else
# has to travel with it. Needs no installer toolchain: it is an ordinary .NET Framework program built
# by the same SDK as everything else.
param(
    [switch]$SkipAppPublish
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root 'dist'
$build = Join-Path $root 'build'
$payload = Join-Path $dist 'CastBridge.exe'
$uninstallerProject = Join-Path $root 'tools\CastBridge.Uninstaller\CastBridge.Uninstaller.csproj'
$setupProject = Join-Path $root 'tools\CastBridge.Installer\CastBridge.Installer.csproj'
$uninstallerOut = Join-Path $build 'uninstaller'
$setupOut = Join-Path $build 'installer'

if (-not $SkipAppPublish) {
    # No Desktop shortcut here: an installed copy makes its own, pointing at the installed
    # executable rather than at this build output.
    & (Join-Path $PSScriptRoot 'publish.ps1') -NoShortcut
}

if (-not (Test-Path $payload)) { throw "CastBridge.exe not found in $dist. Run tools\publish.ps1 first." }

foreach ($folder in @($uninstallerOut, $setupOut)) {
    if (Test-Path $folder) { Remove-Item $folder -Recurse -Force }
}

Write-Host 'Building the uninstaller stub...'
dotnet publish $uninstallerProject -c Release -o $uninstallerOut --nologo
if ($LASTEXITCODE -ne 0) { throw "Publishing the uninstaller failed with exit code $LASTEXITCODE" }

$uninstallerExe = Join-Path $uninstallerOut 'CastBridge-Uninstall.exe'
if (-not (Test-Path $uninstallerExe)) { throw "The uninstaller stub was not produced in $uninstallerOut" }

Write-Host 'Building CastBridge-Setup.exe (it embeds CastBridge.exe and the uninstaller)...'
dotnet publish $setupProject -c Release -o $setupOut --nologo -p:PayloadPath="$payload" -p:UninstallerPath="$uninstallerExe"
if ($LASTEXITCODE -ne 0) { throw "Publishing the setup executable failed with exit code $LASTEXITCODE" }

$setup = Join-Path $setupOut 'CastBridge-Setup.exe'
if (-not (Test-Path $setup)) { throw "The setup executable was not produced in $setupOut" }

$version = (Get-Item $payload).VersionInfo.ProductVersion
$artifact = Join-Path $dist "CastBridge-$version-Setup.exe"

# Only the executable is needed: .NET Framework 4.8 is already on the machine, and the payload and
# the uninstaller are inside it.
Move-Item $setup $artifact -Force

$sizeMb = [math]::Round((Get-Item $artifact).Length / 1MB, 1)
Write-Host ""
Write-Host "Built $artifact ($sizeMb MB)" -ForegroundColor Green
Write-Host "  installs for the current user in %LOCALAPPDATA%\Programs\CastBridge, no administrator rights"
Write-Host "  Start menu and desktop shortcuts, an uninstaller, and Apps & features entry"
Write-Host ""
Write-Host 'Try it without touching this machine:' -ForegroundColor Cyan
Write-Host "  `"$artifact`" /S /D=`"$env:TEMP\CastBridgeTest`" /no-launch /no-startup /no-desktop /no-shortcuts"
Write-Host "  `"$env:TEMP\CastBridgeTest\CastBridge-Uninstall.exe`" /silent"
Write-Host ""
Write-Host "SHA256: $((Get-FileHash $artifact -Algorithm SHA256).Hash)"
