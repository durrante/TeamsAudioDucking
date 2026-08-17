# Builds a release publish of Teams Audio Ducking into .\publish
#
#   .\build.ps1                    -> self-contained single file (no .NET runtime needed on target)
#   .\build.ps1 -FrameworkDependent -> small output, requires .NET 8 Desktop Runtime on target
param(
    [switch]$FrameworkDependent
)
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $root 'src\TeamsAudioDucking\TeamsAudioDucking.csproj'
$out = Join-Path $root 'publish'

if (Test-Path $out) { Remove-Item $out -Recurse -Force }

$selfContained = if ($FrameworkDependent) { 'false' } else { 'true' }

dotnet publish $project -c Release -r win-x64 -o $out `
    --self-contained $selfContained `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true

if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
Write-Host "Published to $out" -ForegroundColor Green
