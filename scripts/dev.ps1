[CmdletBinding()]
param(
    [string]$Version = "0.1.0-local"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$studioRoot = Split-Path -Parent $repoRoot

Push-Location $studioRoot
try {
    dotnet build "NodalMerge.Studio.slnx" -c Release -p:NodalMergePackageVersion=$Version
    dotnet run --project "src/NodalMerge.Studio.Host/NodalMerge.Studio.Host.csproj" -c Release --no-build -p:NodalMergePackageVersion=$Version
}
finally {
    Pop-Location
}
