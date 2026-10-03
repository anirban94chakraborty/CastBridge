# Checks the installer end to end, without touching a real CastBridge installation: it installs into
# a scratch folder under %TEMP% with every optional step switched off, checks what appeared, then
# removes it again with the uninstaller it installed.
#
# The only things it writes outside that folder are CastBridge's own registry entries, which it puts
# there and takes away again, and the installer log. Settings and logs are kept unless -RemoveSettings.
param(
    [string]$Setup,
    [switch]$RemoveSettings,
    [switch]$KeepFolder
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

if (-not $Setup) {
    $Setup = (Get-ChildItem (Join-Path $root 'dist') -Filter 'CastBridge-*-Setup.exe' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName
}

if (-not $Setup -or -not (Test-Path $Setup)) {
    throw 'No setup executable found. Build one with tools\build-installer.ps1, or pass -Setup <path>.'
}

$target = Join-Path $env:TEMP 'CastBridge-InstallCheck'
$exe = Join-Path $target 'CastBridge.exe'
$uninstaller = Join-Path $target 'CastBridge-Uninstall.exe'
$payload = Join-Path $root 'dist\CastBridge.exe'
$settings = Join-Path $env:APPDATA 'CastBridge'
$desktopShortcut = Join-Path ([Environment]::GetFolderPath('Desktop')) 'CastBridge.lnk'
$startMenuShortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'CastBridge\CastBridge.lnk'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\CastBridge'
$recordKey = 'HKCU:\Software\CastBridge\Install'
$failures = 0

function Check([string]$name, [bool]$ok, [string]$detail = '') {
    $mark = if ($ok) { 'ok  ' } else { 'FAIL' }
    if (-not $ok) { $script:failures++ }
    Write-Host ("  {0}  {1}{2}" -f $mark, $name, $(if ($detail) { "  ($detail)" } else { '' }))
}

Write-Host "Setup under test: $Setup" -ForegroundColor Cyan
Write-Host "Installing silently into $target (no shortcuts, no start-up, no launch)..."

# Left over from an interrupted earlier run, so the checks below only ever see what this run made.
if (Test-Path $target) { Remove-Item $target -Recurse -Force }
foreach ($stale in @($recordKey, $uninstallKey)) {
    if ((Get-ItemProperty $stale -ErrorAction SilentlyContinue).InstallDir -eq $target) { Remove-Item $stale -Recurse -Force }
}

# The setup is a windowed program, so the shell's call operator would return before it had finished.
$runBefore = (Get-ItemProperty $runKey -Name CastBridge -ErrorAction SilentlyContinue).CastBridge
$install = Start-Process -FilePath $Setup -ArgumentList @('/S', ('"/D={0}"' -f $target), '/no-launch', '/no-startup', '/no-desktop', '/no-shortcuts') -Wait -PassThru
$installExit = $install.ExitCode

Write-Host ''
Write-Host 'After installing:'
Check 'setup exited 0' ($installExit -eq 0) "exit $installExit"
Check 'CastBridge.exe installed' (Test-Path $exe)
Check 'uninstaller installed' (Test-Path $uninstaller)
Check 'nothing else was copied' (@(Get-ChildItem $target -File -ErrorAction SilentlyContinue).Count -eq 2)

if ((Test-Path $exe) -and (Test-Path $payload)) {
    $installed = (Get-FileHash $exe -Algorithm SHA256).Hash
    $original = (Get-FileHash $payload -Algorithm SHA256).Hash
    Check 'installed exe is byte for byte the published one' ($installed -eq $original)
}

Check 'install location recorded' ((Get-ItemProperty $recordKey -ErrorAction SilentlyContinue).InstallDir -eq $target)
$arp = Get-ItemProperty $uninstallKey -ErrorAction SilentlyContinue
Check 'Apps & features entry written' ($null -ne $arp) $(if ($arp) { $arp.DisplayName + ' ' + $arp.DisplayVersion })
Check 'uninstall command points at the installed stub' ($arp -and $arp.UninstallString -eq ('"' + $uninstaller + '"'))
$runAfterInstall = (Get-ItemProperty $runKey -Name CastBridge -ErrorAction SilentlyContinue).CastBridge
Check 'logon entry left exactly as it was' ($runAfterInstall -eq $runBefore) $(if ($runBefore) { 'pointed somewhere else already' } else { 'there was none' })
Check 'start-up marker written, so the app will not add it back' (Test-Path (Join-Path $settings 'startup-default-applied'))
Check 'no shortcuts created' (-not (Test-Path $startMenuShortcut))

Write-Host ''
Write-Host 'Uninstalling with the stub that was just installed:'
$uninstallArguments = @('/silent')
if (-not $RemoveSettings) { $uninstallArguments += '--keep-settings' }
$uninstall = Start-Process -FilePath $uninstaller -ArgumentList $uninstallArguments -Wait -PassThru
$uninstallExit = $uninstall.ExitCode
Start-Sleep -Seconds 3

Check 'uninstaller exited 0' ($uninstallExit -eq 0) "exit $uninstallExit"
Check 'install folder gone' (-not (Test-Path $target)) $(if (Test-Path $target) { (Get-ChildItem $target -File).Name -join ', ' } else { '' })
Check 'logon entry still untouched' ((Get-ItemProperty $runKey -Name CastBridge -ErrorAction SilentlyContinue).CastBridge -eq $runBefore)
Check 'Apps & features entry gone' (-not (Test-Path $uninstallKey))
Check 'install record gone' (-not (Test-Path $recordKey))
Check 'shortcut of the portable build left alone' (-not (Test-Path $startMenuShortcut))
Check 'helper copy in %TEMP% deleted itself' (@(Get-ChildItem $env:TEMP -Filter 'CastBridge-Uninstall-*.exe' -ErrorAction SilentlyContinue).Count -eq 0)
Check 'settings handled as asked' (-not $RemoveSettings -or -not (Test-Path $settings)) $(if (-not $RemoveSettings) { 'kept' } else { 'deleted' })

if ($KeepFolder -and (Test-Path $target)) {
    Write-Host "  kept $target" -ForegroundColor DarkGray
}
elseif (Test-Path $target) {
    Remove-Item $target -Recurse -Force
}

Write-Host ''
Write-Host "Installer log: $(Join-Path $settings 'installer.log')"
if ($failures -gt 0) {
    Write-Host "$failures check(s) failed." -ForegroundColor Red
    exit 1
}

Write-Host 'Every check passed.' -ForegroundColor Green
