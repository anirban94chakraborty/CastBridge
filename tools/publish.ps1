# Rebuilds the single-file CastBridge.exe and refreshes the Desktop shortcut.
param(
    [switch]$NoShortcut
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\CastBridge.App\CastBridge.App.csproj'
$output = Join-Path $root 'dist'

Write-Host 'Publishing CastBridge (self-contained, single file)...'
if (Test-Path $output) { Remove-Item $output -Recurse -Force }

dotnet publish $project -c Release -o $output --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

# Referenced projects still emit symbols; they are dead weight next to the executable.
Get-ChildItem $output -Filter *.pdb -ErrorAction SilentlyContinue | Remove-Item -Force

$exe = Join-Path $output 'CastBridge.exe'
$sizeMb = [math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host "Built $exe ($sizeMb MB)" -ForegroundColor Green

if (-not $NoShortcut) {
    $desktop = [Environment]::GetFolderPath('Desktop')
    $link = Join-Path $desktop 'CastBridge.lnk'
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($link)
    $shortcut.TargetPath = $exe
    $shortcut.WorkingDirectory = $output
    $shortcut.IconLocation = "$exe,0"
    $shortcut.Description = 'Control Cast speakers on your local network'
    $shortcut.Save()
    Write-Host "Desktop shortcut: $link" -ForegroundColor Green
}