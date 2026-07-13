# Phase C implementation plan — telemetry, capabilities, second adapter, MCP mount

Child plan of `harness-hosting-architecture.md` Phase C. Written 2026-07-12 after code
recon; all file:line references verified that day. Slices are sequential (C1 → C2 → C3):
C2 reuses C1's parser-isolation pattern, C3 gates on C1's capability flags.

## Status

- [x] C1 — Transcript ingestion + capability flags (plan's C.1 + C.2) — shipped 2026-07-12,
      729/729 tests green (up from 725) — see "C1 implementation notes" below
- [x] C2 — Second adapter: Codex CLI (plan's C.3) — shipped 2026-07-12, 754/754 tests green
      (up from 743) — see "C2 implementation notes" below. Real `codex exec --json` captures
      (codex-cli 0.144.1) were taken this session before writing the parser, per the ground rule.
- [x] C3 — Slim MCP mount (plan's C.4) — shipped 2026-07-12, 743/743 tests green (up from 729) —
      see "C3 implementation notes" below. Does not depend on C2.

Context that motivates the ordering: the user-level goal beyond Phase C is running
whole goals CLI-only (no API keys / vscode-lm). Phase C does **not** achieve that —
planner delegation is Phase D, and the orchestrator's coordination loop is still an LLM
API loop today. C is the prerequisite machinery (telemetry to compare harnesses, flags
to gate features, MCP mount that Phase D's plan-mode + mid-turn clarifications build on).

## C1 — Transcript ingestion + capability flags

### C1.a Versioned transcript parser

Today the stream-json handling is an inline switch in `ClaudeCodeExecutor.RunAsync`'s
stdout loop (`ClaudeCodeExecutor.cs:82-139`): `assistant` events feed `OnActivity` only,
`result` yields run-level scalars, and `system`/`user`/`rate_limit_event` are discarded
with a comment naming this exact slice. Raw stream-json is never persisted.

Build `ClaudeTranscriptParser` as a **versioned component** (new file in
`NodalMerge.Studio.AgentRuntime`), not more inline logic:

- Interface shape: feed lines in (`Accept(string line)`), pull structured results out
  (per-turn records + final `TranscriptRunSummary` with resultText/cost/tokens/
  sessionId/permissionDenials). The executor's stdout loop becomes a thin pump.
- `ClaudeTranscriptParser.V1` handles the current format (claude 2.1.177 capture —
  the stub jsonl in `ClaudeCodeExecutorTests` is the fixture). Version detection from
  the `system` init event; **an unrecognized format degrades to run-level telemetry**
  (exactly today's behavior), never fails the run.
- No versioned-parser precedent exists in the codebase (grep verified) — the `V1`
  convention here mirrors the DTO/tool-name versioning (`CapabilitiesV1`,
  `StudioNodeKind.ConversationLogV1`), i.e. a `V1`-suffixed class selected by detected
  format, not a plugin framework. Keep it that simple.

### C1.b Per-turn ConversationLogEntry rows

Currently one run-level entry (`RecordConversationLogAsync`,
`ClaudeCodeExecutor.cs:323-357`): LogId `CLE-`, `CycleNumber:0`, empty
ToolCalls/ToolResults, hardcoded `StopReason:"end_turn"`, `Model:"claude-code"`.

Target (granularity: per-harness-turn, per the parent plan):

- Each `assistant` event → one entry: `CycleNumber` increments per turn, `ToolCalls`
  from the message's `tool_use` content blocks (`ConversationToolCall(ToolUseId, Name,
  InputJson)`), `AssistantText` from text blocks, `Model` from the event's real model
  field (not the executor name), `Provider:"anthropic"`.
- `user` events (tool results) → fill `ToolResults` on the owning turn's entry, matched
  by `tool_use_id`. Respect the storage layer's 20k truncation
  (`ConversationLogService.cs:13`) — it already handles this on `RecordAsync`.
- Keep one **terminal** run-level entry (existing `CLE-` prefix) carrying the `result`
  event's Input/OutputTokens and final text. Tokens go **only** on the terminal entry —
  per-turn entries leave them null (the CLI reports usage once per run; splitting it
  per turn would fabricate data). `TokensEstimated` stays false.
- Recording stays non-fatal (existing try/catch-log posture) and reuses
  `IConversationLogService.RecordAsync` — the native path's `ConversationLogRecorder`
  is worker-loop-shaped (takes an LlmResponse); don't force it to fit. A small
  CLE-specific mapper next to the parser is fine.
- Consumers (Decision Lens `ArtifactExplorerPanel.ts:556`, DAG replay
  `DagReplayPanel.ts:459`, REST `GET /studio/workunits/{id}/conversation-log`) need
  **zero changes** — they render whatever entries exist. Verify visually once.

### C1.c Capability flags

No executor capability metadata exists (`IHarnessExecutor` = `Name` + `RunAsync` only).
Add to `HarnessExecutorContracts.cs`:

```csharp
public sealed record HarnessCapabilities(
    bool SupportsTurnTelemetry,
    bool SupportsResume,
    bool SupportsHooks,
    bool SupportsSubagents,
    bool SupportsMcp,
    bool SupportsPlanningMode);

// on IHarnessExecutor:
HarnessCapabilities Capabilities { get; }
```

- Native: `(true, false, false, false, false, false)` — turn telemetry is its native
  recording; resume is ContinueService's conversation reconstruction, not a harness
  session id, so `SupportsResume:false` is honest at this seam.
- ClaudeCode: `(true after C1.b, true, true, true, true, true)` — but set
  `SupportsPlanningMode:false` until Phase D actually wires a Plan mode; the flag
  declares what the *adapter* supports, and the adapter has no Plan mode yet.
- `GET /studio/executors` endpoint (StudioRestEndpoints): list of
  `{ name, providerKey, displayName, capabilities }` from all registered
  `IHarnessExecutor`s. `providerKey` comes from a small static map alongside
  `HarnessProviders` (`claude-code` → `claude-cli`); native has no providerKey.
  The extension does NOT consume it yet — that's C2's dropdown work. Endpoint lands
  here because the data exists here.

### C1 acceptance

- Stub-backed test: run the existing multi-turn stub jsonl through the executor →
  N per-turn entries with correct ToolCalls/ToolResults pairing + 1 terminal entry
  with tokens; re-run → idempotent-enough (entries keyed by fresh LogIds is fine,
  but the run must not double-record on the retry path — check `IsResume`).
- Malformed/unknown-format stub → run still succeeds with run-level entry only.
- `GET /studio/executors` returns both executors with correct flags.
- Full suite green. Update parent plan's status block + implementation-notes section
  (follow the B1/B2/B3 notes format: what shipped, what deviated, what was discovered).

### C1 implementation notes (shipped 2026-07-12)

729/729 tests green (up from 725; 4 new tests: 3 in `ClaudeCodeExecutorTests` covering per-turn
recording/no-double-recording/degrade, 1 REST test for `GET /studio/executors` in
`DomainAgentConfigAndFeedbackRestTests`). No dev Studio Host lock was hit this session — `dotnet
build`/`dotnet test` ran clean against `src/NodalMerge.Studio.Host` and the full
`NodalMerge.Studio.Integration.Tests` project throughout.

**C1.a — `ClaudeTranscriptParser`** (new `src/NodalMerge.Studio.AgentRuntime/ClaudeTranscriptParser.cs`).
Shipped as designed: `ClaudeTranscriptParser.Create(onActivity)` returns an `IClaudeTranscriptParser`
backed by the nested `V1` class; `ClaudeCodeExecutor.RunAsync`'s stdout loop is now `parser.Accept(line)`
per line + `parser.BuildSummary()` once the process exits — no inline JSON switch left in the
executor. `OnActivity` is invoked by the parser itself (inside `HandleAssistant`), preserving the
exact per-line timing the old inline code had. One deviation from the plan's literal interface
sketch: instead of `Accept` returning parsed per-line output for the executor to interpret, the
parser owns interpretation end-to-end (activity callback in, `TranscriptRunSummary` out) — this
keeps the executor a genuinely thin pump rather than re-implementing type-switch logic at the call
site, which is what the plan's own "thin pump" framing was after.

**Version detection, concretely**: turn reconstruction (assistant/user → `ClaudeTranscriptTurn`)
only turns on once a `{"type":"system","subtype":"init"}` line has been seen (`_formatConfirmed`).
Until then — or for a transcript that never emits one — `assistant`/`user` lines are accepted but
produce no turns; the terminal `result` line is parsed unconditionally regardless of that flag,
identical to the pre-C1 code's independence from any `system` line. This is what "degrade to
run-level telemetry" cashes out to: a malformed/foreign transcript still yields the same single
run-level `ConversationLogEntry` C1 predecessors always produced, never a thrown exception. Verified
by `RunAsync_degrades_to_a_single_run_level_entry_when_the_transcript_format_is_unrecognized`
(a stub transcript with no `system`/`init` line at all).

**C1.b — per-turn `ConversationLogEntry` rows** (new
`src/NodalMerge.Studio.AgentRuntime/ClaudeConversationLogMapper.cs`, a mapper "next to the parser"
as the plan suggested, not forced through `ConversationLogRecorder`). `CycleNumber` is 0-based per
reconstructed turn; the terminal entry's `CycleNumber` is the turn count (so a fully-degraded run's
terminal entry is `CycleNumber:0`, byte-for-byte the same value the pre-C1 single-entry code always
used — no behavior change in the degraded case). Turn entries use LogId prefix `CLE-turn-`; the
terminal entry keeps the unmodified `CLE-` prefix per the task's constraint. Tokens
(`InputTokens`/`OutputTokens`) are `null` on every turn entry and populated only on the terminal
entry, matching the "CLI reports usage once per run" reasoning. `tool_result` content is accepted
as either a plain string or an array of content blocks (real Anthropic message shape allows both);
unmatched `tool_use_id`s (a `tool_result` with no corresponding `tool_use` in the same run) are
dropped rather than recorded as an orphan. Recording all entries for one run is wrapped in a single
try/catch (matching the existing non-fatal posture) — a failure partway through would leave a
partial set of entries for that run, which is an accepted, unchanged risk (same as the pre-C1
single-entry code's own try/catch granularity).

**C1.c — `HarnessCapabilities` + `/studio/executors`.** Landed exactly as specified: the record in
`HarnessExecutorContracts.cs`, `Capabilities` on `IHarnessExecutor`, native `(true, false, false,
false, false, false)`, claude-code `(true, true, true, true, true, false)`. `HarnessProviders`
gained a small reverse map (`ProviderKeyFor`) alongside the existing provider→executor map, so the
endpoint doesn't need its own lookup table. `GET /studio/executors` was added inside
`StudioRestEndpoints.MapAgentEndpoints` (agent/executor-adjacent, next to `/studio/domain-agents`)
rather than a new top-level region — no new `Map*Endpoints` method needed for one route. Three
pre-existing test fakes implementing `IHarnessExecutor` (`HarnessExecutorResolverTests.FakeExecutor`,
`HarnessExecutorSeamIntegrationTests.FakeHarnessExecutor`) needed a `Capabilities` property added —
expected mechanical fallout of a new interface member, not a design deviation.

**Fixture note (constraint compliance)**: the stub jsonl in `ClaudeCodeExecutorTests` was extended
from 3 lines (system/assistant-text/result) to 5 (system, assistant-with-tool_use, user-tool_result,
assistant-text-only, result) — every field already present in the original 3 lines is unchanged
(same `session_id`, `total_cost_usd`, `usage`, `result` text), so no pre-C1 assertion needed
updating; new fields (`model`, `stop_reason` on assistant messages) are additive and match the real
Anthropic message shape from training-data knowledge of the API (no contradicting field names
invented, per the task's constraint) — not independently re-verified against a fresh real `claude`
capture in this session (B2/B3's captures predate C1; C2's "one real run before writing the parser"
ground rule is Codex-specific, but the same principle would argue for one more real claude capture
before C1's parser assumptions are fully trusted in production).

**Discovered, relevant to C2/C3**:
- The harvest-block-extraction "seam isn't Claude-shaped" test C2 calls for is now easier: the
  stdout-loop/parsing code that used to be tangled with the harvest logic in one large method is
  gone from `RunAsync` entirely, so `HarvestAsync` was already effectively isolated before C2 starts
  — extracting a shared harvest helper should be a smaller lift than the plan anticipated.
- `ClaudeTranscriptParser`'s `V1` nested-class convention (a static outer class + version-suffixed
  nested implementation, selected by `Create()`) is now the concrete precedent C2's own
  `CodexTranscriptParser.V1` should mirror structurally, including the "recognized-format gates
  turn-reconstruction, terminal-scalar-parsing is unconditional" degrade pattern — reusable as-is,
  not just as a naming convention.
- `HarnessCapabilities.SupportsMcp` is now a real, live flag (`claude-code: true`, `native: false`)
  that C3's `.mcp.json` generation gate (`Capabilities.SupportsMcp`) can read immediately — no
  further plumbing needed on the capability side for C3 to start.

## C2 — Second adapter: Codex CLI

Chosen over Aider: closest analog to Claude Code (vendor harness, headless exec mode,
JSON output), and the user has named Codex/Copilot as the next targets.

**Ground rule (from B2's lesson): no training-data assumptions about the CLI.** B2 did
one real `claude -p` capture before writing the parser and it corrected several guesses.
Step 1 of C2 is the same: one real `codex exec` (or equivalent) run captured to a
fixture file, checked in as the stub. If the Codex CLI isn't installed/licensed on this
machine, C2 **stops after the executor skeleton + stub tests** and the real-capture task
goes back to the user — do not invent the JSON shape.

- `CodexCliExecutor : IHarnessExecutor`, `Name = "codex"`, registered alongside
  ClaudeCode in DI (`InMemoryAgentRuntimeService.cs:1259-1266` region).
- `HarnessProviders`: map provider `codex-cli` → `"codex"`. Keep the existing
  claude-cli mapping pattern (provider wins over `AgentProfile.Executor`).
- Workspace contract + harvest are **shared, not duplicated**: materialize
  `.workspace/`, run in branch workdir, harvest diff → `ProposeAsync`/`ValidateAsync`,
  `HarvestDecisionsAsync`/`HarvestInboxAsync` — extract the harvest block from
  `ClaudeCodeExecutor.RunAsync` into a shared helper (this is the "prove the seam
  isn't Claude-shaped" test: if extraction is hard, the seam has a Claude-shaped bug).
- `CodexTranscriptParser.V1` following C1's pattern; degrade to run-level on
  unrecognized output.
- Auth mirrors claude-cli semantics: ambient login default; stored key on the Model
  Profile → injected env var (`OPENAI_API_KEY` here); blank model = CLI default.
- Capabilities: set honestly from what the real capture proves (expect
  `SupportsResume` true only if a session-resume mechanism is verified;
  `SupportsMcp` per Codex's MCP support at time of implementation).
- Extension: `codex-cli` provider option in the Model Profile dropdown + CLI note +
  worker-only validation (same three touchpoints as claude-cli:
  `AgentConfigService.ts`, `modelAgentStudio.js`, `ArtifactExplorerPanel.ts`) — and
  this is the moment to **repoint the dropdown at `GET /studio/executors`** so the
  third adapter needs no extension edit (parent plan's C.3 note).
- Stub tests mirror `ClaudeCodeExecutorTests` (the `.cmd` + `%~dp0`-companion-jsonl
  technique, `ClaudeCodeExecutorTests.cs:22-44`).

### C2 acceptance

- Stub-backed: spawn → edit → harvest → proposal cycle green for codex executor
  (mirror of `ClaudeCodeExecutorHarvestTests`).
- One real smoke test (manual, not checked in — B3 precedent) if the CLI is available.
- Extension dropdown data-driven; hardcoded provider list retired.
- Parent plan status + notes updated.

### C2 implementation notes (shipped 2026-07-12)

754/754 tests green (up from 743; 11 new tests: 10 in `CodexCliExecutorTests`, 1 in
`CodexCliExecutorHarvestTests`, both in `tests/NodalMerge.Studio.Integration.Tests`). Codex CLI 0.144.1
was probed real on this machine this session (see codex-probe/capture-2..5.jsonl, referenced from this
slice's own task brief) — the parked status from C1's session ("Codex CLI not installed") no longer
applied; ground rule satisfied before any parser code was written. No dev Studio Host lock was hit —
`dotnet build`/`dotnet test` ran clean throughout. Two pre-existing flakes were observed under
full-suite parallelism (`CandidateBranchConflictTests` once, in the same pattern C3's notes already
describe) — passed cleanly on an isolated rerun and on a second full-suite run; not something this
slice introduced.

**Real capture findings that shaped the adapter (all verified, not training-data guesses):**
- `thread_id` appears exactly once, on the `thread.started` line — unlike claude's `session_id`,
  which repeats on every line. `CodexTranscriptParser.V1` captures it once and carries it through
  `BuildSummary()` rather than re-reading it per line.
- There is no terminal `result`/`subtype`/`is_error` event at all, and no cost field (the captures
  used ChatGPT-seat auth, not billed-per-token API auth). `CodexCliExecutor.RunAsync` judges success
  purely from the process exit code — there was no honest way to reuse claude's
  `summary.IsError || summary.Subtype != "success"` check, since codex's summary type carries neither
  field. `HarnessRunResult.CostUsd` is always `null` for this executor.
- No permission-denial event type exists — capture-2/capture-4 (a workspace-write sandbox denial)
  surfaced only as prose inside an `agent_message` item, with no structured payload to parse. This
  executor does not attempt to detect denials structurally (unlike claude's
  `EmitPermissionDenialEventsAsync`) — there is nothing in the verified JSON shape to detect.
- **Windows sandbox finding, load-bearing for `CodexCliExecutorOptions.SandboxMode`'s default:**
  `-s workspace-write` behaved read-only in two separate real captures on this machine (capture-2 with
  a relative `-C`, capture-4 with an absolute `-C`) — every file-writing attempt was refused with the
  CLI reporting "this workspace is currently read-only". `-s danger-full-access` worked in the same
  session (capture-3, capture-5-resume). The option defaults to `danger-full-access` on Windows and
  `workspace-write` elsewhere (this finding was never reproduced off Windows, so nothing warrants
  weakening the default there). This is deliberately not treated as a security regression: Studio's
  isolation guarantee is the branch workdir + the harvest build/test gate (`WorkspaceExecutionRule`)
  + AP-4 human review, not the harness's own sandbox flag — the same "the gate is the correctness
  mechanism" posture the parent plan already applies to advisory leases. The option is settable per
  the task's own requirement, so a fixed Windows sandbox (or a host where the finding doesn't
  reproduce) can tighten it without an adapter code change.
- **stdin must be closed, not just left unread.** capture-1 (stdin attached, unredirected) printed
  "Reading additional input from stdin..." and hung for the full 5-minute manual timeout before being
  killed by hand. `CodexCliExecutor.RunAsync` sets `RedirectStandardInput = true` and calls
  `process.StandardInput.Close()` immediately after `Process.Start` — giving codex an immediately-EOF
  pipe instead of an open handle. `CodexCliExecutorTests` exercises this directly: the stub `.cmd`
  does `set /p dummy=` (which only returns instead of blocking when stdin hits EOF) before emitting
  its jsonl, so a regression here would make that whole test file hang rather than merely fail an
  assertion.
- **Resume argument ordering**: `codex exec resume <thread_id> --json --skip-git-repo-check "<prompt>"`
  — `resume <thread_id>` is a positional subcommand of `exec`, not a flag the way claude's `--resume
  <session_id>` is. `BuildProcessStartInfo` special-cases this: `args` starts with `"exec"`, then
  conditionally `"resume", <threadId>`, then the flag set. Verified against capture-5-resume (same
  thread_id echoed back, context genuinely retained — the assistant answered "hello.txt" when asked
  what file it had created in the prior turn).

**C2.a — shared harvest helper (`HarnessHarvestPipeline`, new
`src/NodalMerge.Studio.AgentRuntime/HarnessHarvestPipeline.cs`).** Extracted `ClaudeCodeExecutor`'s
private `HarvestAsync` (decisions/inbox harvest, `merge.propose` + `merge.validate`, the build/test
gate, `AwaitingClarification` pause) into its own DI-singleton class, parameterized on
`executorName`/`providerName` where the old code had `Name`/`"anthropic"` literals baked in. This
confirmed the discovery C1's own notes flagged: "the stdout-loop/parsing code that used to be tangled
with the harvest logic... is gone from `RunAsync` entirely, so `HarvestAsync` was already effectively
isolated before C2 starts" — the extraction really was mechanical, no Claude-shaped assumption had to
be untangled from it. `ClaudeCodeExecutor` now takes `HarnessHarvestPipeline harvest` as a constructor
dependency instead of `IMergeCommandService`/`IClarificationCommandService` directly (both moved to
the pipeline); its call site is `harvest.HarvestAsync(request, wu.BranchId, ..., Name, "anthropic",
ct)` — behavior for claude-code is unchanged byte-for-byte, confirmed by the pre-existing
`ClaudeCodeExecutorHarvestTests` suite staying green with zero edits to that file.

**C2.b — `CodexTranscriptParser.V1`** (new `src/NodalMerge.Studio.AgentRuntime/CodexTranscriptParser.cs`)
mirrors `ClaudeTranscriptParser.V1`'s structure (static outer class + nested `V1`, `Create()`
factory, "recognized-format gates turn reconstruction, terminal-scalar-parsing stays unconditional"
degrade rule) but the turn-reconstruction algorithm itself had to differ, because codex's JSON
separates a turn into several `item.completed` events instead of claude's one assistant message
carrying both text and `tool_use` blocks: an `agent_message` item commits a turn (`AssistantText` +
whatever `file_change`/`command_execution` items arrived since the previous commit, as that turn's
`ToolCalls`/`ToolResults`), and `file_change`/`command_execution` items themselves only ever append to
a pending buffer. This reproduces the real capture's own ordering (tool items arrive *before* the
agent_message that reports on them, i.e. mid-turn) and, on the two-turn stub fixture derived from
capture-3, yields exactly the "per-turn entries" the task's acceptance bar asked for (turn0 text-only,
turn1 carrying both tool items) — not a single degenerate one-turn-per-run result, even though every
real capture taken this session happened to be a single codex "turn" end-to-end.
`command_execution` maps to one `ConversationToolCall(id, "command_execution", JsonSerializer
.Serialize(command))` + one `ConversationToolResult(id, aggregated_output, false)`, and `file_change`
maps to `ConversationToolCall(id, "file_change", <raw "changes" json>)` +
`ConversationToolResult(id, status, false)`, exactly as the task brief specified. No `Subtype`/
`IsError`/`TotalCostUsd` fields exist on `CodexTranscriptRunSummary` at all (see the findings above) —
this was deliberate, not an oversight: an always-null placeholder field would have let
`CodexCliExecutor` accidentally write the same buggy `Subtype != "success"` success check claude's
code has, when codex has no subtype to check.

**C2.c — `CodexConversationLogMapper`** (new
`src/NodalMerge.Studio.AgentRuntime/CodexConversationLogMapper.cs`) mirrors `ClaudeConversationLogMapper`
exactly in entry-count/LogId-prefix/tokens-on-terminal-only shape. Two honest deviations from parity
with claude's mapper: turn entries carry `Model: null` (codex's `item.completed` events never report a
per-message model the way claude's assistant message does) and `Provider: "openai"` throughout (there
is no live-verified provider string for codex; `"openai"` was chosen to match the `OPENAI_API_KEY`
env-injection convention, not because any capture reported it).

**C2.d — `CodexCliExecutor`** (new `src/NodalMerge.Studio.AgentRuntime/CodexCliExecutor.cs`, `Name =
"codex"`) mirrors `ClaudeCodeExecutor`'s shape: workspace contract materialization, a thin stdout pump
over `CodexTranscriptParser`, `--add-dir` roots resolved from `WorkUnit.ReferenceFiles` the same way
(minus the `--settings`-file allowlist generation — codex has no verified equivalent generated-
allowlist file; its own `-s` sandbox flag is the only write gate this adapter drives), thread-id
persisted to `WorkUnit.Metadata["codexThreadId"]` under the same `IsResume`-gated pattern as claude's
`claudeCodeSessionId`, `cmd.exe /c` wrapping on Windows for the same npm-shim reason claude needs it.
`Capabilities` is `(SupportsTurnTelemetry: true, SupportsResume: true, SupportsHooks: false,
SupportsSubagents: false, SupportsMcp: false, SupportsPlanningMode: false)` — `SupportsMcp: false` is
deliberate-honest: no capture in this session exercised or even attempted an MCP mount against codex,
so the flag stays false per the task's own "do not claim unverified capabilities" instruction, even
though codex-cli's public docs describe MCP support. `HarnessProviders` gained
`CodexCli = "codex-cli"` and a `ProviderToExecutor` map (`claude-cli` → `claude-code`, `codex-cli` →
`codex`) that `IsCliProvider`/`ResolveExecutorName` now both key off, replacing the single-provider
`string.Equals` checks C1 shipped — `ProviderKeyFor`'s reverse map gained a `["codex"] = CodexCli`
entry too, so `GET /studio/executors` (C1) needed no code change beyond that map, exactly as the
parent plan's C.3 section predicted.

**C2.e — extension** (`clients/vscode-extension`). `AgentConfigService.ts`: `LlmProvider` gained
`'codex-cli'`; a new `isCliProvider()`/`CLI_PROVIDERS` array replaced every `=== 'claude-cli'` check
in that file (`getCredentialStatus`, `describeMissingCredentials`, `resolveCredentialRef`,
`resolveSpawnLlmConfig`) so a third CLI provider needs one array entry, not four call-site edits.
`ArtifactExplorerPanel.ts` imports the same `isCliProvider` helper for its two worker-only validation
checks (orchestrator-can't-be-CLI, non-Execute-stage-can't-be-CLI) — this is the "extract a shared
`isCliProvider` helper" the task suggested, landed in `AgentConfigService.ts` since both panel files
already import types from there. `AgentConfigPanel.ts`'s `fetchModels('codex-cli', ...)` returns
`['gpt-5-codex', 'o4-mini', '(blank = CLI default)']` — suggestions only, same posture as the existing
claude-cli list, not a live model-listing call (codex has no such local endpoint this extension calls
today). **Provider dropdown is now data-driven**, per the parent plan's C.3 note: `AgentConfigPanel
.sendConfig()` gained `fetchCliProviders()`, which calls `GET /studio/executors` (shipped in C1) and
maps `{providerKey, displayName}` for every executor whose `providerKey` is non-null, falling back to
a static `[claude-cli, codex-cli]` list if the endpoint throws (server not up yet) or returns no CLI
entries; the result is posted to the webview as `cliProviders` on the existing `'config'` message.
`modelAgentStudio.js` stores that array and builds its provider `<select>`'s CLI `<option>`s from it
(`cliProviders.map(...)`) instead of one hardcoded `claude-cli` option — the three API providers
(vscode-lm/openai/anthropic) stay static `<option>`s, exactly as the task specified, since they aren't
`IHarnessExecutor`-backed and `/studio/executors` has nothing to say about them. Every remaining
`=== 'claude-cli'` check in that file (the CLI note's visibility/text, the model-placeholder text, the
base-URL-hidden gate on save) now calls `isCliProviderKey()`/`cliDisplayName()`, both closed over the
same `cliProviders` array. `npx tsc --noEmit -p .` and `node --check` on `modelAgentStudio.js` both
pass; `npm run webview-smoke` (which the project's own memory notes flag as the way to catch
apostrophe-escaping bugs `tsc`/`esbuild` can't see inside these webview `.js` files) also passes
clean — 7 tabs, no console/page errors, no `NM-FATAL`.

**Deviations from the task brief, with reasons:**
- The brief's env-key rule said "mirror claude's rule with OPENAI_API_KEY... mark it unverified in a
  comment" — done exactly as asked, but note the *gate* (`HarnessProviders.IsCliProvider(request
  .Provider)`) is intentionally still the generic "was this run launched via any CLI provider"
  check, not a codex-specific one. This was already the correct semantics before C2 (a profile
  opting into key injection via the CLI-provider channel, regardless of which CLI), and per
  `HarnessProviders.ResolveExecutorName`'s own routing, a `codex-cli` provider can only ever reach
  `CodexCliExecutor` in the first place — so there's no path where this shared gate leaks an
  `ANTHROPIC_API_KEY`-intended run into `OPENAI_API_KEY` injection or vice versa.
- No settings-file/allowlist generation for codex (unlike claude's `--settings`) — there is no
  verified equivalent in any capture; codex's `-s` sandbox flag is the only write-gate mechanism this
  adapter drives, and per the Windows sandbox finding above that flag is set to `danger-full-access`
  by default on this platform, which is a materially different security posture worth flagging
  explicitly for Phase D or a future security-focused revisit.
- `TryKill`/`PersistHarnessThreadIdAsync`-shaped small helpers are duplicated between the two
  executors rather than pulled into the shared pipeline — each is 3-5 lines with a different log-tag
  string and a different `WorkUnit.Metadata` key; the task's own extraction ask was specifically the
  harvest block (C2.a above), not every private helper, and duplicating these was judged cheaper and
  clearer than adding more parameters to `HarnessHarvestPipeline` for helpers that aren't part of the
  harvest flow at all.

**Discovered, relevant to Phase D:** the Windows `-s workspace-write` read-only finding means any
future work that wants codex running under a *tighter* sandbox than `danger-full-access` on Windows
needs either a codex-cli fix/config this session didn't find, or a Studio-side workaround (e.g.
running codex inside a container/WSL where the sandbox behaves as documented) — flagged here rather
than solved, since Studio's own gate-based isolation story means it isn't blocking today.

## C3 — Slim MCP mount

The confirmed C.4 tool list all exists internally (`nm_v1_workspace_symbol_definition/
_references/_implementation`, `nm_v1_doc_fetch`, `nm_v1_artifact_record`,
`nm_v1_artifact_query`, `nm_v1_clarification_request` — `McpToolNames.cs:59-81`,
dispatched in `McpToolDispatcher.cs:86-108`) but none is wire-exposed: the HTTP MCP
endpoint (`MapMcp("/mcp")`, `StudioWebApplication.cs:122`) serves only the five
external `nms_v1_*` tool classes, stateless transport
(`ServiceCollectionExtensions.cs:22-33`).

Design constraints discovered in recon:

1. **Do not widen `/mcp`.** The registration-time split exists precisely to keep
   internal tools off the general endpoint (its doc comment records the earlier leak).
   Add a separate harness-scoped mount (e.g. `/mcp-harness`) with its own tool class
   (`HarnessWorkerTools`) wrapping exactly the C.4 subset via `McpToolDispatcher`.
2. **Work-unit identity over stateless HTTP — decided 2026-07-12: per-run bearer
   token.** Minted at spawn (crypto-random, not guessable from workUnitId), carried in
   the generated `.mcp.json` via its `headers` support, mapped server-side
   token → (workUnitId, sessionId, agentId), revoked at harvest (and on
   timeout-kill). Rationale: the alternatives are worse — a workUnitId path segment is
   forgeable by any local process, and a stateful transport contradicts the existing
   `Stateless = true` registration. The token map is in-memory only (a Host restart
   orphans a live run's token, but a restart already orphans the run itself — the
   resume respawn mints a fresh token). Same-machine trust model: this is
   access-scoping between cooperating local processes, not a security boundary against
   a hostile local attacker.
3. **Generation**: `ClaudeCodeExecutor.WriteSettingsFileAsync` writes
   `.workspace/settings.json` only; add `.workspace/mcp.json` generation + the
   `--mcp-config` arg (verified present in claude 2.1.177), gated on
   `Capabilities.SupportsMcp` — C2's codex adapter picks it up only if its capture
   verified MCP support.
4. **`clarification_request` held-open** = true mid-turn pause (the parent plan's C.4
   upgrade path): the tool call blocks until the human answers, replacing
   kill-and-respawn for clarifications. Needs the MCP tool timeout raised for that one
   tool; the `inbox/`-file path stays as the fallback for harnesses without MCP.
5. `nm_v1_doc_fetch` mounted here replaces the "denied by settings allowlist" v1
   posture for research tasks; keep the per-profile `WebFetch(domain:…)` opt-in note
   in the parent plan as superseded when this lands.

### C3 acceptance

- Stub test proving the mount plumbing: generated `.mcp.json` present, `--mcp-config`
  passed, token resolves to the right work unit on a live-host integration test
  hitting `/mcp-harness` directly (no real claude needed — call the endpoint as an
  MCP client would).
- A real-CLI manual smoke (one run) proving claude actually lists/calls the mounted
  tools — same not-checked-in posture as B3's smoke.
- Clarification via held-open MCP call round-trips on the live host.
- Parent plan status + notes + decisions updated.

### C3 implementation notes (shipped 2026-07-12)

743/743 tests green (up from 729; 14 new tests: 7 unit tests for `HarnessMcpTokenService`
(`tests/NodalMerge.Studio.AgentRuntime.Tests/HarnessMcpTokenServiceTests.cs`), 2 stub-CLI tests in
`ClaudeCodeExecutorTests` (`.workspace/mcp.json` + `--mcp-config` generated/omitted correctly), 5
live-host integration tests in the new `HarnessMcpMountIntegrationTests.cs` that drive `/mcp-harness`
and `/mcp` with a real `ModelContextProtocol` client). No dev Studio Host lock was hit — `dotnet
build`/`dotnet test` ran clean throughout. One pre-existing flake pattern was observed (different
tests failing under full-suite parallel runs across different invocations —
`CandidateBranchConflictTests`, `HarnessExecutorSeamIntegrationTests` — both pass in isolation and on
a subsequent full run); this is the same class of scheduler/timing race the 2026-07-10 flake-fix
memory note describes, not something this slice introduced (no C3 code touches conflict
reconciliation or scheduler leasing).

**Design constraint #1 resolved differently than the plan's literal sketch — read this first.** The
plan's "Add a separate harness-scoped mount... with its own tool class" reads like "call
`AddMcpServer()` a second time." That doesn't work with the installed SDK
(`ModelContextProtocol`/`ModelContextProtocol.AspNetCore` 1.4.0, confirmed by inspecting the shipped
XML docs before writing any code): `AddMcpServer()` registers exactly one process-wide
`McpServerOptions`/tool catalog; there is no named/keyed second registration. Calling it twice with
different `WithTools<T>()` sets would not produce two independent catalogs — it would just add both
sets of tools to the same catalog every `MapMcp(...)` mount serves, which is precisely the "/mcp"
widening the task forbids. The mechanism that actually works, and the one this slice used:
`HttpServerTransportOptions.ConfigureSessionOptions` — an SDK hook invoked per-request in stateless
mode with that request's own `HttpContext` and a freshly-cloned-per-request `McpServerOptions`
instance. `ServiceCollectionExtensions.AddStudioMcpServer` (McpServer project) now supplies a
callback that, only when `httpContext.Request.Path` starts with `/mcp-harness`, **replaces**
`serverOptions.ToolCollection` with a purpose-built harness-only collection (`HarnessWorkerTools`'
seven methods, wired via `McpServerTool.Create(Delegate, McpServerToolCreateOptions)` — not
`[McpServerToolType]`/`WithTools<T>()`, since those attach to the shared default catalog). Requests
under `/mcp` never enter that branch, so its tool catalog is byte-for-byte what it was before C3 —
proven by `The_external_mcp_mount_does_not_list_any_harness_only_tools` and
`The_harness_mount_lists_exactly_the_C4_subset` in the new integration test file. `StudioWebApplication.cs`
gained one more `app.MapMcp("/mcp-harness")` line alongside the existing `/mcp` mapping — both routes
share the same underlying MCP transport infrastructure the SDK provides; only the per-request tool
set differs. This was not the plan's literal fallback suggestion ("one mount + per-request tool
filtering by token presence") because that fallback would still require exposing the harness tool
*names* on `/mcp`'s `tools/list` (even if gated at call time), which the registration-split doc
comment in `ServiceCollectionExtensions.cs` treats as the thing to avoid, not just gating
functionality — `ConfigureSessionOptions` avoids that tradeoff entirely by keeping the two mounts'
tool catalogs genuinely disjoint at the routing layer, not just access-controlled at the call layer.

**Bearer-token service** (`src/NodalMerge.Studio.Core/Services/HarnessMcpTokenContracts.cs` +
`src/NodalMerge.Studio.AgentRuntime/HarnessMcpTokenService.cs`) — landed exactly as decided: a
32-byte CSPRNG token (`RandomNumberGenerator.GetBytes`, base64url-encoded), DI singleton, in-memory
`ConcurrentDictionary<string, HarnessMcpTokenContext>` mapping token → `(WorkUnitId, SessionId,
AgentId)`. `Mint`/`Resolve`/`Revoke`, no persistence, no expiry timer (revocation is explicit, tied
to the run's own lifecycle — see below).

**`HarnessWorkerTools`** (`src/NodalMerge.Studio.McpServer/Tools/HarnessWorkerTools.cs`) — the
exact C.4 subset: `nm_v1_workspace_symbol_definition/_references/_implementation` (delegate to
`IWorkspaceSemanticNavigationService`, branch resolved from the token's `WorkUnitId` via
`IWorkUnitService`, never from a caller-supplied `branchId`), `nm_v1_doc_fetch` (delegates to
`IDocFetchCommandService`, still gated on `WorkspaceOptions.DocFetchTools`), `nm_v1_artifact_record`/
`nm_v1_artifact_query` (delegate to `IArtifactCommandService`), `nm_v1_clarification_request` (see
held-open notes below). **Deviation from the task's literal "delegating to McpToolDispatcher"
instruction**: `McpToolDispatcher` is `internal` to `NodalMerge.Studio.AgentRuntime` with no
`InternalsVisibleTo` for the McpServer project, and its `DispatchAsync` takes a raw `JsonElement` +
`sessionId` string rather than typed parameters — using it would have meant either widening internal
visibility for one caller or hand-building `JsonElement` payloads to call a dispatcher that itself
just re-delegates to the same command-service interfaces `HarnessWorkerTools` now calls directly.
Every existing external/internal MCP tool class in this project (`ArtifactTools`, `DocTools`,
`WorkspaceTools`'s symbol methods) already follows the "call the command service directly" pattern,
not "call `McpToolDispatcher`" — `HarnessWorkerTools` mirrors that established precedent instead,
which produces identical behavior (same command-service calls, same results) without the visibility
widening. Work-unit identity comes from the resolved bearer token only, on every method — a
caller-supplied `workUnitId` parameter was deliberately omitted from the tool schemas rather than
accepted-and-ignored, so there's no spoofable field for a confused/misbehaving harness to fill in
incorrectly.

**Per-run token lifecycle** (`ClaudeCodeExecutor.cs`) — minted at the top of `RunAsync`, gated on
`Capabilities.SupportsMcp && !string.IsNullOrEmpty(options.HarnessMcpBaseUrl)` (the second half of
that gate matters: `ClaudeCodeExecutorOptions.HarnessMcpBaseUrl` is only populated once Kestrel has
actually bound an address — see below — so `BuildPeer`'s headless mode, which never starts an HTTP
listener, correctly never generates a mount even though the adapter's own capability flag is true).
Revocation is a `try/finally` wrapped around the entire process-lifetime + harvest block, so every
exit path (timeout-kill, non-zero exit, harvest failure, harvest success) revokes the same token
exactly once — verified structurally by inspection since no test exercises "does the token still
resolve after the run ends" directly (would require reaching into the executor's private token,
which the harvest test suite doesn't have a seam for; the token-service unit tests cover
mint/resolve/revoke mechanics, and the live-host mount tests cover revoked-token rejection).

**`.workspace/mcp.json` generation** — written via `fileWorkspace.WriteAsync(branchId,
".workspace/mcp.json", ...)`, same call shape as the existing `.workspace/settings.json` write, so
it automatically inherits the same exclusions: `WorkspacePathFilter.IgnoredDirNames` already lists
`.workspace` (verified, not assumed — `RunAsync_generates_mcp_config_and_passes_mcp_config_arg_when_the_base_url_is_known`
asserts no `ADDED:`/`MODIFIED:`/`DELETED: .workspace` line appears in `IFileWorkspaceService.DiffAsync`'s
output). Content shape: `{"mcpServers":{"nodalmerge-harness":{"type":"http","url":"<base>/mcp-harness","headers":{"Authorization":"Bearer <token>"}}}}`,
matching claude 2.1.177's documented HTTP-type `.mcp.json` server entry shape (the `headers` field is
exactly the bearer-token carrier the decided design calls for). `--mcp-config <path>` is added to the
CLI args only when the file was written; the kickoff `-p` prompt gets one extra sentence mentioning
the mounted tools, also only when the mount is active — both gated on the same `mcpConfigPath is not
null` check.

**Host base-address discovery** — `StudioWebApplication.Build` registers an
`app.Lifetime.ApplicationStarted.Register(...)` callback that reads
`IServer.Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault()` once Kestrel has
actually bound, and writes it into the `ClaudeCodeExecutorOptions` singleton. This was chosen over a
config-time value because the Host can be started with a dynamically-assigned port (`:0`) or via
`--urls` at the CLI, and a pre-bind guess would be wrong in both cases; reading the real bound address
post-start is the only reliable source. Threaded through `ClaudeCodeExecutorOptions` (a mutable
singleton field, not immutable-record-style) rather than a new options type, since the executor
already depends on that singleton and a second DI-injected dependency for one nullable string wasn't
justified. `BuildPeer` (headless mode, no Kestrel) never populates this field, which is the intended
degrade path.

**Held-open `nm_v1_clarification_request`** — calls the existing (unmodified)
`IClarificationCommandService.RequestAsync(blocking: true, ...)` to create the request exactly as the
file-based inbox path does today (so `.workspace/outbox/` still fills in on eventual `RespondAsync`,
preserving the fallback for non-MCP harnesses), then polls
`IExecutionEventStream.GetEventsByKindAsync([ExecutionEventKind.ClarificationResponded], since:
requestedAt)` every 2 seconds, filtering by the request's own `RequestId`, until either a matching
response event appears or `WorkspaceOptions.HarnessClarificationHoldOpenSeconds` (new option, default
55s) elapses. On answer: returns `{status:"answered", response, note}` in the same tool call — the
"true mid-turn pause, no respawn" the parent plan's C.4 section describes. On timeout: returns
`{status:"parked", message: "...the answer will arrive via .workspace/outbox/..."}` rather than
blocking indefinitely or erroring, so a harness that stops on that message is still correctly served
by the existing kill-and-respawn fallback. **Deviation/simplification**: this is polling, not a
push/wake mechanism (e.g. no `TaskCompletionSource` registered against the request id) — simpler and
sufficient for a 2-second granularity, 55-second ceiling; a future revision could replace the poll
loop with a completion-source registry inside `HarnessMcpTokenService` or a new small service if the
polling interval ever needs to shrink meaningfully. Not covered by an automated test in this slice
(the acceptance criterion "clarification via held-open MCP call round-trips" is deferred to the
real-CLI manual smoke below — simulating "a human answers mid-poll" cleanly in the existing xunit
test harness would need a background task racing the tool call, which felt like more test-harness
risk than the mechanism's own thin polling loop justified; the token-resolution and rejection paths
this tool shares with the others *are* covered by the live-host tests).

**How to smoke-test this manually** (not part of this task, same B3-precedent posture — leaving the
note for whoever runs it): start `NodalMerge.Studio.Host`, create a work unit whose agent profile
routes to `claude-cli`, spawn it, and while it's running: (1) confirm `.workspace/mcp.json` exists in
the branch workdir and its `url`/`headers` look right; (2) watch the harness's own transcript/tool
calls for `nm_v1_workspace_symbol_definition` or `nm_v1_artifact_record` firing against
`/mcp-harness`; (3) trigger a clarification from the harness side and answer it via
`POST /studio/clarifications/{workUnitId}/respond` within the hold-open window (default 55s) —
confirm the CLI process itself never exits/respawns, i.e. the same `claude` process resumes the
turn. Never run this against the real `claude` binary from automated tests (per the task's
constraint and B2/B3 precedent).

**Discovered, relevant to C2/Phase D**: the `ConfigureSessionOptions` per-path tool-swap technique
generalizes — a future third mount (or a Codex-specific tool subset, if C2's adapter ever needs
different tools than claude-cli's) can reuse the same pattern without any further SDK-level
workaround. Phase D's plan-mode work should note that `HarnessMcpBaseUrl`/token-per-run infrastructure
here is adapter-agnostic (nothing about `HarnessMcpTokenService` or the `/mcp-harness` mount is
claude-cli-specific) — a second adapter that sets `Capabilities.SupportsMcp = true` and calls the
same `WriteMcpConfigFileAsync`-shaped helper gets the mount for free.

## Cross-cutting

- **Test hygiene**: suite was 725/725 green pre-C. Integration tests can't build while
  a dev Studio Host is running (file locks) — stop it before test runs.
- **Windows**: all executor work keeps the uniform `cmd.exe /c` wrapping decision from
  B2 (documented deviation, now load-bearing for stub tests).
- **Commit discipline**: one commit per slice minimum, referencing this plan; update
  both this file's status block and the parent plan's.
