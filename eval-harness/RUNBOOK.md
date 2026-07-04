# Harness comparison eval — runbook

Companion to [plans/harness-comparison-eval.md](../plans/harness-comparison-eval.md).
This is the "how to actually run it" doc.

**Chosen workflow: manual paste, all 4 tasks combined into one session per run, 4-arm
design.** Not headless scripting (`run-one.ps1` still exists for R1/R2 if you want it
later, but isn't the primary path) — you drive each session yourself.

- `R1` = raw Claude Code, single model (Sonnet)
- `R2` = raw Claude Code, tiered (Sonnet plans, Haiku executes via a subagent)
- `C`  = Studio's own orchestrator, single model — the combined prompt is pasted
  **directly into Studio's own goal field**. No Claude Code, no MCP at all.
- `D`  = same as C, but Studio's model config is set to **tiered** (Sonnet
  orchestrate/plan, Haiku execute) before the goal is submitted. C and D use the
  identical prompt — the model config is the only difference.

Arms A/B (Studio triggered via REST) were **dropped**, and MCP was tried for C/D and
then dropped too — see "How C/D settled on 'just paste into Studio' " below for why.

## Status — 32 checkouts prepped (4 arms × 2 repos × 3 attempts + 8 spares), nothing run yet

Under `../../eval-harness-runs/<repo>/combined/<arm>/<runId>-attempt<N>/` (sibling of
`nodalmerge-studio`, not nested inside it — see the CPM gotcha below). Each is a clean
clone at the repo's `baseline-ref` with `EVAL-PROMPT.md` ready to paste — identical
content for all four arms now. **Nothing has been run yet** — these are inputs, not
results.

| File | Status |
|---|---|
| `prep-run.ps1` | Used for the real batch above; rewritten twice (see below) as the C/D design settled. |
| `grade-task.ps1` | Tested — verified against a full 4/4 pass, a build-failure-cascades-to-all-4-`notRun` case (single-project repo), and precision (missing/extra file) detection, all on real fixes applied to real checkouts. |
| `run-one.ps1` | Available for R1/R2 headless scripting if wanted later. Not updated for the C/D redesign since C/D's manual workflow is the chosen path. |

## How C/D settled on "just paste into Studio" (two corrections, in order)

1. **First design (dropped): Arms A/B via REST vs. Arms C/D via Claude-Code-as-MCP-client.**
   Assumed Claude Code could do granular file edits through Studio's MCP server the way
   its own agent loop does internally. Wrong — confirmed by reading
   `McpServerToolNames.cs` directly: Studio exposes `nm_v1_*` (internal, used only by
   `OrchestratorAgentLoop`, unreachable externally) and a completely separate `nms_v1_*`
   surface (13 tools — `goal_run`, `goal_status`, `results_get`, `results_apply`,
   `repo_register`, etc.) for external callers. There's no external
   `workspace_read`/`write` — an MCP client can only hand a whole goal to Studio's own
   orchestrator and poll for results.
2. **Second design (also dropped): Claude Code as a remote trigger via `nms_v1_goal_run`.**
   Since the external MCP surface only supports delegating a whole goal, "Arms A/B via
   REST" and "Arms C/D via MCP" turned out to be the *same* underlying mechanism —
   Studio's own orchestrator does 100% of the work either way, whether triggered by a
   REST call or by Claude Code relaying `nms_v1_goal_run`/`goal_status`/`results_get`/
   `results_apply` on your behalf. Once that's true, the MCP hop adds nothing —
   Claude Code was just "a fancy way to set a goal." So: skip it. Paste the same
   combined prompt directly into Studio's own goal field instead.

Net result: C/D checkouts have no `.mcp.json`, no `.claude/settings.json`, no subagent
config — just a plain checkout + `EVAL-PROMPT.md`, identical in shape to R1/R2's.

## A leak that was caught and fixed before any checkout was prepped

Every `task.md` has a `## Grading` section naming the exact hidden test file(s) and
sometimes describing their expected behavior. Early versions of `prep-run.ps1` pasted
the *whole* `task.md` into the prompt, handing the agent exactly what its hidden test
checks. It now extracts only the `## Goal (as given to the agent)` section via regex,
and throws rather than silently paste the whole file if a `task.md` doesn't match the
expected structure. Verified on real output for both repos' task sets.

## Running a prepped checkout — Arms R1/R2

1. Open a checkout, e.g.
   `../../eval-harness-runs/eval-stub-small/combined/R1/eval-stub-small-R1-1-attempt1/`,
   in an interactive Claude Code window.
2. Paste `EVAL-PROMPT.md`'s contents as your first message.
3. Let it work through all 4 tasks with its own native tools.
4. Grade it (fill in that checkout's own `eval-manifest.json` taskPaths):

   ```powershell
   ./grade-task.ps1 -RepoPath '<checkout>' -Arm R1 -RunId eval-stub-small-R1-1 -AttemptNumber 1 `
     -ResultsFile .\results.jsonl -TaskPath @(<the 4 paths from eval-manifest.json's taskPaths>)
   ```

## Running a prepped checkout — Arms C/D

1. For Arm D specifically: make sure Studio's model config for this repo/goal is set to
   **tiered** (Sonnet orchestrate/plan, Haiku execute) *before* the goal runs — this is
   the only thing that distinguishes D from C. For Arm C, make sure it's single-model.
2. Register/open the checkout in Studio (however you normally point Studio at a repo).
3. Paste `EVAL-PROMPT.md`'s contents directly into Studio's own goal field and submit —
   no Claude Code, no MCP involved in this arm at all.
4. When Studio finishes, grade it the same way as R1/R2, with `-Arm C` or `-Arm D`.

## Preparing more checkouts

```powershell
# Arm R1 — raw Claude Code, single model, all 4 tasks in one session
./prep-run.ps1 -StubRepo ..\..\eval-stub-small -EvalTasksDir ..\plans\eval-tasks\eval-stub-small -Arm R1

# Arm R2 — raw Claude Code, tiered (Sonnet plans, Haiku executes via subagent)
./prep-run.ps1 -StubRepo ..\..\eval-stub-small -EvalTasksDir ..\plans\eval-tasks\eval-stub-small `
  -Arm R2 -SubagentConfigPath .\studio-executor-native.md

# Arm C — Studio's orchestrator, single model, goal pasted directly into Studio
./prep-run.ps1 -StubRepo ..\..\eval-stub-small -EvalTasksDir ..\plans\eval-tasks\eval-stub-small -Arm C

# Arm D — same prompt as C; only Studio's model config differs (set before submitting)
./prep-run.ps1 -StubRepo ..\..\eval-stub-small -EvalTasksDir ..\plans\eval-tasks\eval-stub-small -Arm D
```

`studio-executor.md` (the `mcp__studio__*`-restricted variant) is unused now — it was
built for an earlier, since-dropped Arm D design that routed through Claude Code and
MCP. Kept in this directory in case a future redesign needs it; not referenced by any
current arm. `studio-executor-native.md` is still used, by Arm R2.

## Checkouts are kept, not cleared — on purpose

Never auto-deleted. A surprising result (an arm that "passed" by gaming the test, or
"failed" on something that looks like a grading-script bug rather than a real miss)
needs to be inspectable after the fact, not just re-derivable from a pass/fail line in
`results.jsonl`. `git diff <baseline-ref>` inside any checkout shows exactly what
happened. `results.jsonl` (which records each run's `repoPath`) is the thing worth
keeping under version control — the checkouts themselves are regenerable at any time.

### Why checkouts can't live inside `nodalmerge-studio/`

Found by actually running this, not by inspection: cloning a stub repo *underneath*
`nodalmerge-studio` breaks its build. MSBuild walks up the directory tree from every
`.csproj` looking for `Directory.Build.props`/`Directory.Packages.props`, finds
`nodalmerge-studio`'s own Central Package Management config, and the stub repo's plain
`<PackageReference Include="..." Version="...">` entries collide with CPM's requirement
that versions live in a `PackageVersion` item instead (`NU1008`). Applies to **any**
nested `.NET` checkout under `nodalmerge-studio`.

## Results

All arms append to the same `results.jsonl` via `grade-task.ps1` — one JSON object per
run, matching `plans/harness-comparison-eval.md`'s Metrics schema plus grading-specific
fields (`taskIds`, `perTaskResults` with per-task pass/fail + notRun class detection,
`filesTouched`/`filesExpected`/`filesMissing`/`filesExtra`, `repoPath`). No separate
collector/database — append-only JSONL, read it with whatever you like (`jq`,
`ConvertFrom-Json`, a notebook) once enough runs have accumulated.
