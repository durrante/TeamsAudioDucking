# Per-user install of Teams Audio Ducking. No administrator rights required.
# Installs to %LOCALAPPDATA%\Programs\TeamsAudioDucking, creates a Start-menu
# shortcut and a per-user startup entry, then launches the app.
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$publish = Join-Path $root 'publish'
$exeName = 'TeamsAudioDucking.exe'

if (-not (Test-Path (Join-Path $publish $exeName))) {
    Write-Host 'No publish output found; building first...'
    & (Join-Path $root 'build.ps1')
}

$target = Join-Path $env:LOCALAPPDATA 'Programs\TeamsAudioDucking'
$targetExe = Join-Path $target $exeName

# Stop a running instance so files can be replaced.
Stop-Process -Name 'TeamsAudioDucking' -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 700

New-Item -ItemType Directory -Force $target | Out-Null
Copy-Item (Join-Path $publish '*') $target -Recurse -Force

# Start-menu shortcut (per user).
$startMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut((Join-Path $startMenu 'Teams Audio Ducking.lnk'))
$shortcut.TargetPath = $targetExe
$shortcut.WorkingDirectory = $target
$shortcut.Description = 'Mutes other applications during Microsoft Teams calls'
$shortcut.Save()

# Per-user startup (HKCU Run key - no admin needed).
Set-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' `
    -Name 'TeamsAudioDucking' -Value ('"{0}"' -f $targetExe)

Start-Process $targetExe
Write-Host 'Installed and started. Look for the green tray icon.' -ForegroundColor Green
Write-Host "Install folder : $target"
Write-Host 'To remove      : run installer\uninstall.ps1'
