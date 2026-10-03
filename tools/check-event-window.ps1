# Dumps everything the Application log recorded around a moment in time. Windows Error Reporting
# entries here usually carry the exception details that the crash event omits.
param(
    [datetime]$At = (Get-Date),
    [int]$Seconds = 90
)

$from = $At.AddSeconds(-$Seconds)
$to = $At.AddSeconds($Seconds)
$events = Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = $from; EndTime = $to } -ErrorAction SilentlyContinue

if (-not $events) {
    Write-Output "nothing logged between $from and $to"
    exit 0
}

foreach ($event in $events | Sort-Object TimeCreated) {
    Write-Output ("== {0}  {1}  id={2}" -f $event.TimeCreated, $event.ProviderName, $event.Id)
    $lines = $event.Message -split "`r?`n" | Where-Object { $_.Trim() } | Select-Object -First 12
    foreach ($line in $lines) { Write-Output ("   " + $line.Trim()) }
}
