<#
.SYNOPSIS
    Build (and optionally publish) the NodalMerge Studio VSCode extension.

.PARAMETER Publish
    After packaging, publish to the VS Marketplace via vsce.
    Requires VSCE_PAT env var or -Pat parameter.

.PARAMETER Pat
    VS Marketplace Personal Access Token. Falls back to $env:VSCE_PAT if omitted.

.PARAMETER BumpVersion
    Increment the patch version in package.json before building.
    Values: patch | minor | major

.PARAMETER Platforms
    .NET RIDs to publish the host binary for. Defaults to win-x64 only.
    Pass multiple to build a multi-platform VSIX: -Platforms win-x64,linux-x64,osx-arm64

.PARAMETER SkipDotnet
    Skip the dotnet publish step (use existing bin/ output). Useful for iterating on the
    JS side when the host binary hasn't changed.

.EXAMPLE
    # Build for win-x64 (default)
    .\scripts\build-vsix.ps1

.EXAMPLE
    # Build for all platforms
    .\scripts\build-vsix.ps1 -Platforms win-x64,win-arm64,linux-x64,linux-arm64,osx-x64,osx-arm64

.EXAMPLE
    # Bump patch version, build, and publish
    .\scripts\build-vsix.ps1 -BumpVersion patch -Publish
#>
param(
    [switch]$Publish,
    [string]$Pat,
    [ValidateSet('patch', 'minor', 'major', '')]
    [string]$BumpVersion = '',
    [string[]]$Platforms = @('win-x64'),
    [switch]$SkipDotnet
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$ExtDir  = $PSScriptRoot | Split-Path -Parent
$RepoRoot = $ExtDir | Split-Path -Parent | Split-Path -Parent
$HostProject = Join-Path $RepoRoot 'src' 'NodalMerge.Studio.Host' 'NodalMerge.Studio.Host.csproj'
$NuGetConfig = Join-Path $RepoRoot 'NuGet.config'

Push-Location $ExtDir
try {

    # ── 1. Optional version bump ─────────────────────────────────────────────
    if ($BumpVersion) {
        Write-Host "[build] Bumping $BumpVersion version..." -ForegroundColor Cyan
        npm version $BumpVersion --no-git-tag-version
        if ($LASTEXITCODE -ne 0) { throw "npm version bump failed" }
    }

    # ── 2. Read current version for output filename ──────────────────────────
    $pkg     = Get-Content 'package.json' -Raw | ConvertFrom-Json
    $version = $pkg.version
    $name    = $pkg.name
    Write-Host "[build] $name v$version" -ForegroundColor Cyan

    # ── 3. Publish .NET host binary for each target platform ─────────────────
    if (-not $SkipDotnet) {
        foreach ($rid in $Platforms) {
            $outDir = Join-Path $ExtDir 'bin' $rid
            Write-Host "[build] dotnet publish → bin/$rid/ ..." -ForegroundColor Cyan
            dotnet publish $HostProject `
                -c Release `
                -r $rid `
                --self-contained false `
                --configfile $NuGetConfig `
                -o $outDir `
                /p:NodalMergePackageVersion=0.1.0-local
            if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $rid" }
        }
    } else {
        Write-Host "[build] Skipping dotnet publish (-SkipDotnet)" -ForegroundColor Yellow
    }

    # ── 4. Install / ensure JS deps ──────────────────────────────────────────
    if (-not (Test-Path 'node_modules')) {
        Write-Host "[build] Installing npm dependencies..." -ForegroundColor Cyan
        npm ci
        if ($LASTEXITCODE -ne 0) { throw "npm ci failed" }
    }

    # ── 5. Type-check ────────────────────────────────────────────────────────
    Write-Host "[build] Type-checking..." -ForegroundColor Cyan
    npm run typecheck
    if ($LASTEXITCODE -ne 0) { throw "TypeScript type-check failed" }

    # ── 6. Production JS bundle ───────────────────────────────────────────────
    Write-Host "[build] Bundling (production)..." -ForegroundColor Cyan
    node esbuild.config.mjs --production
    if ($LASTEXITCODE -ne 0) { throw "esbuild failed" }

    # ── 7. Package .vsix ──────────────────────────────────────────────────────
    $outFile = "$name-$version.vsix"
    Write-Host "[build] Packaging → $outFile ..." -ForegroundColor Cyan
    npx vsce package --no-dependencies --out $outFile
    if ($LASTEXITCODE -ne 0) { throw "vsce package failed" }

    $outPath = Join-Path $ExtDir $outFile
    Write-Host "[build] Done: $outPath" -ForegroundColor Green

    # ── 8. Optional publish ───────────────────────────────────────────────────
    if ($Publish) {
        $token = if ($Pat) { $Pat } else { $env:VSCE_PAT }
        if (-not $token) {
            throw "Publish requested but no PAT provided. Set `$env:VSCE_PAT or pass -Pat <token>."
        }
        Write-Host "[build] Publishing to VS Marketplace..." -ForegroundColor Cyan
        npx vsce publish --no-dependencies --pat $token --packagePath $outFile
        if ($LASTEXITCODE -ne 0) { throw "vsce publish failed" }
        Write-Host "[build] Published $name v$version" -ForegroundColor Green
    }

} finally {
    Pop-Location
}
