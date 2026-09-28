# Builds the iTask installers: publishes a self-contained iTask for each architecture, then wraps
# it with Inno Setup. Output: artifacts\iTask-Setup-<version>-<arch>.exe
#
#   .\installer\build.ps1              # x64 and arm64
#   .\installer\build.ps1 -Arch x64    # just one

param([string[]]$Arch = @("x64", "arm64"))

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root "src\iTask.csproj"

[xml]$csproj = Get-Content $project
$version = @($csproj.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ })[0]
if (-not $version) { throw "No <Version> in $project" }

$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 not found. Install it with: winget install JRSoftware.InnoSetup" }

foreach ($a in $Arch) {
    $publish = Join-Path $root "artifacts\publish\win-$a"
    if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }

    Write-Host "Publishing iTask $version for win-$a..."
    dotnet publish $project -c Release -r "win-$a" --self-contained true -o $publish
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for win-$a" }

    Write-Host "Building installer for $a..."
    & $iscc /Q "/DAppVersion=$version" "/DArch=$a" "/DPublishDir=$publish" (Join-Path $PSScriptRoot "iTask.iss")
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed for $a" }
}

Get-ChildItem (Join-Path $root "artifacts") -Filter "iTask-Setup-*.exe" |
    Select-Object Name, @{ n = "MB"; e = { [math]::Round($_.Length / 1MB, 1) } }
