# Reports whether Windows recorded a crash or a runtime failure for CastBridge, which is the only
# way to see a hard process death that never reached the app's own exception handler.
param([int]$Minutes = 60)

$events = Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = (Get-Date).AddMinutes(-$Minutes) } -ErrorAction SilentlyContinue
$hits = $events | Where-Object { $_.Message -match 'CastBridge' }

if (-not $hits) {
    Write-Output "no CastBridge entries in the Application log in the last $Minutes minutes"
    exit 0
}

foreach ($hit in $hits | Select-Object -First 8) {
    Write-Output ("== {0}  {1}  id={2}" -f $hit.TimeCreated, $hit.ProviderName, $hit.Id)
    $lines = $hit.Message -split "`r?`n" | Where-Object { $_.Trim() } | Select-Object -First 6
    foreach ($line in $lines) { Write-Output ("   " + $line.Trim()) }
}
