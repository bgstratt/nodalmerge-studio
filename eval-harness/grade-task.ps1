<#
.SYNOPSIS
  Grades one harness-comparison-eval run against ONE OR MORE task packages sharing the
  same checkout — supports a combined session where an agent was handed all of a repo's
  tasks at once, not just single-task runs.

.DESCRIPTION
  Order matters: precision (git diff vs the union of every task's expected-files.txt) is
  computed BEFORE any hidden test files are copied in, so the grading artifacts
  themselves never get counted as part of the agent's diff.

  Runs the whole suite once with a TRX logger, then attributes outcomes back to
  individual tasks by matching each hidden test file's class name(s) against the TRX
  results — so a combined 4-tasks-in-one-session run reports which of the 4 actually
  passed, not just one aggregate pass/fail that hides partial credit. If a test
  project fails to *build* at all (as opposed to a test failing), every task whose
  hidden tests live in that project is reported as "notRun", not silently as a pass.

  Does not revert anything — RepoPath is assumed to be a scratch checkout the caller
  keeps or discards after grading, not the stub repo's own working copy.

.PARAMETER RepoPath
  Path to the checkout containing the agent's finished diff (working tree, may be
  uncommitted). Graded in place.

.PARAMETER TaskPath
  One or more paths to eval-tasks/<repo>/<task-id>/ packages (task.md, baseline-ref,
  expected-files.txt, hidden-tests/<TestProject>/*.cs). All must share the same
  baseline-ref — they're graded against a single checkout.

.PARAMETER ResultsFile
  JSONL file to append the result record to. Created if it doesn't exist.

.PARAMETER Arm
  Label for which harness/model-strategy arm produced this run (e.g. "A", "B", "C",
  "D", "R1", "R2") — free text, not validated against the plan's matrix.

.PARAMETER RunId, AttemptNumber, WallClockSeconds, TokensIn, TokensOut, CostUsd,
  ToolCallCount, TurnCount, ClarificationCount
  Pass-through metrics the caller already collected. All optional — omit whatever a
  given arm can't supply and it's recorded as null.

.EXAMPLE
  # Single task (original usage — still works, TaskPath just binds a 1-element array)
  ./grade-task.ps1 -RepoPath C:\scratch\run-017 -TaskPath ..\plans\eval-tasks\eval-stub-small\task-01-discount-bug `
    -ResultsFile .\results.jsonl -Arm A -RunId run-017

.EXAMPLE
  # Combined session — agent was handed all 4 tasks for this repo at once
  ./grade-task.ps1 -RepoPath C:\scratch\run-018 -Arm R1 -RunId run-018 -ResultsFile .\results.jsonl -TaskPath @(
    "..\plans\eval-tasks\eval-stub-small\task-01-discount-bug",
    "..\plans\eval-tasks\eval-stub-small\task-02-subscription-line-item",
    "..\plans\eval-tasks\eval-stub-small\task-03-fulfillment-lease",
    "..\plans\eval-tasks\eval-stub-small\task-04-rename-buyer-id"
  )
#>
param(
    [Parameter(Mandatory)] [string]$RepoPath,
    [Parameter(Mandatory)] [string[]]$TaskPath,
    [Parameter(Mandatory)] [string]$ResultsFile,
    [Parameter(Mandatory)] [string]$Arm,
    [string]$RunId = [guid]::NewGuid().ToString("N").Substring(0, 12),
    [int]$AttemptNumber = 1,
    [double]$WallClockSeconds,
    [int]$TokensIn,
    [int]$TokensOut,
    [double]$CostUsd,
    [int]$ToolCallCount,
    [int]$TurnCount,
    [int]$ClarificationCount
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $RepoPath)) { throw "RepoPath not found: $RepoPath" }

# --- Load every task package, validate, and confirm they share one baseline-ref --------
$tasks = @()
foreach ($tp in $TaskPath) {
    if (-not (Test-Path $tp)) { throw "TaskPath not found: $tp" }
    $baselineRefPath = Join-Path $tp "baseline-ref"
    $expectedFilesPath = Join-Path $tp "expected-files.txt"
    $hiddenTestsRoot = Join-Path $tp "hidden-tests"
    if (-not (Test-Path $baselineRefPath)) { throw "Missing baseline-ref in $tp" }
    if (-not (Test-Path $expectedFilesPath)) { throw "Missing expected-files.txt in $tp" }
    if (-not (Test-Path $hiddenTestsRoot)) { throw "Missing hidden-tests/ in $tp" }

    $tasks += [pscustomobject]@{
        TaskId         = Split-Path $tp -Leaf
        Path           = $tp
        BaselineRef    = (Get-Content $baselineRefPath -Raw).Trim()
        ExpectedFiles  = @(Get-Content $expectedFilesPath | Where-Object { $_.Trim() -ne "" } | ForEach-Object { $_.Trim() })
        HiddenTestsDir = $hiddenTestsRoot
    }
}

$distinctBaselines = @($tasks.BaselineRef | Select-Object -Unique)
if ($distinctBaselines.Count -gt 1) {
    throw "Tasks reference different baseline-refs ($($distinctBaselines -join ', ')) — they can't share one checkout. Grade them separately."
}
$baselineRef = $distinctBaselines[0]

# --- Step 1: precision, computed BEFORE hidden tests are copied in ---------------------
Push-Location $RepoPath
try {
    # stdout and stderr captured separately — merging them (2>&1) turns stderr lines
    # (e.g. git's CRLF warnings) into ErrorRecord objects mixed into the string
    # pipeline, breaking .Trim() below with no useful diagnostic.
    $diffOutput = git diff --name-only $baselineRef 2>$null
    if ($LASTEXITCODE -ne 0) {
        throw "git diff failed against baseline-ref '$baselineRef' (exit $LASTEXITCODE) — is $RepoPath a checkout of the right repo at/after that commit?"
    }
    $touchedFiles = @($diffOutput | Where-Object { $_.Trim() -ne "" })
}
finally {
    Pop-Location
}

$expectedFiles = @($tasks.ExpectedFiles | Select-Object -Unique)
$touchedSet = [System.Collections.Generic.HashSet[string]]::new([string[]]$touchedFiles, [System.StringComparer]::OrdinalIgnoreCase)
$expectedSet = [System.Collections.Generic.HashSet[string]]::new([string[]]$expectedFiles, [System.StringComparer]::OrdinalIgnoreCase)
$missingExpected = @($expectedFiles | Where-Object { -not $touchedSet.Contains($_) })
$extraTouched = @($touchedFiles | Where-Object { -not $expectedSet.Contains($_) })

# --- Step 2: copy every task's hidden tests into place, tracking class -> taskId -------
$classNameToTaskId = @{}
foreach ($task in $tasks) {
    $projectDirs = Get-ChildItem $task.HiddenTestsDir -Directory
    if ($projectDirs.Count -eq 0) {
        throw "hidden-tests/ has no <TestProject>/ subdirectories — nothing to copy for task $($task.TaskId)"
    }
    foreach ($projectDir in $projectDirs) {
        $destDir = Join-Path $RepoPath "tests" $projectDir.Name
        if (-not (Test-Path $destDir)) {
            throw "Target test project directory not found: $destDir (expected because $($task.TaskId)/hidden-tests/$($projectDir.Name)/ exists)"
        }
        Get-ChildItem $projectDir.FullName -Filter "*.cs" | ForEach-Object {
            Copy-Item $_.FullName -Destination $destDir -Force
            foreach ($m in [regex]::Matches((Get-Content $_.FullName -Raw), 'class\s+(\w+)')) {
                $classNameToTaskId[$m.Groups[1].Value] = $task.TaskId
            }
        }
    }
}

# --- Step 3: run the suite once, with TRX output for per-class attribution -------------
$trxDir = Join-Path ([System.IO.Path]::GetTempPath()) "eval-trx-$RunId"
if (Test-Path $trxDir) { Remove-Item -Recurse -Force $trxDir }
New-Item -ItemType Directory -Force -Path $trxDir | Out-Null

Push-Location $RepoPath
try {
    $testOutput = dotnet test --logger "trx" --results-directory $trxDir 2>&1 | Out-String
    $overallExitSuccess = ($LASTEXITCODE -eq 0)
}
finally {
    Pop-Location
}

# className (short, e.g. "DiscountRulesFixTests") -> list of outcome strings
$outcomesByClass = @{}
Get-ChildItem $trxDir -Filter "*.trx" -Recurse -ErrorAction SilentlyContinue | ForEach-Object {
    [xml]$trx = Get-Content $_.FullName -Raw
    $idToClass = @{}
    foreach ($unitTest in @($trx.TestRun.TestDefinitions.UnitTest)) {
        if ($unitTest -and $unitTest.TestMethod) {
            $shortClass = ($unitTest.TestMethod.className -split '\.')[-1]
            $idToClass[$unitTest.id] = $shortClass
        }
    }
    foreach ($result in @($trx.TestRun.Results.UnitTestResult)) {
        if (-not $result) { continue }
        $cls = $idToClass[$result.testId]
        if (-not $cls) { continue }
        if (-not $outcomesByClass.ContainsKey($cls)) { $outcomesByClass[$cls] = @() }
        $outcomesByClass[$cls] += $result.outcome
    }
}

# --- Step 4: attribute outcomes back to each task ---------------------------------------
$perTaskResults = @()
foreach ($task in $tasks) {
    $taskClasses = @($classNameToTaskId.GetEnumerator() | Where-Object { $_.Value -eq $task.TaskId } | ForEach-Object { $_.Key })
    $notRun = @($taskClasses | Where-Object { -not $outcomesByClass.ContainsKey($_) })
    $allOutcomes = @($taskClasses | Where-Object { $outcomesByClass.ContainsKey($_) } | ForEach-Object { $outcomesByClass[$_] } | ForEach-Object { $_ })
    $failed = @($allOutcomes | Where-Object { $_ -ne "Passed" })

    $taskSuccess = ($notRun.Count -eq 0) -and ($allOutcomes.Count -gt 0) -and ($failed.Count -eq 0)

    $perTaskResults += [ordered]@{
        taskId       = $task.TaskId
        success      = $taskSuccess
        testClasses  = $taskClasses
        testsPassed  = @($allOutcomes | Where-Object { $_ -eq "Passed" }).Count
        testsFailed  = $failed.Count
        classesNotRun = $notRun
    }
}

$success = ($perTaskResults.Count -gt 0) -and (-not ($perTaskResults | Where-Object { -not $_.success }))

# --- Step 5: emit result ----------------------------------------------------------------
$result = [ordered]@{
    runId              = $RunId
    arm                = $Arm
    taskIds            = @($tasks.TaskId)
    attemptNumber      = $AttemptNumber
    repoPath           = (Resolve-Path $RepoPath).Path
    success            = $success
    overallExitSuccess = $overallExitSuccess
    perTaskResults     = $perTaskResults
    wallClockSeconds   = $WallClockSeconds
    tokensIn           = $TokensIn
    tokensOut          = $TokensOut
    costUsd            = $CostUsd
    toolCallCount      = $ToolCallCount
    turnCount          = $TurnCount
    clarificationCount = $ClarificationCount
    filesTouched       = $touchedFiles
    filesExpected      = $expectedFiles
    filesMissing       = $missingExpected
    filesExtra         = $extraTouched
    gradedAt           = (Get-Date).ToUniversalTime().ToString("o")
}

$resultJson = $result | ConvertTo-Json -Compress -Depth 6
Add-Content -Path $ResultsFile -Value $resultJson

Write-Host "$(if ($success) { 'PASS' } else { 'PARTIAL/FAIL' }) — $($tasks.Count) task(s) ($Arm, run $RunId, attempt $AttemptNumber)"
foreach ($r in $perTaskResults) {
    $mark = if ($r.success) { "PASS" } else { "FAIL" }
    Write-Host "  [$mark] $($r.taskId) — passed $($r.testsPassed), failed $($r.testsFailed)$(if ($r.classesNotRun.Count -gt 0) { ", notRun: $($r.classesNotRun -join ',')" })"
}
Write-Host "  Files expected: $($expectedFiles.Count)  touched-and-expected: $($expectedFiles.Count - $missingExpected.Count)  missing: $($missingExpected.Count)  extra: $($extraTouched.Count)"
if (-not $success) {
    Write-Host "--- dotnet test output ---"
    Write-Host $testOutput
}

Remove-Item -Recurse -Force $trxDir -ErrorAction SilentlyContinue

exit ($(if ($success) { 0 } else { 1 }))
