# Removes Teams Audio Ducking (per-user install).
$ErrorActionPreference = 'SilentlyContinue'

Stop-Process -Name 'TeamsAudioDucking' -Force
Start-Sleep -Milliseconds 700

Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'TeamsAudioDucking'
Remove-Item (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Teams Audio Ducking.lnk') -Force
Remove-Item (Join-Path $env:LOCALAPPDATA 'Programs\TeamsAudioDucking') -Recurse -Force

Write-Host 'Uninstalled.' -ForegroundColor Green
Write-Host 'Settings and logs were kept in %LOCALAPPDATA%\TeamsAudioDucking - delete that folder too if you want a full clean-up.'
