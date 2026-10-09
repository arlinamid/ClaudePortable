#Requires -Version 7
#
# Build script for the AgentPortable MSI.
# Steps:
#   1. Publish ClaudePortable.App as a self-contained win-x64 single app.
#   2. Invoke WiX via dotnet build on the .wixproj.
#   3. Copy the resulting .msi next to the repo root.
#
# Usage:
#   pwsh .\build-msi.ps1 [-Version 0.1.0] [-Configuration Release]

param(
  [string] $Version = "0.4.0",
  [string] $Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$appCsproj = Join-Path $root "src\ClaudePortable.App\ClaudePortable.App.csproj"
$installerCsproj = Join-Path $root "src\ClaudePortable.Installer\ClaudePortable.Installer.wixproj"
$staging = Join-Path $root "src\ClaudePortable.Installer\staging"
$outputMsi = "AgentPortable-$Version.msi"

Write-Host "[1/3] Cleaning staging..." -ForegroundColor Cyan
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }

Write-Host "[2/3] Publishing App self-contained..." -ForegroundColor Cyan
dotnet publish $appCsproj `
  --configuration $Configuration `
  --runtime win-x64 `
  --self-contained true `
  -p:PublishSingleFile=false `
  -p:InvariantGlobalization=false `
  -p:AssemblyVersion=$Version.0 `
  -p:FileVersion=$Version.0 `
  -p:InformationalVersion=$Version `
  --output $staging
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

Write-Host "[3/3] Building MSI..." -ForegroundColor Cyan
dotnet build $installerCsproj `
  --configuration $Configuration `
  -p:InstallerVersion=$Version `
  -p:PublishStageDir=$staging
if ($LASTEXITCODE -ne 0) { throw "dotnet build wixproj failed" }

# Match this version's file exactly: bin can still hold MSIs from earlier
# builds (incl. the pre-rename ClaudePortable-*.msi), and "first *.msi"
# could pick one of those.
$builtMsi = Get-ChildItem -Path (Join-Path $root "src\ClaudePortable.Installer\bin\$Configuration") -Filter $outputMsi -Recurse |
  Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $builtMsi) { throw "$outputMsi not found in bin output" }

$target = Join-Path $root $outputMsi
Copy-Item $builtMsi.FullName $target -Force
Write-Host ""
Write-Host "OK: $target" -ForegroundColor Green
