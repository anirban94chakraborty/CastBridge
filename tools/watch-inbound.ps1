# Watches for inbound connections to the media server while a cast is running.
# Answers: does the speaker even try to reach this PC, or does it never try at all?
param([int]$Seconds = 25, [int]$Port = 45455)

$seen = @{}
$deadline = (Get-Date).AddSeconds($Seconds)

Write-Output "watching port $Port for $Seconds seconds..."
while ((Get-Date) -lt $deadline) {
    Get-NetTCPConnection -LocalPort $Port -ErrorAction SilentlyContinue |
        Where-Object { $_.RemoteAddress -and $_.RemoteAddress -ne '127.0.0.1' -and $_.RemoteAddress -ne '::1' } |
        ForEach-Object {
            $key = "$($_.RemoteAddress):$($_.RemotePort) -> $($_.LocalPort) $($_.State)"
            if (-not $seen.ContainsKey($key)) {
                $seen[$key] = $true
                Write-Output ("  " + (Get-Date -Format 'HH:mm:ss') + "  " + $key)
            }
        }
    Start-Sleep -Milliseconds 200
}

if ($seen.Count -eq 0) {
    Write-Output 'NO INBOUND CONNECTION FROM ANY SPEAKER'
} else {
    Write-Output "$($seen.Count) distinct inbound connection(s) seen"
}