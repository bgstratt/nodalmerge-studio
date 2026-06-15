<#
.SYNOPSIS
  Publishes the Studio Host as self-contained binaries for all supported platforms
  and drops them into the extension's bin/ directory.

.DESCRIPTION
  Run this script once before packaging the .vsix.
  Output: clients/vscode-extension/bin/{rid}/NodalMerge.Studio.Host[.exe]

.EXAMPLE
  cd clients/vscode-extension
  .\scripts\publish-host.ps1
#>

param(
  [string]$Configuration = "Release",
  [string]$Version       = "0.1.0"
)

$ErrorActionPreference = "Stop"

$RepoRoot   = Resolve-Path (Join-Path $PSScriptRoot ".." ".." "..")
$HostProject = Join-Path $RepoRoot "src" "NodalMerge.Studio.Host" "NodalMerge.Studio.Host.csproj"
$OutRoot     = Join-Path $PSScriptRoot ".." "bin"

if (-not (Test-Path $HostProject)) {
  Write-Error "Studio Host project not found: $HostProject"
  exit 1
}

$Targets = @(
  @{ RID = "win-x64";     Binary = "NodalMerge.Studio.Host.exe" },
  @{ RID = "linux-x64";   Binary = "NodalMerge.Studio.Host"     },
  @{ RID = "osx-arm64";   Binary = "NodalMerge.Studio.Host"     }
)

foreach ($target in $Targets) {
  $rid    = $target.RID
  $outDir = Join-Path $OutRoot $rid

  Write-Host "Publishing $rid -> $outDir" -ForegroundColor Cyan

  dotnet publish $HostProject `
    --configuration $Configuration `
    --runtime $rid `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:Version=$Version `
    --output $outDir

  if ($LASTEXITCODE -ne 0) {
    Write-Error "dotnet publish failed for $rid"
    exit $LASTEXITCODE
  }

  Write-Host "  -> $outDir\$($target.Binary)" -ForegroundColor Green
}

Write-Host ""
Write-Host "All targets published successfully." -ForegroundColor Green
