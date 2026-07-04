# Harness comparison eval — Studio orchestrator vs. Claude Code, model-tiered vs. single-model

## Status

- [x] `eval-stub-small` scaffolded and committed — `../eval-stub-small` (sibling repo,
      standalone git history, baseline commit `771ee4c`). Builds clean, 7/7 tests pass.
      Four task anchors seeded and documented in its README.
- [x] `eval-stub-medium` scaffolded and committed — `../eval-stub-medium` (sibling repo,
      standalone git history, baseline commit `76d1bfa`). 4-project layered split
      (Contracts/Core/Storage/Host, ~50 files), builds clean, 20/20 tests pass across
      all three test projects. Four task anchors seeded and documented in its README —
      bug (discount stacking), feature add (missing Payments module across all layers),
      underspecified (tier-blind/unleased fulfillment), refactor (no designated target
      picked yet, unlike small's).
- [x] `eval-tasks/eval-stub-small/` fully packaged — all 4 anchors from `eval-stub-small`
      now have `task.md` + `baseline-ref` + `expected-files.txt` + `hidden-tests/*.cs`
      under `plans/eval-tasks/eval-stub-small/task-{01..04}-*/`. Hidden tests reference
      symbols (`LineItemKind.Subscription`, `OrderService.TryClaimForFulfillment`,
      `CreateOrderRequest.BuyerId`) that don't exist on the baseline yet by design —
      compile failure against an incomplete fix is treated as a failing grade, not just
      a runtime assertion failure. Dry-run verified: applied a correct fix for all four
      anchors directly in `eval-stub-small`, dropped in all four `hidden-tests/*.cs`,
      ran `dotnet test` — 16/16 pass. Reverted afterward (`git checkout --`); baseline
      commit `771ee4c` is unchanged and back to 7/7.
- [x] `eval-tasks/eval-stub-medium/` fully packaged — same convention as small, plus a
      `hidden-tests/<TestProject>/` subdirectory layer (medium has three test projects,
      not one) that small was retrofitted to match. Dry-run verified task-01 (discount
      stacking bug) and task-04 (rename) directly — 24/24 pass with both fixes + all
      four hidden test files in place. **Not** dry-run verified: task-02 (Payments
      module — full implementation would be substantial) and task-03 (fulfillment
      priority) — reviewed carefully for internal consistency but not executed against
      a real implementation. Treat those two as higher-risk of an oracle bug until
      someone actually implements and runs them once.
- [x] Verifying task-04 caught a real gap: the *existing baseline* test
      `InMemoryOrderRepositoryTests.cs` also constructs `Order { CustomerId = ... }`
      directly, so a correct rename has to touch it too or the solution won't compile.
      Added it to `expected-files.txt` after finding this by actually running the
      fix — a reminder that these packages need dry-run verification, not just
      inspection, to be trustworthy.
- [x] `eval-harness/grade-task.ps1` — generic grading script (any repo/task pair):
      computes file-scope precision via `git diff` against `baseline-ref` **before**
      copying hidden tests in (so the grading artifacts themselves never pollute the
      precision measurement), then copies hidden tests into their target test project,
      runs `dotnet test`, appends one JSON line per run to a results file. Tested against
      both a passing and a deliberately-failing run (including an out-of-scope file
      touch, correctly flagged as `filesExtra`).
- [x] `eval-harness/run-one.ps1` — drives Arms C/D/R1/R2 (Claude Code) end to end:
      clone at `baseline-ref` → invoke `claude -p ... --output-format json` → parse its
      own usage/cost → grade → prune build artifacts. Full plumbing verified end to end
      with a fake `claude` stub in place of the real CLI (see RUNBOOK.md) — caught and
      fixed two real bugs this way: (1) relative `-TaskPath`/`-StubRepo` broke after the
      script `Push-Location`s into the checkout (paths now resolved to absolute up
      front); (2) checkouts nested inside `nodalmerge-studio` inherited its NuGet
      Central Package Management config via MSBuild's directory-props walk-up, breaking
      the stub repo's own versioned `PackageReference`s — default checkout location
      moved to a sibling `eval-harness-runs/` outside `nodalmerge-studio` entirely.
      **Still unverified: the real `claude -p --output-format json` call itself** — no
      API calls made from this session. Confirm the exact output shape
      (`usage.input_tokens`/`output_tokens`/`total_cost_usd`/`num_turns`) against your
      installed CLI before trusting the parsed numbers.
- [x] Checkouts are persistent by design, not cleared after grading — with `bin`/`obj`
      pruned post-grade by default (`-KeepBuildArtifacts` to skip pruning). Rationale: a
      surprising result needs its actual diff inspectable afterward, not just a
      pass/fail line in `results.jsonl` — each result row now also records `repoPath`
      for exactly this.
- [x] **Workflow pivoted to manual-paste, all-4-tasks-combined-per-session** (per user
      decision) instead of headless-scripted, one-task-per-session. `grade-task.ps1`
      rewritten to accept multiple `-TaskPath` entries, run the suite once, and attribute
      pass/fail back to each task individually via TRX-parsed class-name matching — a
      combined session's partial completion is now visible (e.g. "3 of 4 tasks passed"),
      not collapsed into one boolean. Verified against real fixes: a clean 4/4 pass, and
      a build-failure case (`eval-stub-small`'s single test project means one broken
      hidden test fails the *whole* build, correctly reported as all 4 tasks `notRun` —
      real behavior, not a script bug; `eval-stub-medium`'s 3 separate test projects
      would show true per-project partial credit since MSBuild builds unaffected
      projects independently).
- [x] `eval-harness/prep-run.ps1` — the manual-workflow counterpart to `run-one.ps1`:
      clones at `baseline-ref`, builds one combined prompt from all of a repo's
      `task.md` files, injects arm-specific config (`.mcp.json` for C/D,
      `.claude/agents/studio-executor.md` for D/R2), writes `EVAL-PROMPT.md`, and stops
      — no `claude` invocation, no grading; you drive the session and grade afterward.
      Extended to also prep checkouts for Arms A/B (no MCP/subagent injection needed,
      since Studio's own orchestrator doesn't use either) so all 6 arms share one script.
- [x] **Real leak found and fixed before anything was prepped**: every `task.md` has a
      `## Grading` section naming the exact hidden test file(s) and sometimes their
      expected behavior. Both `prep-run.ps1` and `run-one.ps1` were dumping the *whole*
      `task.md` into the agent's prompt, handing it exactly what its hidden test checks.
      Both now regex-extract only `## Goal (as given to the agent)`, and throw rather
      than silently paste the full file if a `task.md` doesn't match the expected
      structure. Verified on real generated output for both repos — no leak.
- [x] **36 checkouts prepped for real**, then corrected down to a 4-arm design after a
      real discovery (see next item) — 2 repos × 4 arms (C/D/R1/R2) × 3 attempts, under
      `../../eval-harness-runs/<repo>/combined/<arm>/<runId>-attempt<N>/`. Each has a
      working baseline checkout + `EVAL-PROMPT.md` ready to paste. C/D checkouts have
      `.mcp.json` (port already configured on attempt-1 of all 4 repo×arm combos) and
      `.claude/settings.json` denying native file tools. **Nothing has been run yet —
      these are inputs, not results.** See `eval-harness/RUNBOOK.md`.
- [x] **Matrix corrected from 6 arms to 4 — Arms A/B dropped, C/D redefined.** Found by
      grepping `McpServerToolNames.cs` directly (not assumed from docs): Studio exposes
      two *separate* MCP surfaces — `nm_v1_*` (internal, used only by Studio's own
      `OrchestratorAgentLoop`, unreachable externally) and `nms_v1_*` (external, 13
      tools: `goal_run`/`goal_status`/`results_get`/`results_apply`/`repo_register`/etc.
      — a goal-*delegation* API, not a file-tools API). The original design assumed an
      external MCP client could do granular file edits through Studio's server the way
      its own agent loop does internally — not possible; there is no external
      `workspace_read`/`write`. That makes "Arm A/B via REST" and "Arm C/D via MCP" the
      same underlying mechanism (Studio's own orchestrator does 100% of the work either
      way), so A/B were dropped as redundant. C/D were redefined to mean exactly that
      delegation flow (`repo_register` → `goal_run` → poll `goal_status` →
      `results_get`/`results_apply`), with native file tools denied via
      `.claude/settings.json` so Claude Code can't fall back to editing directly. C and
      D now share an identical prompt — tiering for D is a Studio-side model-config
      setting active before the goal runs, not a Claude Code subagent, so the
      `studio-executor.md` subagent (built for the original, incorrect Arm D) is no
      longer used by any arm. All 12 C/D checkouts were patched in place to match
      (preserving the 4 `.mcp.json` ports already configured), and the 12 obsolete A/B
      checkouts were deleted.
- [ ] Actual comparison runs — **not executed, and shouldn't be from an unattended
      session**: this spends real API budget across N=3 × 4 arms × 8 tasks. In progress
      manually, one checkout at a time.

See `eval-harness/RUNBOOK.md` for exact per-arm commands. Everything below this point in
this file is still a plan, not a result — `eval-harness/` is the executable half.

## Goal

Answer two separate questions objectively (test scores, not subjective "this plan reads
better"):

1. **Harness mechanics** — holding tools and model strategy constant, how much is our
   hand-rolled `OrchestratorAgentLoop` losing to a mature harness (Claude Code) on cost,
   time, and success rate?
2. **Domain-integration value** — does routing through Studio's MCP tools
   (`nm_v1_workspace_*`, projections, file leasing, decision log) actually help versus a
   top-tier general harness working directly against the filesystem with no Studio
   awareness at all?

Question 1 is answered by a controlled 2×2. Question 2 is answered by reference arms
that are *not* statistically pooled with the 2×2, because they don't hold tooling
constant.

## Matrix

**Revised twice from the original 6-arm draft.** First: Arms A/B (Studio triggered via
REST) were dropped and C/D redefined to delegate via Studio's external MCP surface
(`nms_v1_*` — a goal-delegation API, not a file-tools API; there is no external
`nm_v1_workspace_read`/`write`). Second: once it was clear the MCP hop added nothing —
Studio's own orchestrator does 100% of the work either way, whether triggered by REST,
by MCP, or just by pasting into Studio's own goal field — the MCP indirection was
dropped too. Current 4-arm design:

| Arm | Harness | Tools | Model strategy |
|---|---|---|---|
| C | Studio's own `OrchestratorAgentLoop` | The combined prompt pasted directly into Studio's own goal field. No Claude Code, no MCP. | Single-model (Sonnet everywhere) — a Studio-side setting active before the goal runs |
| D | Same as C | Same as C | Tiered — Sonnet for Orchestrate/Plan, Haiku for Execute, via `AgentControlService.GetCredentialsForStage`. C and D use the **identical prompt**; the only difference is which Studio-side model config was active when the goal ran |
| R1 (reference) | Claude Code CLI, no MCP | Native `Read`/`Edit`/`Write`/`Bash` against a plain checkout | Single-model (Sonnet) |
| R2 (reference) | Claude Code CLI, no MCP | Native tools against a plain checkout | Tiered — main thread Sonnet, execution delegated to a subagent (`studio-executor-native.md`) pinned to Haiku |

C/D are the controlled pair for the harness-maturity question (same underlying engine —
Studio's own orchestrator — same tools, only the model strategy varies between C and D).
R1/R2 are the reference pair for raw Claude Code. The R1-vs-C / R2-vs-D comparison is
now the core of the eval: same model strategy, different harness. Since C/D no longer
involve Claude Code actually writing any code (it only delegates), the original
"domain-integration value" question (does routing through Studio's tools help a *mature
general harness*) isn't answerable by this matrix anymore — what's left is closer to
"Studio's own orchestrator vs. Claude Code's own harness," full stop, at each model
tier.

## Task substrate: synthetic stub repositories

Do **not** use the real Studio backlog as the task source. Two problems with that:

1. **Contamination risk** — Studio's own code and history may be in a model's context
   or (for widely-mirrored patterns) training data by the time this runs; a synthetic,
   never-published app has no chance of being pre-known.
2. **Confound with repo size/complexity** — Studio itself is large and layered. Running
   tasks straight against it means every arm also has to contend with Studio's own
   scale, which swamps the signal we actually want (harness mechanics, tiering). Better
   to control repo size directly with purpose-built stubs, sized specifically to expose
   the gaps identified earlier (prompt caching, compaction, multi-file search).

Build **two** stub repos, not one — they stress different things:

| Repo | Size | Purpose |
|---|---|---|
| `eval-stub-small` | 16 files, single project | Cheap, fast calibration — debug the eval harness itself (grading script, cost caps) before spending budget on real comparison runs. Also the floor case: does harness maturity even matter when context never gets large? |
| `eval-stub-medium` | ~53 files, layered (Contracts/Core/Storage/Host, mirrors Studio's own split) | The real comparison substrate. Large enough that an agent *must* search/navigate rather than read the whole repo into context. Smaller than the original 80–150 estimate — 53 real, working, tested files turned out to be enough to force real cross-project navigation without ballooning scaffolding effort; grow it further if a run shows agents are still just reading everything into context anyway. |

Suggested domain for both: a small order/task-processing service (e.g. "MiniLedger" — 
orders, line items, a pricing/discount rule engine, an async fulfillment worker) in
C#/.NET, matching Studio's own stack so `dotnet test`/`scripts/verify.ps1`-style grading
works unmodified and file-scope conventions transfer. `eval-stub-medium` reproduces the
same layering Studio uses (domain records / service layer / storage / API host / tests)
so multi-file navigation in the stub is representative of multi-file navigation in real
Studio work.

### Task types (apply across both repos, not just one)

Different task shapes stress different harness capabilities — don't only test bug fixes:

| Type | Stresses | Example |
|---|---|---|
| Localized bug fix | Baseline tool-use efficiency, not much planning | Off-by-one in discount calculation |
| Multi-file feature add | Planning quality, orchestration/fan-out value | Add a new line-item type touching domain + service + API + tests |
| Cross-cutting refactor | Context management over a long session — the compaction/caching gap specifically | Rename a pervasive concept across 15+ files |
| Underspecified requirement | Judgment quality — does the agent ask vs. guess | Goal statement deliberately omits an edge case (e.g. concurrent order updates) that a good implementation should surface a question or a defensible assumption about |

### Task packaging (per task)

```
eval-tasks/<repo>/<task-id>/
  task.md                       # goal statement exactly as given to the agent
  baseline-ref                  # git ref/tag for the starting commit
  expected-files.txt            # minimal expected diff scope (precision scoring only)
  hidden-tests/
    <TestProjectName>/*.cs      # held out — grade-task.ps1 copies each subdir's .cs
                                 # files into tests/<TestProjectName>/ at grading time
```

The `<TestProjectName>` subdirectory layer exists because `eval-stub-medium` has three
test projects (Core/Storage/Host), not one — a task's hidden tests may need to land in
more than one of them (e.g. a rename that spans a Core-testable property and a
Host-testable request DTO). `eval-stub-small` uses the same convention with a single
`hidden-tests/MiniLedger.Tests/` subdirectory, for consistency with `grade-task.ps1`.

- **Held-out acceptance check**: `hidden-tests/` is never present in the checkout the
  agent works from. Grading copies it in post-run and runs `dotnet test` /
  `scripts/verify.ps1`. Pass/fail is the primary success metric — plus a quick human
  check that the diff doesn't game the test (e.g., hardcoding expected output) rather
  than solving the underlying task.
- **Precision scoring**: `expected-files.txt` vs. actual diff — did the agent wander
  outside scope.
- Size label (S/M/L by expected file count) per task, independent of which stub repo
  it's in, so results can be sliced by task complexity as well as repo scale.

8–12 tasks total, spread across both repos and all four task types — not evenly, but
enough that each type has at least 2 tasks to average over.

Fresh checkout of the relevant stub repo per run — never run against a shared/live
workspace, so runs can't see each other's state or leftover artifacts.

## Metrics schema

One record per run, all machine-collected:

| Field | Source |
|---|---|
| `runId`, `arm`, `taskId`, `attemptNumber` | test harness |
| `success` (bool) | acceptance test result |
| `wallClockSeconds` | test harness (start/end of run) |
| `tokensIn`, `tokensOut`, `cacheReadTokens`, `cacheWriteTokens` | per-arm, see below |
| `costUsd` | computed from token counts + published per-model rates |
| `toolCallCount`, `turnCount` | per-arm, see below |
| `filesTouched` vs `filesExpected` | diff against task's minimal expected set |
| `clarificationCount` | count of `nms_v1_clarification_respond` calls (C/D) or equivalent stop-and-ask behavior (R1/R2) |

### Where each arm's data comes from

**Revised twice now** — first when the collector-endpoint idea turned out unnecessary
(Studio already tracks what's needed via REST), second when Arms A/B were dropped
entirely (see the nms_v1 discovery). Current picture:

- **Arms C/D**: Studio's own `OrchestratorAgentLoop` does all the work (Claude Code only
  delegates via `nms_v1_goal_run`), so its per-turn token usage is captured the same way
  it always was — `ConversationLogEntry.InputTokens`/`OutputTokens`/`Provider`/`Model`
  via `GET /studio/workunits/{id}/conversation-log`, plus routing-decision detail via
  `GET /studio/workunits/{id}/orchestration-events`. The `workUnitId` needed for both is
  returned by `nms_v1_goal_run` (it equals the `goalId`). No new code needed — this was
  true under the old A/B design too and remains true under C/D.
- **Arms R1/R2**: Claude Code's own reported usage (however you capture it manually, or
  via `--output-format json` if scripted through `run-one.ps1`) is the only source of
  truth for tokens/cost/turns — no Studio interaction happens in these arms at all.
- **All arms**: `filesTouched` comes from `git diff --name-only` against the fresh
  checkout, computed by `grade-task.ps1` uniformly regardless of arm.

All arms append to one local `results.jsonl` via `grade-task.ps1` — see
`eval-harness/RUNBOOK.md`. No collector server, no new Studio endpoint.

## Execution protocol

1. Fresh git checkout per run (no shared state between runs, including across arms).
2. **N = 3 repeats per (task × arm) cell minimum.** LLM agent runs are stochastic — a
   single run per cell is not a result, it's an anecdote. Report median and IQR, not
   just mean, given the small N.
3. Randomize run order across arms per task (don't run all of Arm C, then all of Arm D)
   to avoid any time-of-day / API load confound.
4. Hard wall-clock and cost caps per run (e.g., 20 min / $5), logged as a failure mode
   if hit — a runaway loop shouldn't silently inflate one arm's average.
5. Same task prompt text verbatim across all arms — no arm gets a friendlier or more
   detailed goal description than another.

## Analysis

- Primary: success rate per arm (pass/fail on held-out acceptance test), aggregated
  across tasks and by size label (S/M/L).
- Secondary, only on tasks where success rate is comparable: cost and wall-clock time
  per successful run (comparing efficiency on cost/time when quality is a wash is more
  informative than comparing on tasks where one arm just failed the tests).
- Precision: files touched vs. expected, as a proxy for scope creep / wandering.
- Report A vs. B and C vs. D as the "does tiering help, per harness" comparisons; A vs.
  C and B vs. D as the "does harness maturity matter, per model strategy" comparisons.
  Don't collapse these into one combined ranking — the 2×2 exists so each factor can be
  read independently.
- R1/R2 reported as a separate table: "general harness, no domain tools" vs. the best
  of A–D, to answer the domain-integration-value question.

## Out of scope / not doing

- VS Code extension as a separate arm — if it drives the same MCP server through the
  same agent loop as the CLI, it's not a new experimental condition, just a different
  UI on an existing cell.
- Any change to `nm_v1_*` tool behavior to make it "fairer" for one arm — the tools stay
  exactly as they are for real Studio usage; the eval adapts to them, not vice versa.
- Formal statistical significance testing — N is too small for that to mean much; this
  is a directional read, not a paper.

## Open items to confirm before running

- Dry-run verify `eval-stub-medium` task-02 (Payments module) and task-03 (fulfillment
  priority) against real implementations — only task-01 and task-04 have been proven
  out so far.
- Arm D's tiering config (Sonnet orchestrate/plan, Haiku execute) needs to actually be
  set on Studio before a D goal runs — the exact "Agent Topology per-stage-credentials"
  mechanism (`AgentControlService.GetCredentialsForStage`) wasn't traced to a specific
  UI/REST contract in this session. Confirm how you're setting it before trusting a D
  run actually used tiered credentials rather than silently falling back to single-model.
- Verify `run-one.ps1`'s assumptions about `claude -p --output-format json`'s exact
  output shape against your installed CLI version — untested from this session.
- Per-model rates to use for `costUsd` (pull from current published pricing at run time,
  don't hardcode a table that goes stale).
- Wall-clock/cost caps (proposed: 20 min / $5 per run) — tune if tasks turn out
  chunkier than expected. Not yet enforced anywhere in `run-one.ps1`.
- 8–12 tasks total across both repos was the plan's target; 8 exist today (4 per repo).
  Fine as a starting matrix — add more only if the initial runs show a task type is
  too noisy to trust with just 2 examples.

---

## Appendix A: tiered subagent config for Arm R2 (superseded for D)

Claude Code subagents can pin a model in frontmatter independent of the main thread.
`eval-harness/studio-executor-native.md` (no `tools:` restriction, since R2 has no MCP)
is used for this. Main-thread agent runs on Sonnet, plans, then delegates the execute
step via the `Task` tool to the subagent pinned to Haiku.

This mirrors what `AgentControlService.GetCredentialsForStage` does natively inside
Studio's own orchestrator — which is exactly why it's *not* needed for Arm D anymore.
D's tiering is that same Studio-side mechanism directly, with no Claude Code subagent
in the loop at all (C and D share one prompt; only Studio's active model config
differs). `eval-harness/studio-executor.md` (the `mcp__studio__*`-restricted variant)
was built for the original, incorrect Arm D design and is no longer used by any arm.
