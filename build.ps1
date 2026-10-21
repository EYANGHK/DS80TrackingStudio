param(
    [switch]$FrameworkDependent
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$dist = Join-Path $root "dist\DS80TrackingStudio"

Write-Host "== Build DS80 Tracking Studio ==" -ForegroundColor Cyan

$selfContained = if ($FrameworkDependent) { "false" } else { "true" }

dotnet publish (Join-Path $root "src\DS80Bridge\DS80Bridge.csproj") `
    -c Release `
    -r win-x64 `
    -p:Platform=x64 `
    -p:SelfContained=$selfContained `
    -p:PublishSingleFile=false `
    -o $dist

if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

New-Item -ItemType Directory -Force -Path (Join-Path $dist "tools") | Out-Null
Copy-Item (Join-Path $root "tools\pose_monitor.py") (Join-Path $dist "tools") -Force
Copy-Item (Join-Path $root "README.md") $dist -Force
Copy-Item (Join-Path $root "docs") (Join-Path $dist "docs") -Recurse -Force

Write-Host ""
Write-Host "Output: $dist" -ForegroundColor Green
Write-Host "Run:    $dist\DS80Bridge.exe --sim --autostart" -ForegroundColor Green
Write-Host ""
Write-Host "Note: real DS80 requires XVSDK installed (default C:\Program Files\xvsdk)." -ForegroundColor Yellow
