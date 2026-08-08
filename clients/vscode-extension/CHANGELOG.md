# Changelog

## 0.1.18 — 2026-08-08

Extension-only release — the bundled NodalMerge runtime is unchanged from 0.1.16.
This entry supersedes the 0.1.17 build, which was documentation-only and shipped without
its own changelog. Everything here is in Model & Agent Studio → Model Profiles.

- **The Model dropdown is fetched from the provider now, not hardcoded.** Picking
  **Anthropic API** lists the models your key can actually reach (via the Models API), so
  newly released models show up without waiting for an extension update and retired ones stop
  being offered. It uses the key you've typed *or* the one already stored for the profile, and
  falls back to a suggestion list when no key is available yet. **OpenAI compatible** already
  listed live models but only when you'd filled in a Base URL — leaving it blank (meaning
  "the OpenAI default") produced an empty dropdown; it now resolves the default and lists
  normally. CLI providers (**Claude Code**, **Codex**) remain curated suggestions: those
  binaries expose no model-listing command, and blank still means "use the CLI's own default".
- **VS Code LM profiles now use the model you picked.** The dropdown lists model *ids*, but
  the proxy resolved them against the model *family* — a different field — so a specific pick
  usually matched nothing and quietly fell through to whichever model happened to be first in
  VS Code's list. That fallback was easy to miss because it only surfaced in the "NodalMerge LM
  Proxy" output channel. Selection now matches on id, falling back to family for a hand-typed
  family name, and only then to any-available.
- **Store Key works while creating a profile.** On the Add Profile form the button did nothing
  at all — no key stored, no error — because the profile didn't exist in settings yet; the key
  only stuck if you saved, reopened the row, and stored it there. It now stores immediately and
  the profile keeps the key when you save. Remove Key cleans up correctly in that same
  pre-save window, and storing a key re-lists the models right away instead of leaving the
  dropdown on the no-key fallback until a save-and-reopen.
- Model suggestions refreshed to current ids (`claude-opus-5`, `claude-sonnet-5`), replacing
  the retired `claude-3-5-*` entries.

## 0.1.16 — 2026-07-22

Ships an updated NodalMerge runtime — the features below are runtime behavior the
extension surfaces, so a clean install (superseding any 0.1.15 build) is needed to get them.

- **Recursive planning — let a planner decompose a slice instead of forcing it into one
  worker.** A plan slice can now be marked *compound*, which hands it to its own sub-planner
  that re-slices it into a smaller sub-plan; the grandchildren fan out, execute, and
  reconcile bottom-up through the interior node back to the goal — no different from a flat
  fan-out, just deeper. It's gated by a **Max plan depth** setting (Goal Workspace → ⚙
  Settings → Planning): `1` is the previous flat-and-wide behavior unchanged, `2+` lets the
  planner go recursive up to that many layers. Each goal can override the global default from
  the **Plan depth** control in the Goal Workspace's Review/Target row — pre-filled with the
  default, change it to steer just that run. Use it for goals that are really several
  subsystems (e.g. "a React UI, a .NET API, and the services behind it") so each piece gets
  its own focused planning pass instead of one over-wide slice list.
- **Peer contracts keep parallel slices coherent.** When a planner splits work into peers that
  must agree on an interface (a backend endpoint and the frontend that calls it), it can now
  author a small shared *contract* and mark which slices *provide* vs *consume* it. The
  contract is injected into both workers so they build against the same shape, and into the
  reviewer so a non-conformant peer is rejected rather than silently merged — closing a gap
  where two slices over non-overlapping files could disagree on the interface and still merge
  clean.
- **Agent auto-review is reliable now on repository-linked goals.** Setting **Agent Approval**
  (or **Hybrid**) on both the goal and workspace review policy previously left fan-out
  children stranded at the review gate on a real repo — you had to Accept then Apply each one
  by hand. Two fixes land it: children on a repo-linked goal are once again governed by their
  inherited task review policy (an inherited repository id was misrouting them to a
  human-required default), and multi-child reconciliation now waits for each self-approving
  child to finish merging before folding it in, so a child's own auto-apply is never
  superseded out from under it. Children now review and merge on their own, staggered as they
  finish.
- **Max auto-retries is now a setting.** Goal Workspace → ⚙ Settings → **Max auto-retries**
  caps how many automated attempts (review-rejection revises and worker failures both) a work
  unit gets before it dead-letters for a human. `1` = one shot then escalate; higher = more
  lenient auto-fixing. Human/explicit retries are never capped.
- Version bumped to 0.1.16 so a clean install supersedes any older build still registered in
  VS Code.

## 0.1.15 — 2026-07-20

Extension-only release — the bundled NodalMerge runtime is unchanged from 0.1.13.
This entry also covers the 0.1.14 build, which shipped without its own changelog.

- **Re-link a repository, from the panel.** When Studio can't match your open folder to
  a registered repository — or matches it to the wrong one — you can now fix it without
  leaving the editor. The Artifact Explorer shows which repository room you're in and adds
  a **Re-link** action that offers an automatic re-link when the match is unambiguous and a
  manual picker otherwise. Opening a brand-new folder that was never registered can register
  it in place, and re-linking to a *new* repository (splitting one that was already linked)
  now warns — with a modal confirmation — when doing so would orphan content in the old room.
  Folder changes are watched, so switching workspaces offers a host restart instead of
  silently drifting.
- **Constraints are advisory, not a wall.** The agent reviewer no longer rejects finished,
  working code just because it departs from a recorded constraint. A justified departure is
  surfaced as a reviewer attestation on the result — the goal completes and the departure is
  called out — rather than throwing away work the user already paid for. Constraints remain
  guidance the pipeline respects; they no longer hard-block it.
- **Knowledge scope: manage constraints across repos and the workgroup.** The Insights view
  gains a **Constraints** sub-tab where each constraint has a local on/off toggle. You can
  add a constraint by hand with an explicit 2×2 scope — Local vs. Workgroup × this repository
  vs. all repositories — and promote a constraint discovered in one goal's lineage to global
  policy so it applies everywhere. Finding-promotion destinations and constraint re-scoping
  are clearer about where a promoted item will live.
- **Run the Insights scan through a CLI profile.** Detecting findings with an LLM can now go
  through a one-shot Claude or Codex CLI completer, not just an API-key provider, so a
  `claude-cli`/`codex` Model Profile works here too. The scan gives clearer progress feedback
  and, when a model returns something that doesn't parse, surfaces the raw output instead of
  failing silently.
- **File-scoped agents can run on their own model.** A file-scoped Execute or Plan Agent
  Profile can bind its own Model Profile, so a domain worker (e.g. everything under `src/db`)
  runs on the LLM you choose for it rather than the goal default.
- **Fixes.** The Pathways live view reconnects correctly inside the webview (localhost access
  is granted via port mapping). The host-readiness timeout is raised from 15s to 120s, so a
  slow cold first launch (or a fresh build) no longer times out before the host is up.
- Version bumped to 0.1.15 so a clean install supersedes any older build still registered in
  VS Code.

## 0.1.13 — 2026-07-18

- **Bundled runtime updated to the published NodalMerge host 0.2.4.** Fixes the
  snapshot-on-mutation storm: the room server was re-serializing the entire room to a
  full hydrate snapshot on every applied peer mutation, so seeding or heavily mutating a
  large room degraded to O(n²) (the symptom behind the two-machine test's disconnects and
  the peer `studio` room snapshot growing ~100 KB/mutation). The full-room checkpoint is
  now debounced to at-most-once per window (default 200 mutations or 30 s), with a flush on
  peer disconnect; the incremental packs remain the authoritative log, so hydration is
  unchanged. No Studio-side behavior change beyond the runtime it spawns.

## 0.1.12 — 2026-07-17

- **Bundled runtime updated to the published NodalMerge host 0.2.3.** The Studio
  host the extension ships and spawns now builds on the 0.2.3 CAS/blob runtime
  (restored from nuget.org), which brings several server-side correctness fixes
  that matter most for multi-peer and blob-heavy workspaces:
  - Safer blob-layout migration — the legacy→v2 dedupe pass now verifies the
    destination before dropping a duplicate and repairs a corrupt copy from the
    already-verified bytes, and the completion marker is written only after a
    fully clean pass (a transient failure is retried instead of stranding a file).
  - Inbound pack notifications no longer run on the WebSocket receive loop, so a
    slow domain observer can no longer stall frame processing (and relay) on a
    connection.
  - The legacy `/sync/blob-url` route is restored to its original response shape
    and status codes (an internal refactor had silently changed them).
  - Blob options fail-fast validation, plus a reverse-proxy path-prefix fix for
    chained remote blob origins.
- **Multi-user room server, documented end-to-end.** Running one Studio host as a
  shared room server — so people on different machines collaborate in the same
  room, each keeping their own local runtime and workspace — now has a
  step-by-step guide: server bind (`--Studio:Urls=http://0.0.0.0:<port>`),
  firewall, the client settings (`nodalmerge.room.hostUri`,
  `nodalmerge.room.workgroup`, `nodalmerge.blobOrigin.uri`), and how to apply them
  (edit settings → **NodalMerge: Restart Studio Host**, no window reload needed).
  See the "Multi-user room server" guide in the docs.
- Version bump to 0.1.12 so a clean install supersedes any older builds still
  registered in VS Code.

## 0.1.11 — 2026-07-14

- **Claude CLI and API-key harnesses are at parity.** Any role — planner, worker,
  or reviewer — can run through a Claude CLI (or Codex) Model Profile or an
  API-key provider, with equivalent behavior across the whole pipeline:
  execution, agent review, and reconciliation. A `claude-cli` Model Profile
  carries the profile's model and uses ambient CLI login by default; storing a
  key on that profile opts into key-based auth (e.g. for overages), and leaving
  it blank keeps ambient auth.
- **The orchestrator is now a deterministic service, not an LLM.** The per-goal
  orchestration tail (fan-out, reconcile, reviewer enqueue, completion) runs as
  a pure service with no orchestrator LLM calls — faster and free. The
  Orchestrator role is now the goal's Default profile, and Reinvoke no longer
  needs credentials re-supplied.
- **Remove Key button.** Model & Agent Studio can now clear a profile's stored
  API key: it deletes the secret, clears the setting, and evicts the running
  host's cached credential so swapping an api-key profile to a CLI profile takes
  effect immediately — no host restart. Previously the only way to remove a key
  was editing `settings.json`, and the host kept the old key cached until
  restart. The button is always available (works on a fresh add and right after
  Store Key), and also clears an unsaved key you typed but changed your mind on.
- **Agent review on any configured harness.** A reviewer agent can run on a CLI
  provider or an API key. Under `Agent Approval` an approved real-repo proposal
  auto-applies to merged; under `Hybrid` it keeps its human-override countdown
  (an agent approval no longer merges instantly, so the override window works).
  A reviewer that wrote its verdict from a nested directory it `cd`'d into is now
  recovered instead of stalling the review.
- **Clarifications are never invisible.** A blocking question raised by an agent
  with no live session (or on a goal-only session) now surfaces via a synthetic
  session fallback with threaded session ids, instead of silently disappearing.
- **Fixes.**
  - Planning-context isolation: `EngineeringState` facts no longer leak across
    unrelated goals (which had been corrupting planners' view of the codebase).
  - A configured worker/Execute-stage profile is now honored on single-file
    ("atomic") goals — the no-plan fast path was reusing the planner's model.
  - Merged multi-repo work units are evictable again — their branch directories
    no longer leak (the apply-time snapshot vs. status-update ordering defeated
    the old eviction check).
  - CLI-provider goals with a blank key no longer park "awaiting credentials"
    forever; restart credential reconstruction, previously-silent stalled runs,
    and unread CLI stderr are all surfaced now.
  - CLI harness conversation-log entries record the correct agent role (planner/
    worker/reviewer) instead of always "worker".
  - Fanned-out workers get an on-disk record of their own scoped task; goals no
    longer read `Completed` before workspace review, and stale `DeadLettered`
    roots repair themselves when work resumes underneath them.
  - A live "Planning…" pulse badge on the decision tree, mirroring the reviewer
    indicator.
  - MCP tool-count in the docs corrected (117, not 66).

## 0.1.10 — 2026-07-08

- **Pathways is now workspace history, not an agent task list.** The Pathways
  tab renders the new `WorkspacePathways` projection — goals started,
  integrations, rejections, dead branches, and external file updates, each
  attributed to an actor (agent/human/external) — instead of the old
  per-work-unit artifact + orchestration decision-log dump. The
  NoOp/Enqueue/SpawnPlanner chatter no longer appears there (it stays in the
  per-goal views where it belongs). Lanes order chronologically; selecting a
  session dims out-of-session lanes instead of hiding workspace history.
- **Pathways node detail.** Clicking an Integration/Rejection/Superseded node
  shows the proposal's file diffs (inline, plus "View Diff in Editor") and the
  agent conversation that produced it. External-update nodes list the changed
  files and can fetch a before/after file-level diff. New actions: "Branch
  from here (new steering)" (re-run from the proposal's base state with a
  different profile/goal/constraint), "Materialize to scratch workspace"
  (reconstructs the branch's current content into
  `{extension storage}/pathways-scratch/{branch}/{timestamp}` — never the live
  repo), and a "Sync now" toolbar button that resyncs external changes using
  the host's own configured repository path.
- **Pathways history is event-sourced and tamper-proof against supersede.**
  A proposal that merged and was later superseded by reconciliation now
  keeps *both* moments in the graph (its Integration node and its Superseded
  node, chained), with true transition timestamps from the execution event
  log. Nested topology: a fan-out child's proposal chains to its parent's
  proposal node, not straight to the root goal.
- **True point-in-time materialize.** Integration nodes now carry the
  repository snapshot recorded at apply time (including multi-repo
  write-backs, previously not snapshotted at all); "Materialize this point
  in time to scratch" reconstructs the repo exactly as that integration
  left it, via snapshot + content-addressed store — never the live repo.
- **Reviewer identity.** Approve/reject now records who decided ("user" or
  the reviewer agent id) on the proposal, in the event log, and in the
  Pathways drawer ("Reviewed by").
- **Pathways DAG visual.** Per-kind node shapes/colors (goal, integration,
  rejection, superseded, dead branch, external update) with a legend row,
  and the projection's edges drawn as cross-lane connectors.
- **Fixes.** Webview HTML escaping in Pathways was a no-op (rendered LLM/diff
  text unescaped); "View Diff" clicks no longer double-fire across Studio
  Shell views (duplicate/wrong diff tabs); node detail no longer renders a
  stale response after rapid node clicks.
- **Reconciliation dead-ends eliminated.** A reconciliation work unit's own
  proposal merging now correctly transitions it `Executing` → `Merged`
  (previously an illegal-transition error silently left it stuck at
  `Executing` forever); its still-`Proposed` children get finalized to
  `Merged` alongside it. `MergeReconciliationService` no longer dead-ends on
  cancelled children, on a superseded reconciled proposal whose replacement
  lives elsewhere in the chain, or on a task-conflict resolution proposal
  mistaken for the top-level "already reconciled" one — and every
  `WaitingForChildren`/`Conflict` result now carries a human-readable detail
  (which child, which status, which files) instead of a bare enum.
- **Orchestrator rescue sweep.** Reinvoking a stuck orchestrator now also
  sweeps its `Executing`/`Active`/`Waiting` children for a fan-out that never
  fired (the case where a reconciliation child got planned but never
  decomposed), and records what the reconciliation sweep concluded — awaiting
  review, escalated, or the specific blocker — in the decision log instead of
  an unexplained `NoOp`.
- **Goal status convergence.** Goals read from the goal store now derive
  `Converged`/`Abandoned` from their work unit's terminal status and write it
  back, instead of reporting `Exploring` forever once the underlying work
  finished.
- **File lease deadlock detection, scoped per goal.** Leases are now scoped
  to the root goal, so an unrelated goal touching the same relative file path
  in the same repository no longer blocks it. A wait-for cycle forming
  between two work units (each waiting on a file the other holds) is now
  detected at the moment it would form and resolved synchronously instead of
  hanging forever.
- **Credential resupply for retry/continue/re-plan.** Dead-letter retry,
  continue, and re-plan now accept an `overrideCredentialRef` so credentials
  can be resupplied after a Host restart wipes the in-memory registry, backed
  by a new `IRuntimeCredentialCache` that never persists a live API key to
  disk. `DeadLetterEntry.ApiKey` is `[JsonIgnore]`d end-to-end, so the old
  manual per-MCP-tool redaction step was removed as dead code rather than a
  real protection.
- **Scheduler guard against re-planning a leaf slice.** Enqueuing a Plan-stage
  profile directly against an already-fanned-out leaf now fails fast with a
  clear error instead of spinning up a confused planner run; further
  decomposition goes through Re-plan on the parent instead.
- **Test coverage.** Added integration tests for overlapping-file-scope
  auto-sequencing, staggered child completion, stuck-work/goal recovery,
  credential-cache and routing rehydration, planner handoff routing, and
  end-to-end workspace pathways.
- **Goal-level merge conflicts now use the same Task Conflict machinery as
  fan-out siblings**, instead of a dead-end `Reviewing` status flip with no
  actionable entity behind it. `MergeReconciliationService` opens a real
  `TaskConflictRecord` (with real REST endpoints, a Reconciler agent, and
  Reconcile/Resolve-Manually buttons already wired end-to-end) for every
  losing proposal in the conflict, auto-triggering reconciliation when both
  sides are `AgentApproval`. `MergeCommandService`'s AgentApproval/Hybrid
  auto-apply also now records an unexpected failure (a crash mid-review, a
  transient LLM/tool failure) to the dead-letter queue instead of letting the
  proposal vanish silently at `ReadyForReview` forever.
- **The automated reviewer is now visible while it's running**, and no longer
  reads as a flat rejection when it stalls. `InlineReviewerService` registers
  its synchronous review run in the same Activity Center visibility registry
  spawned agents use (a pulsing "Agent reviewing…" indicator now shows on the
  Decision Tree node being reviewed), and distinguishes "ran out of
  iterations/stalled without a decision" from "reviewer rejected it" — the
  former is now dead-lettered (`MaxIterationsExceeded`/`Stalled`) so it's
  resumable via Continue instead of silently stuck. Continue itself can now
  resume a dead-lettered *review* (not just a worker task) via
  `ReviewerAgentLoop`, reconstructing the reviewer's own prior turns the same
  way a resumed worker already did. Also fixed: `nm_v1_merge_review`'s
  `automated` flag silently falling through when sent as the string `"true"`
  instead of a JSON boolean; a truncated/anomalous LLM response (most
  commonly a max-output-tokens cutoff) in either the worker or reviewer loop
  now gets recorded in the conversation log instead of vanishing.
- **Requeue Goal — the un-cancel.** `Cancelled` was a true dead end for a
  work unit (no legal transition out of it at all) — the exact same problem
  Unreject-and-Revise already solved for a Rejected proposal, for the same
  reason: a human explicitly asking to resume something should never be
  permanently blocked by a status designed to stop runaway *automated*
  retries. **↺ Requeue** now appears on any Cancelled goal card in the
  Activity Center (and `nms_v1_goal_requeue` for external callers): a leaf
  work unit is re-queued for a worker; a fan-out parent re-attempts
  reconciliation via the now-idempotent, status-agnostic
  `MergeReconciliationService.TryReconcileAsync`. Since a cancel/requeue
  cycle commonly spans a Host restart (often *why* a human had to step in),
  Requeue also resupplies LLM credentials from the configured Orchestrator
  profile before doing anything else — the same in-memory cache the inline
  reviewer and re-enqueued workers depend on, resolved from settings.json +
  the saved credential store client-side, same as Reconcile already does —
  without spawning a new orchestrator loop (see `ResupplyCredentialsAsync`).
- **Goal Workspace's Decision Lens no longer strands you.** Selecting a
  goal/task with a pending decision candidate still auto-jumps to review it
  (the one "Actionable" item), but now as a 4th tab alongside
  Metadata/Context/Conversation instead of replacing the whole inspector —
  so the "Important" info those other tabs hold is always one click away,
  and the auto-jump only fires once per node selection instead of re-firing
  every time you revisit it. Clicking any proposal directly in the
  Reasoning & Execution Timeline routes through the same tabbed view.

## 0.1.9 — 2026-07-08

- **Candidate conflict reconciliation.** When promotion branches are on and two
  proposals land on the shared `candidate` branch touching the same file paths
  (or two fan-out sibling work units conflict under the same goal — now
  distinguished as a **Task Conflict**), the Activity Center lists the
  conflict with four ways to resolve it: **View Conflict Diff** (read-only
  side-by-side of the candidate branch vs. the losing proposal), **Reconcile**
  (spawns a dedicated reconciliation work unit seeded from the conflicting
  diffs plus optional steering notes — auto-spawns if a **Reconciler** agent
  profile is configured, otherwise created for manual spawn), **Restart**
  (rejects the losing proposal and restarts its goal in Revert mode from a
  clean branch snapshot), and **Resolve manually** (submit resolved file
  content directly, recorded as a synthetic merged proposal that supersedes
  the losing one(s)).
- **Edit File / Resync Workspace** in Decision Convergence (Review): both the
  normal proposal diff view and the apply-time conflict-report view now have
  an inline **Edit File** button per changed/conflicting path, with a
  **Resync Workspace** button appearing once you've edited it, to pull that
  edit back into the work unit's branch before deciding.
- New REST surface backing the above: `GET /studio/branches/candidate/conflicts`,
  `POST /studio/branches/candidate/conflicts/{id}/reconcile`,
  `POST /studio/branches/candidate/conflicts/{id}/resolve`,
  `GET /studio/workunits/{id}/task-conflicts`,
  `POST /studio/workunits/{id}/task-conflicts/{conflictId}/reconcile`,
  `POST /studio/workunits/{id}/task-conflicts/{conflictId}/resolve`,
  `GET /studio/merges/{id}/constituents`.
- Review policy and profile/topology selection UX cleanup in Model & Agent
  Studio and the Goal Workspace (shared webview chrome, trimmed dead code in
  `AgentConfigPanel` and `modelAgentStudio.js`).

## 0.1.8 — 2026-07-06

- Bumped bundled `NodalMerge.DotNetHost` to 0.2.0, which converges blob storage
  on one canonical cross-runtime layout: a flat, global content-addressed pool
  at `data/blobs/blake3/<hash>` (no shard directories, no `.blob` extension).
- **Existing workspaces convert themselves automatically** — on the first blob
  access after upgrading, the store migrates legacy `<shard>/<hash>.blob` files
  into `blake3/`, dedupes identical content, and writes a `.layout-v2` marker so
  the migration runs exactly once. No manual steps; anything unrecognized is
  quarantined into `.migration-skipped/` rather than deleted.
- Blob writes are now atomic (temp + rename), fixing a race where concurrent
  writes of the same asset could fail with a file-sharing violation.

## 0.1.6 — 2026-07-03

- Bumped bundled `NodalMerge.DotNetHost` to 0.1.4, picking up two correctness fixes:
  - Catch-up pack imports now retry parent-ordering rejects to fixpoint, so
    reconnecting after offline edits and late-joining a room no longer silently
    drops nodes when a pack arrives out of topological order.
  - Native library resolution prefers the NuGet-packaged `runtimes/<rid>/native`
    binaries over stale dev builds found in sibling repo checkouts
    (`NODALMERGE_HOST_FFI_DLL` / `NODALMERGE_LOCAL_FFI_DLL` still override).
  - No public API, FFI, or wire changes.

## 0.1.5 — 2026-07-02

- Bumped bundled `NodalMerge.DotNetHost` to 0.1.2, picking up core engine improvements:
  - Text projection rewritten around a chunked order-statistic RGA — 50k-op replay
    throughput up from 5.5k to 214k ops/sec.
  - Incremental map/list/blob/conflict caches replace full-history replay on reads,
    and conflict detection now streams winner/loser pairs as they happen.
  - No public API, FFI, or wire changes — this is a transparent performance upgrade.

## 0.1.4 and earlier

See git history.
