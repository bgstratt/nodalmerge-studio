<#
.SYNOPSIS
  Runs ONE (repo, task, arm) combination of the harness-comparison-eval end to end for
  the Claude-Code-driven arms (C, D, R1, R2): fresh checkout, invoke Claude Code
  headless, parse its own usage/cost, grade the result.

.DESCRIPTION
  Deliberately scoped to one run at a time, not a full N=3-per-cell sweep — spend and
  API credentials stay under your explicit control per invocation. Loop this yourself
  (or wrap it) once you've dry-run it manually and are ready to spend real API budget.

  Arms A/B (the Studio orchestrator) are NOT handled by this script — see RUNBOOK.md
  for the REST call sequence, which hasn't been executed/verified against a live Studio
  instance from this session.

.PARAMETER StubRepo
  Path to the stub repo to clone from (e.g. ..\..\eval-stub-small).

.PARAMETER TaskPath
  Path to the eval-tasks/<repo>/<task-id>/ package.

.PARAMETER Arm
  "C" or "D" (via Studio's MCP server) or "R1"/"R2" (raw Claude Code, no MCP).

.PARAMETER ResultsFile
  Passed through to grade-task.ps1.

.PARAMETER McpUrl
  Studio's MCP endpoint, e.g. http://localhost:5000/mcp. Required for Arm C/D, ignored
  for R1/R2.

.PARAMETER SubagentConfigPath
  Path to the tiered-model subagent definition (see plans/harness-comparison-eval.md
  Appendix A). Required for Arm D/R2, ignored for A/C.

.PARAMETER RunsDir
  Where per-run checkouts live, persistently — not $env:TEMP. Defaults to a sibling
  `eval-harness-runs/` next to this repos root (i.e. NOT nested inside
  nodalmerge-studio) — deliberately outside nodalmerge-studio's own directory tree,
  because MSBuild walks up looking for Directory.Build.props/Directory.Packages.props
  and nodalmerge-studio's own Central Package Management config silently breaks a
  nested stub repo's plain <PackageReference Version="..."> entries if the checkout
  lives underneath it (found by actually running this, not by inspection — see the
  plan doc's verification notes). Checkouts are never auto-deleted; the whole point is
  being able to go back and read a surprising run's actual diff, not just its pass/fail
  line in results.jsonl.

.PARAMETER KeepBuildArtifacts
  By default, bin/ and obj/ under the checkout are deleted after grading — dotnet build
  output is fully reproducible from source and just costs disk across dozens of runs.
  Pass this switch to keep them (e.g. to inspect a specific build/test failure without
  re-running dotnet test yourself).

.EXAMPLE
  ./run-one.ps1 -StubRepo ..\..\eval-stub-small `
    -TaskPath ..\..\nodalmerge-studio\plans\eval-tasks\eval-stub-small\task-01-discount-bug `
    -Arm R1 -ResultsFile .\results.jsonl
#>
param(
    [Parameter(Mandatory)] [string]$StubRepo,
    [Parameter(Mandatory)] [string]$TaskPath,
    [Parameter(Mandatory)] [ValidateSet("C", "D", "R1", "R2")] [string]$Arm,
    [Parameter(Mandatory)] [string]$ResultsFile,
    [string]$McpUrl,
    [string]$SubagentConfigPath,
    [string]$RunsDir = (Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) "eval-harness-runs"),
    [switch]$KeepBuildArtifacts,
    [string]$RunId = [guid]::NewGuid().ToString("N").Substring(0, 12),
    [int]$AttemptNumber = 1
)

$ErrorActionPreference = "Stop"

if ($Arm -in @("C", "D") -and -not $McpUrl) { throw "Arm $Arm requires -McpUrl (Studio's MCP endpoint)." }
if ($Arm -in @("D", "R2") -and -not $SubagentConfigPath) { throw "Arm $Arm requires -SubagentConfigPath (tiered subagent definition)." }

# Resolve every path parameter to absolute BEFORE any Push-Location below — relative
# paths captured in a variable are re-evaluated against the current directory at the
# point of use, not at assignment, so they silently break once we cd into the checkout.
$TaskPath = (Resolve-Path $TaskPath).Path
$StubRepo = (Resolve-Path $StubRepo).Path
if ($SubagentConfigPath) { $SubagentConfigPath = (Resolve-Path $SubagentConfigPath).Path }
if (-not [System.IO.Path]::IsPathRooted($ResultsFile)) { $ResultsFile = Join-Path (Get-Location) $ResultsFile }

$taskId = Split-Path $TaskPath -Leaf
$repoName = Split-Path $StubRepo -Leaf
$taskMd = Join-Path $TaskPath "task.md"
$baselineRef = (Get-Content (Join-Path $TaskPath "baseline-ref") -Raw).Trim()
if (-not (Test-Path $taskMd)) { throw "Missing task.md in $TaskPath" }

# Persistent, browsable layout: runs/<repo>/<task-id>/<arm>/<runId>-attempt<N>/ — so a
# surprising result can be found by walking directories, not just by grepping runId out
# of results.jsonl.
$checkout = Join-Path $RunsDir $repoName $taskId $Arm "$RunId-attempt$AttemptNumber"
if (Test-Path $checkout) { throw "Checkout dir already exists (reused RunId?): $checkout" }
New-Item -ItemType Directory -Force -Path (Split-Path $checkout -Parent) | Out-Null

Write-Host "Cloning $StubRepo @ $baselineRef -> $checkout"
git clone -q $StubRepo $checkout
Push-Location $checkout
try {
    git checkout -q $baselineRef

    # task.md's "## Grading" section names the exact hidden test file(s) and sometimes
    # describes their expected assertions — that's for whoever runs the eval, not the
    # agent. Extract only the "## Goal (as given to the agent)" section.
    $taskMdRaw = Get-Content $taskMd -Raw
    $goalMatch = [regex]::Match(
        $taskMdRaw,
        '## Goal \(as given to the agent\)\s*\r?\n(.*?)(?:\r?\n## Grading|\z)',
        [System.Text.RegularExpressions.RegexOptions]::Singleline)
    if (-not $goalMatch.Success) {
        throw "task.md doesn't match the expected '## Goal (as given to the agent)' ... '## Grading' structure — refusing to paste the whole file (would leak hidden-test details)."
    }
    $goalText = $goalMatch.Groups[1].Value.Trim()

    if ($Arm -in @("C", "D")) {
        # Point Claude Code at Studio's MCP server for this checkout only.
        $mcpConfig = @{ mcpServers = @{ studio = @{ type = "http"; url = $McpUrl } } } | ConvertTo-Json -Depth 5
        Set-Content -Path ".mcp.json" -Value $mcpConfig

        # sessionId convention: McpToolDispatcher only appends WorkspaceRead/Search/
        # FileLeaseContended/ArtifactSurfaced events when a caller passes a sessionId —
        # Studio's own OrchestratorAgentLoop always does; Claude Code has no reason to
        # know to unless told. Without this, Arms C/D get zero tool-call telemetry from
        # Studio's side (Claude Code's own --output-format json usage/cost still works
        # regardless — this only affects the McpToolDispatcher-side event trail).
        $sessionInstruction = "IMPORTANT: on every nm_v1_* tool call that accepts a `sessionId` parameter, always pass sessionId=`"$RunId`" (this exact run ID, every time) so this run's tool activity is traceable.`n`n"
        $goalText = $sessionInstruction + $goalText
    }

    if ($Arm -in @("D", "R2")) {
        New-Item -ItemType Directory -Force -Path ".claude\agents" | Out-Null
        Copy-Item $SubagentConfigPath -Destination ".claude\agents\studio-executor.md" -Force
        $goalText += "`n`nPlan on the current model. Delegate the actual implementation step to the `studio-executor` subagent via the Task tool."
    }

    Write-Host "Invoking Claude Code headless (arm $Arm)..."
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    # NOTE: not executed from this session — verify the exact flag names against your
    # installed `claude --help` before relying on this. -p/--print + --output-format json
    # is documented headless/scripted usage as of authoring time.
    $rawOutput = claude -p $goalText --output-format json 2>&1
    $sw.Stop()

    $parsed = $null
    try { $parsed = $rawOutput | ConvertFrom-Json } catch {
        Write-Warning "Could not parse Claude Code output as JSON — check --output-format json is supported by your CLI version. Raw output follows."
        Write-Host $rawOutput
    }

    $wallClock = $sw.Elapsed.TotalSeconds
    $tokensIn = $parsed.usage.input_tokens
    $tokensOut = $parsed.usage.output_tokens
    $costUsd = $parsed.total_cost_usd
    $turnCount = $parsed.num_turns
}
finally {
    Pop-Location
}

& (Join-Path $PSScriptRoot "grade-task.ps1") `
    -RepoPath $checkout -TaskPath $TaskPath -ResultsFile $ResultsFile `
    -Arm $Arm -RunId $RunId -AttemptNumber $AttemptNumber `
    -WallClockSeconds $wallClock -TokensIn $tokensIn -TokensOut $tokensOut `
    -CostUsd $costUsd -TurnCount $turnCount
$gradeExitCode = $LASTEXITCODE

if (-not $KeepBuildArtifacts) {
    Get-ChildItem $checkout -Recurse -Directory -Include "bin", "obj" -ErrorAction SilentlyContinue |
        Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "Checkout kept at $checkout (source + diff only unless -KeepBuildArtifacts) — not auto-deleted."
exit $gradeExitCode
