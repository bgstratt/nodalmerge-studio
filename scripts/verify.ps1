[CmdletBinding()]
param(
    [string]$Version = "0.1.0-local",
    [switch]$IncludeIntegration
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$studioRoot = Split-Path -Parent $repoRoot

Push-Location $studioRoot
try {
    dotnet build "NodalMerge.Studio.slnx" -c Release -p:NodalMergePackageVersion=$Version

    $filter = "Category!=Integration"
    if ($IncludeIntegration -or $env:NODALMERGE_NATIVE_AVAILABLE -eq "true") {
        $filter = ""
    }

    if ([string]::IsNullOrWhiteSpace($filter)) {
        dotnet test "NodalMerge.Studio.slnx" -c Release --no-build -p:NodalMergePackageVersion=$Version
    }
    else {
        dotnet test "NodalMerge.Studio.slnx" -c Release --no-build -p:NodalMergePackageVersion=$Version --filter $filter
    }
}
finally {
    Pop-Location
}

Write-Host "Verify complete."
