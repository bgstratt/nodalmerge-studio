[CmdletBinding()]
param(
    [string]$Version = "0.1.1",
    [string]$NodalMergeRepo = "../nodalmerge"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$studioRoot = Split-Path -Parent $repoRoot
$nodalMergePath = [System.IO.Path]::GetFullPath((Join-Path $studioRoot $NodalMergeRepo))

if (-not (Test-Path $nodalMergePath)) {
    throw "NodalMerge repo not found at $nodalMergePath"
}

$packScript = Join-Path $nodalMergePath "pack-local-artifacts.ps1"
if (-not (Test-Path $packScript)) {
    throw "pack-local-artifacts.ps1 not found at $packScript"
}

Write-Host "Packing local NodalMerge artifacts (version $Version) ..."
Push-Location $nodalMergePath
try {
    & $packScript -Version $Version -SkipNpm -SkipCrates
}
finally {
    Pop-Location
}

Write-Host "Restoring NodalMerge Studio solution ..."
$localFeed = Join-Path $nodalMergePath "artifacts/package-local/nuget"
Push-Location $studioRoot
try {
    # RestoreAdditionalProjectSources appends the local feed to NuGet.config's sources.
    # (Passing multiple --source flags misparses the URL as a relative local path on
    # current SDKs — same issue previously fixed in nodalmerge's pack-local-nuget.ps1.)
    dotnet restore "NodalMerge.Studio.slnx" --configfile "NuGet.config" "-p:RestoreAdditionalProjectSources=$localFeed"
}
finally {
    Pop-Location
}

Write-Host "Done."
