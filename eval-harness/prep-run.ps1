<#
.SYNOPSIS
  Prepares ONE (repo, arm, attempt) checkout covering ALL of that repo's tasks at once —
  clones and writes a combined prompt file, then stops. Does not invoke `claude`, does
  not touch Studio, and does not grade; you drive the session yourself, then grade
  afterward with grade-task.ps1.

.DESCRIPTION
  Four-arm design, no MCP anywhere — MCP was explored for C/D and dropped as
  unnecessary indirection (Studio's external MCP surface is just a goal-delegation API;
  submitting the goal directly through Studio's own UI is simpler and identical in
  effect):

    R1 = raw Claude Code, single model (Sonnet) — paste EVAL-PROMPT.md into a Claude
         Code chat; it edits files with its own native tools.
    R2 = same, tiered (Sonnet plans, Haiku executes via a subagent).
    C  = Studio's own orchestrator, single model — paste EVAL-PROMPT.md directly into
         Studio's own goal field (VS Code extension or however you drive Studio) with
         that repo/goal's model config set to single-model. No Claude Code involved.
    D  = same as C, but with Studio's model config set to TIERED (Sonnet
         orchestrate/plan, Haiku execute) before the goal runs. C and D use the
         IDENTICAL prompt — the only difference is which Studio-side model config was
         active when the goal ran.

.PARAMETER StubRepo
  Path to the stub repo to clone from (e.g. ..\..\eval-stub-small).

.PARAMETER EvalTasksDir
  Path to the eval-tasks/<repo>/ directory — every task-*/ subdirectory found directly
  under it is included in the combined session. Mutually exclusive with -TaskPath.

.PARAMETER TaskPath
  Explicit list of eval-tasks/<repo>/<task-id>/ package paths to include, if you don't
  want all of them. Mutually exclusive with -EvalTasksDir.

.PARAMETER Arm
  "C"/"D" (Studio's own orchestrator, goal pasted directly into Studio) or "R1"/"R2"
  (raw Claude Code).

.PARAMETER SubagentConfigPath
  Path to the tiered-model subagent definition. Required for Arm R2 only.

.PARAMETER RunsDir
  Where the checkout lives, persistently. Defaults to a sibling `eval-harness-runs/`
  OUTSIDE nodalmerge-studio (nested checkouts inherit nodalmerge-studio's NuGet Central
  Package Management config and fail to build — found by actually running this).

.EXAMPLE
  ./prep-run.ps1 -StubRepo ..\..\eval-stub-small -EvalTasksDir ..\plans\eval-tasks\eval-stub-small -Arm R1

.EXAMPLE
  ./prep-run.ps1 -StubRepo ..\..\eval-stub-small -EvalTasksDir ..\plans\eval-tasks\eval-stub-small -Arm C
#>
param(
    [Parameter(Mandatory)] [string]$StubRepo,
    [string]$EvalTasksDir,
    [string[]]$TaskPath,
    [Parameter(Mandatory)] [ValidateSet("C", "D", "R1", "R2")] [string]$Arm,
    [string]$SubagentConfigPath,
    [string]$RunsDir = (Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) "eval-harness-runs"),
    [string]$RunId = [guid]::NewGuid().ToString("N").Substring(0, 12),
    [int]$AttemptNumber = 1
)

$ErrorActionPreference = "Stop"

if ($Arm -eq "R2" -and -not $SubagentConfigPath) { throw "Arm R2 requires -SubagentConfigPath (tiered subagent definition)." }
if (-not $EvalTasksDir -and -not $TaskPath) { throw "Provide either -EvalTasksDir (all tasks under it) or -TaskPath (explicit list)." }
if ($EvalTasksDir -and $TaskPath) { throw "-EvalTasksDir and -TaskPath are mutually exclusive." }

$StubRepo = (Resolve-Path $StubRepo).Path
if ($SubagentConfigPath) { $SubagentConfigPath = (Resolve-Path $SubagentConfigPath).Path }

if ($EvalTasksDir) {
    $EvalTasksDir = (Resolve-Path $EvalTasksDir).Path
    $TaskPath = @(Get-ChildItem $EvalTasksDir -Directory | Sort-Object Name | ForEach-Object { $_.FullName })
    if ($TaskPath.Count -eq 0) { throw "No task-*/ subdirectories found under $EvalTasksDir" }
}
else {
    $TaskPath = @($TaskPath | ForEach-Object { (Resolve-Path $_).Path })
}

# --- Load and validate every task package, confirm shared baseline-ref -----------------
# Every task.md in this repo follows "## Goal (as given to the agent)" ... "## Grading"
# — the Grading section names the exact hidden test file(s) and sometimes describes
# their expected assertions (e.g. "it will fail to compile against an incomplete
# rename"). That's for whoever runs the eval, not the agent — handing it over verbatim
# would leak exactly what the hidden test checks. Extract only the Goal section.
function Get-TaskGoalOnly([string]$taskMdRaw) {
    $match = [regex]::Match(
        $taskMdRaw,
        '## Goal \(as given to the agent\)\s*\r?\n(.*?)(?:\r?\n## Grading|\z)',
        [System.Text.RegularExpressions.RegexOptions]::Singleline)
    if (-not $match.Success) {
        throw "task.md doesn't match the expected '## Goal (as given to the agent)' ... '## Grading' structure — can't safely extract just the goal without risking a leak. Fix the task.md or this extraction pattern."
    }
    return $match.Groups[1].Value.Trim()
}

$tasks = @()
foreach ($tp in $TaskPath) {
    $taskMdPath = Join-Path $tp "task.md"
    $baselineRefPath = Join-Path $tp "baseline-ref"
    if (-not (Test-Path $taskMdPath)) { throw "Missing task.md in $tp" }
    if (-not (Test-Path $baselineRefPath)) { throw "Missing baseline-ref in $tp" }
    $tasks += [pscustomobject]@{
        TaskId      = Split-Path $tp -Leaf
        Path        = $tp
        TaskMd      = Get-TaskGoalOnly (Get-Content $taskMdPath -Raw)
        BaselineRef = (Get-Content $baselineRefPath -Raw).Trim()
    }
}
$distinctBaselines = @($tasks.BaselineRef | Select-Object -Unique)
if ($distinctBaselines.Count -gt 1) {
    throw "Tasks reference different baseline-refs ($($distinctBaselines -join ', ')) — can't prep one checkout for all of them."
}
$baselineRef = $distinctBaselines[0]
$repoName = Split-Path $StubRepo -Leaf
$checkout = Join-Path $RunsDir $repoName "combined" $Arm "$RunId-attempt$AttemptNumber"
if (Test-Path $checkout) { throw "Checkout dir already exists (reused RunId?): $checkout" }

# --- Build the combined prompt ----------------------------------------------------------
# Identical for all four arms now — C/D paste this into Studio's own goal field, R1/R2
# paste it into a Claude Code chat. No MCP, no Studio-specific instructions needed.
$promptSections = @(
    "# Combined session — $($tasks.Count) tasks in one repository",
    "",
    "Complete ALL $($tasks.Count) tasks below, in this single session, in this repository. " +
    "They are independent of each other unless a task's own text says otherwise. Work " +
    "through them in any order that makes sense to you.",
    ""
)
$i = 0
foreach ($task in $tasks) {
    $i++
    $promptSections += "---"
    $promptSections += ""
    $promptSections += "## Task $i of $($tasks.Count): $($task.TaskId)"
    $promptSections += ""
    $promptSections += $task.TaskMd.Trim()
    $promptSections += ""
}
$combinedTaskText = ($promptSections -join "`n")

if ($Arm -eq "R2") {
    $combinedPrompt = $combinedTaskText + "`n`n---`n`nFor each task's implementation step, plan on the current model, then delegate the actual implementation to the ``studio-executor`` subagent via the Task tool."
}
else {
    $combinedPrompt = $combinedTaskText
}

# --- Prepare the checkout ----------------------------------------------------------------
New-Item -ItemType Directory -Force -Path (Split-Path $checkout -Parent) | Out-Null

Write-Host "Cloning $StubRepo @ $baselineRef -> $checkout"
git clone -q $StubRepo $checkout
Push-Location $checkout
try {
    git checkout -q $baselineRef

    if ($Arm -eq "R2") {
        New-Item -ItemType Directory -Force -Path ".claude\agents" | Out-Null
        Copy-Item $SubagentConfigPath -Destination ".claude\agents\studio-executor.md" -Force
    }

    Set-Content -Path "EVAL-PROMPT.md" -Value $combinedPrompt
}
finally {
    Pop-Location
}

# A manifest of which task packages this checkout covers, so grade-task.ps1's -TaskPath
# list doesn't have to be retyped/remembered later.
$manifest = [ordered]@{
    runId         = $RunId
    arm           = $Arm
    attemptNumber = $AttemptNumber
    repoPath      = $checkout
    baselineRef   = $baselineRef
    taskPaths     = @($tasks.Path)
}
$manifest | ConvertTo-Json -Depth 4 | Set-Content -Path (Join-Path $checkout "eval-manifest.json")

Write-Host ""
Write-Host "Prepped: $checkout"
Write-Host "  Arm: $Arm   Tasks: $($tasks.Count) ($(($tasks.TaskId) -join ', '))"
Write-Host ""
Write-Host "Next steps:"
if ($Arm -in @("C", "D")) {
    if ($Arm -eq "D") {
        Write-Host "  1. Set Studio's model config for this repo/goal to TIERED (Sonnet"
        Write-Host "     orchestrate/plan, Haiku execute) before submitting the goal — this is"
        Write-Host "     the only thing that distinguishes D from C; the prompt is identical."
        Write-Host "  2. Register/open $checkout in Studio."
        Write-Host "  3. Paste the contents of EVAL-PROMPT.md into Studio's goal field and submit."
        Write-Host "  4. When Studio finishes, grade it:"
    }
    else {
        Write-Host "  1. Make sure Studio's model config for this repo/goal is SINGLE-MODEL"
        Write-Host "     (same model for every stage) before submitting the goal."
        Write-Host "  2. Register/open $checkout in Studio."
        Write-Host "  3. Paste the contents of EVAL-PROMPT.md into Studio's goal field and submit."
        Write-Host "  4. When Studio finishes, grade it:"
    }
}
else {
    Write-Host "  1. Open $checkout in Claude Code (interactive)."
    Write-Host "  2. Paste the contents of EVAL-PROMPT.md as your first message."
    Write-Host "  3. When Claude Code finishes, grade it:"
}
Write-Host "     ./grade-task.ps1 -RepoPath '$checkout' -Arm $Arm -RunId $RunId -AttemptNumber $AttemptNumber ``"
Write-Host "       -ResultsFile .\results.jsonl -TaskPath @($(($tasks.Path | ForEach-Object { "'$_'" }) -join ', '))"
Write-Host "     (or read eval-manifest.json's taskPaths field back programmatically)"
