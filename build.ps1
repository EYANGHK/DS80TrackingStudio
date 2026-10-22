param(
    [switch]$FrameworkDependent,
    # 可选输出目录；默认 dist\DS80TrackingStudio。旧版程序正在运行时用它可以避免文件占用。
    [string]$Output
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$dist = if ([string]::IsNullOrWhiteSpace($Output)) { Join-Path $root "dist\DS80TrackingStudio" }
        elseif ([IO.Path]::IsPathRooted($Output)) { $Output }
        else { Join-Path $root $Output }

Write-Host "== Build DS80 Tracking Studio ==" -ForegroundColor Cyan

& (Join-Path $root "native\build.cmd")
if ($LASTEXITCODE -ne 0) { throw "native SDK bridge build failed" }

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
Copy-Item (Join-Path $root "tools\*.py") (Join-Path $dist "tools") -Force
Copy-Item (Join-Path $root "README.md") $dist -Force
foreach ($name in @("profiles", "docs")) {
    $target = Join-Path $dist $name
    if (Test-Path $target) { Remove-Item $target -Recurse -Force }
    Copy-Item (Join-Path $root $name) $target -Recurse -Force
}

Write-Host ""
Write-Host "Output: $dist" -ForegroundColor Green
Write-Host "Run:    $dist\DS80Bridge.exe --sim --autostart" -ForegroundColor Green
Write-Host ""
Write-Host "Note: real DS80 requires XVSDK installed (default C:\Program Files\xvsdk)." -ForegroundColor Yellow
