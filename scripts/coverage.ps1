[CmdletBinding()]
param(
    [switch]$IncludeIntegration,
    [switch]$OpenReport
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$studioRoot = Split-Path -Parent $repoRoot
$resultsDir = Join-Path $studioRoot "TestResults"
$reportDir = Join-Path $studioRoot "CoverageReport"

Push-Location $studioRoot
try {
    if (Test-Path $resultsDir) { Remove-Item $resultsDir -Recurse -Force }
    if (Test-Path $reportDir) { Remove-Item $reportDir -Recurse -Force }

    $filter = "Category!=Integration"
    if ($IncludeIntegration -or $env:NODALMERGE_NATIVE_AVAILABLE -eq "true") {
        $filter = ""
    }

    $testArgs = @(
        "test", "NodalMerge.Studio.slnx",
        "--collect:XPlat Code Coverage",
        "--results-directory", $resultsDir
    )
    if (-not [string]::IsNullOrWhiteSpace($filter)) {
        $testArgs += @("--filter", $filter)
    }

    dotnet @testArgs

    dotnet tool run reportgenerator `
        "-reports:$resultsDir/**/coverage.cobertura.xml" `
        "-targetdir:$reportDir" `
        "-reporttypes:Html;TextSummary"

    Get-Content (Join-Path $reportDir "Summary.txt")

    if ($OpenReport) {
        Start-Process (Join-Path $reportDir "index.html")
    }
}
finally {
    Pop-Location
}

Write-Host "Coverage report: $reportDir/index.html"
