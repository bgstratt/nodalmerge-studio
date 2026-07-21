# Studio room schema (frozen contract)

Status: **frozen 2026-07-15** (slice S6.0 of `plans/cas-distribution-and-storage.md`,
Phase 6). This note freezes the schema/encoding the first replicated write uses — per
the plan's own rule ("the first peer that writes one freezes it"), any change to a byte
shape defined here after 6.1a starts writing is a format break requiring a `v` bump, not
a silent edit. Style follows `docs/TREE_OBJECT_FORMAT.md`: normative voice, vectors,
readers-lenient/writers-constrained where it matters.

This note freezes three encodings, consumed by three later slices:

- **(a)** studio-node → engine-map encoding — consumed by **6.1a** (`NodalMergeStudioNodeStore` v2).
- **(b)** workgroup-room repositories map — consumed by **6.2** (repository identity, D1/D2).
- **(c)** pinned cross-repo reference triple — consumed by **6.3** (room-per-repo, D3).

**Out of scope:** the CRDT pack's on-the-wire byte encoding (how the engine serializes a
`MapSet` op into a pack) is engine-owned (`nodalmerge/engine/host-core`, `nodalmerge-core`)
and frozen by its own conformance tests, not this doc. This doc freezes only the
**JSON values Studio hands to `HostCommand::MapSet`** (namespace, key, value) and the
**JSON shapes those values contain** — everything below the engine's `Value =
serde_json::Value` boundary. Migration mechanics (the one-shot legacy-row import) are
6.1a's job, not this doc's.

## (a) Studio-node → engine-map encoding

### Namespace and key

- Engine namespace: `"studio"` — one namespace, all `StudioNodeKind`s multiplexed into
  it via the key, mirroring how the legacy scheme multiplexed them into one room
  (`"studio"`, `NodalMergeStudioNodeStore.StudioRoomId`) via the node-ID prefix.
- Key: **`"{kind}/{entityId}"`**, where `kind` is the exact `StudioNodeKind` string
  (e.g. `"studio/work-unit/v1"`, itself already containing `/`) and `entityId` is the
  caller-supplied entity identifier, which **may itself contain `/`** (e.g.
  `"base/MP-1"` — see `MergeProposal.cs`'s own comment: a work unit's pre-attempt
  branch snapshot is keyed `base/{proposalId}`).

  Example key: `studio/merge-proposal/v1/base/MP-1`.

#### Parsing rule (normative — this is the part that isn't obvious)

Because both `kind` and `entityId` may contain `/`, a key cannot be split by a naive
"first slash" or "last slash" rule. The resolution is the same one the legacy scheme
already relies on (`NodalMergeStudioNodeStore.ReadAllNodesAsync`'s prefix-strip trick,
just without a fixed-width numeric suffix to anchor the other end):

1. `StudioNodeKind` is a **closed, compile-time-known set of string constants**. To
   recover `(kind, entityId)` from a bare key (e.g. when enumerating an entire
   `MapAll("studio")` result without already knowing which kind each entry is), try
   each known `StudioNodeKind` value as a candidate, longest string first, and test
   whether `"{candidate}/"` is a literal prefix of the key. The first match wins;
   `entityId` is everything after that prefix, verbatim (it is not re-parsed or
   re-split — further `/` inside it is opaque).
2. This is unambiguous today: no `StudioNodeKind` constant is a literal prefix of
   another once both are suffixed with `/` (verified by inspection of the full list in
   `StudioNodeStore.cs` — e.g. `studio/repository/v1/` is not a prefix of
   `studio/repository-op/v1/...` because they diverge at the character immediately
   after `studio/repository`: `/` vs `-`). Longest-match-first is specified anyway as a
   defensive rule for any future kind added without checking this invariant by hand.
3. In practice, most callers (`WriteNodeAsync`/`ReadNodeAsync`/`ReadAllNodesAsync`)
   already know `kind` up front (it's a parameter), so this global-parse case only
   matters for kind-agnostic tooling (debug inspection, the legacy-row migration
   walking the map after it's populated, etc.) — but the rule must hold globally, not
   just per-call, so it's specified once here rather than left as caller convention.

### Value — versioned envelope

```json
{"v": 1, "kind": "studio/work-unit/v1", "payload": { ... }}
```

- **`v`** (envelope schema version, integer) — lets the envelope shape itself evolve
  (e.g. adding a field later) independently of `payload`'s own internal versioning
  (which already lives in `kind`'s trailing `/v1`/`/v2` segment, per existing
  `StudioNodeKind` convention). Two different version concerns, two different fields,
  on purpose — bumping `payload`'s shape is a new `StudioNodeKind` constant (existing
  convention); bumping the envelope's own wrapper shape is a `v` bump here.
- **`kind`** — duplicates the key's kind segment inside the value. Redundant with the
  key by construction, kept anyway because: (1) it makes each map entry
  self-describing without re-running the parsing rule above, which matters for
  `MapAll` consumers, cross-language tooling (Rust-side GC/reachability walks,
  eventually, per the plan's Phase 5 note), or debug dumps that shouldn't need to
  import Studio's kind list to make sense of a record; (2) it lets a writer assert
  `kind`-in-value equals `kind`-parsed-from-key as a cheap self-consistency check; (3)
  the cost is negligible — `kind` strings are short and this is metadata, not bulk
  content (see Size guidance below).
- **`payload`** — **the payload stays JSON, embedded as a JSON value, not a
  double-encoded string.** `IStudioNodeStore.WriteNodeAsync(kind, entityId,
  payloadJson, ...)` today takes `payloadJson` as an already-serialized JSON string
  (callers do `JsonSerializer.Serialize(entity)` before calling it); the engine's
  `HostCommand::MapSet.value` is `serde_json::Value` — arbitrary JSON, not a string.
  6.1a's store must therefore **parse** `payloadJson` into a JSON value (e.g.
  `JsonDocument.Parse(payloadJson).RootElement` / a `JsonNode`) and embed the parsed
  tree as `payload`, rather than nesting `payloadJson` as an escaped string literal.
  Rationale: nesting as a string would (1) bloat every value by the escaping overhead
  of every `"` and `\` in the payload (roughly 10-30% larger for typical JSON, more
  for payloads with embedded strings/newlines), (2) force every reader to
  parse-then-parse-again (parse the envelope, then parse the string field), and (3)
  gives up structural readability in engine-side/debug tooling that inspects map
  values directly. Nothing about the payload's own shape changes — this is purely
  "don't wrap already-JSON content in a JSON string."

### Delete / tombstone encoding

**Deletes use the engine-native `HostCommand::MapDelete{namespace: "studio", key}`,
never an envelope-level tombstone value.** Rationale:

- The engine's map is already an LWW CRDT primitive with its own delete semantics
  (`MapValueDeleted{found: bool}` event, per `api.rs`) — a deleted key is simply absent
  from `MapAll`/`MapGet` going forward, and a later `MapSet` on the same key is an
  ordinary undelete (LWW: latest write wins, whatever it is). This is free, and it is
  exactly the semantics needed (see Semantics below).
- An envelope-level tombstone (e.g. writing `{"v":1,"kind":"...","deleted":true}` as
  the *value* of a live key) would remain a live, enumerable entry forever — every
  `MapAll` consumer would need bespoke tombstone-filtering logic baked in permanently,
  and the "deleted" marker never actually leaves map storage without extra machinery
  the engine doesn't need to be taught, since `MapDelete` already exists. Using it
  defeats the purpose of having a native delete op.
- **Studio's current code never deletes nodes** — `IStudioNodeStore` has no
  `DeleteNodeAsync` method today, so this rule is a forward-looking definition for
  whenever a delete operation is added (out of scope for 6.1a, per its constraints —
  6.1a only needs `MapSet`/`MapGet`/`MapAll` for the write/read paths it replaces).
  Freezing it now means 6.1a's implementer (or whoever adds delete later) doesn't have
  to re-litigate this choice.

### Semantics

**Per-entity last-write-wins**, matching the tick-suffix behavior of the legacy rows
exactly in outcome, differently in mechanism:

- Legacy: every `WriteNodeAsync` call appends a **new** node
  (`studio:{kind}:{entityId}:{ticksD20}`, append-only); "current value" is computed at
  read time by taking the row with the latest `AcceptedAtUtc` among rows sharing the
  `(kind, entityId)` prefix (`ReadNodeAsync`/`ReadAllNodesAsync`'s `OrderByDescending`
  + `GroupBy`).
- Engine map: `MapSet` on an existing key **overwrites** it — the LWW resolution is a
  first-class property of the map CRDT, not an application-level scan over history.
  Net observable behavior for `ReadNodeAsync`/`ReadAllNodesAsync` callers is identical
  (latest write per entity), but the *history* of intermediate legacy writes is not
  preserved once migrated — only the latest payload lives in engine map state. This is
  acceptable per AP-5: legacy rows are **never rewritten** during migration, so the
  full write history remains inspectable via the legacy node-store scan if ever
  needed; it just isn't part of engine map state going forward. (Migration mechanics —
  the one-shot import — are 6.1a's job, not this doc's; this section only defines the
  target semantics migration must produce.)
- **Legacy `"studio"`-payload-kind rows are never rewritten (AP-5) and remain readable
  during migration** — 6.1a's store must keep a fallback read path to the legacy
  node-store rows for anything not yet migrated, exactly as `TREE_OBJECT_FORMAT.md`'s
  legacy-`TreeEntries`-fallback pattern does for tree objects.

### Size guidance

Envelope values carry **metadata-scale JSON** — the same scale of content
`WriteNodeAsync` payloads carry today (work units, tasks, proposals, decisions: single-
or low-double-digit KB at most). **Bulk content (trees, file bytes) belongs in the CAS
as hashes, never inline in a map value** — this is the Phase 1 rule
(`RepositorySnapshot.TreeHash`, not inline `TreeEntries`, for new snapshots) restated
for the map encoding: a `StudioNodeKind` payload that references file content
references it by BLAKE3 hash (already true today — e.g.
`RepositorySnapshotV1`/`BlobIndexEntryV1` payloads carry hashes, not bytes), never by
embedding the bytes in the envelope.

## (b) Workgroup-room repositories map

Fields per decision record D1/D2 (`plans/cas-distribution-and-storage.md`, "Decision
record (2026-07-14)").

### Namespace and key

- Namespace: `"repositories"` (lives in the workgroup room, per D1 — distinct from the
  `"studio"` namespace, which is repo-room-scoped per repo).
- Key: `repoId`.

### Value

```json
{
  "v": 1,
  "label": "nodalmerge-studio",
  "repoRoomId": "repo/repo-3fa85f6457174562b3fc2c963f66afa6",
  "hints": {
    "rootShas": ["4b825dc642cb6eb9a060e54bf8d69288fbee4904"],
    "remotes": ["github.com/acme/nodalmerge-studio"]
  }
}
```

- `label` — optional human-readable name; mirrors `RepositoryV1.Label` (nullable
  today; carried through unchanged).
- `repoRoomId` — see naming rule below.
- `hints` — matching hints only, **never identity** (D2's core rule: "git supplies
  matching hints, never identity"). Consulted only at first-contact matching; never
  re-derived to re-identify an already-bound repo **except on an explicit user-initiated
  re-link** (see "User-initiated re-link" below).

### `repoId` mint format

**`repo-{32 lowercase hex}`** (no dashes) — e.g.
`repo-3fa85f6457174562b3fc2c963f66afa6`. The *shape* is frozen; the *derivation* has two
cases (amended 2026-07-17, `plans/repo-identity-convergence.md`, see the D2 amendment
below):

- **Strong signal (non-empty root-SHA set) → deterministic:**
  `repo-` + first 32 lowercase hex chars of `sha256(join('\n', sort(distinct(rootShas))))`.
  Derived from the **root-SHA set alone** — remotes are excluded (two clones of the same
  repo routinely have different remote sets, so a remote-inclusive key would give them
  different ids and defeat convergence). Two clones compute this identically, offline, with
  no coordination — which is the whole point: convergence must not depend on
  registration-order or replication timing. Implemented as
  `RepositoryIdentityMatcher.DeterministicRepoId`.
- **Degraded signal (empty root-SHA set: shallow clone, empty repo, no HEAD) → minted:**
  `repo-{guid:N}` (`` $"repo-{Guid.NewGuid():N}"`` ), as before. A hash of nothing would
  wrongly collapse unrelated degraded repos, so these keep the guid + one-time
  disambiguation path.

**Amendment (2026-07-17):** pre-amendment this was always a fresh `repo-{guid:N}`, with D2's
"identity is minted, never derived" rule. That is exactly what made two independent clones of
one repo diverge under a startup race (each minted its own guid before the other's workgroup
entry replicated), landing them in different repo rooms with no repair path. The deterministic
derivation for strong-signal repos removes that race; the workgroup map remains the *authority*
(it still records entries, splits genuine forks, and carries human overrides), and the ID shape
is unchanged, so this is a derivation change, not a format break.

### `repoRoomId` naming

**`repo/{repoId}`** — e.g. `repo/repo-3fa85f6457174562b3fc2c963f66afa6`. This is the
room identifier the repo-scoped state (D1's "repo room": snapshot/generation DAG, work
units, branches, proposals, decisions, conflicts, artifacts-as-CAS-references, and —
since the "#1 goal replication" change, `plans/repo-identity-convergence.md` — top-level
**goals** (`GoalV1`), denormalized with their work unit's `RepositoryId`) lives
under, replacing the hardcoded `"studio"` room constant
(`NodalMergeStudioNodeStore.StudioRoomId`, `RuntimeGraphPromoter`) per D1/6.3. The
authoritative per-kind list is `StudioNodeStore.RepoScopedKinds`. Note this revises the
earlier "GoalV1 is workgroup/global, not single-repo-scoped" stance: a single-repo goal is
repo-scoped so peers on the same repo see each other's goals; genuinely cross-repo goal
fan-out (D3) remains a later layer that can override placement per goal.

### Hint formats

#### Root-commit SHA set

`hints.rootShas` is a **set** (order-insensitive, no duplicates) of 40-lowercase-hex
git commit SHAs — the output of:

```
git rev-list --max-parents=0 HEAD
```

A set, not a single value, because merged unrelated histories (`git merge
--allow-unrelated-histories`, subtree merges, some monorepo consolidations) yield
**multiple** root commits for one working tree. Per D2: an exact-match on this set (or
any member of it, for fork-ambiguity detection) is the strongest available hint, but
still only a hint — it is consulted once, at first contact, and cached, never
re-derived to re-identify a binding later **unless a person explicitly asks for a
re-link** (see "User-initiated re-link" below).

**Known gap, flagged for 6.2's implementer, not resolved here:** this rule assumes
git's default SHA-1 object hashing. A repository configured with `extensions.objectFormat
= sha256` (rare today, growing) produces 64-hex root SHAs, not 40. The hint format
above is frozen as **"whatever `git rev-list --max-parents=0 HEAD` prints, one entry
per line, hex string, no fixed width assumed by consumers"** — 6.2's matching code
must not hardcode a 40-character length check, even though every worked example below
uses SHA-1's 40 hex chars.

#### Remote-URL normalization

Normalizes a git remote URL (any of the forms git itself accepts) to one canonical
string, so the same remote reachable via different syntaxes matches. Algorithm,
applied in order:

1. **Parse the host and path**, one of three input shapes:
   - **URI form** — `scheme://[user[:pass]@]host[:port]/path` (schemes: `http`,
     `https`, `ssh`, `git`). Discard scheme, user, and password entirely. Keep host,
     port (if present), and path.
   - **SCP-like form** — `[user@]host:path` (no `://` anywhere in the string — this is
     git's shorthand for ssh, e.g. `git@github.com:org/repo.git`). Discard the
     `user@` part. Everything before the (first) `:` is `host`; everything after is
     `path`. No port is expressible in this form.
   - **Bare form** — anything else (already `host/path`, e.g. a prior normalization
     output fed back in) — treat as `host` = first path segment, `path` = the rest.
2. **Lowercase `host` only.** Path segments keep their original case (org/repo names
   are frequently case-sensitive in practice, e.g. GitHub is case-preserving) —
   normalizing path case would risk conflating genuinely distinct remotes.
3. **Drop the port if it is the scheme's default** (`80` for `http`, `443` for
   `https`, `22` for `ssh`/`git`); otherwise keep it as `host:port`. The bare and
   SCP-like forms carry no scheme, so a port is only ever dropped when the URI form's
   explicit scheme confirms it's redundant.
4. **Strip one or more trailing `/` characters** from `path`.
5. **Strip exactly one trailing `.git` suffix** from `path` (case-sensitive, exact
   suffix match), if present.
6. **Join** as `host[:port]/path` — no scheme, no credentials, no leading `/` before
   path, single `/` separating host and path.

> **Amendment (2026-07-15, pre-replication):** the original freeze ordered steps 4/5
> the other way (`.git`-strip before slash-strip), under which
> `https://host/org/repo.git/` normalized to `org/repo.git` while the slashless form
> of the same remote normalized to `org/repo` — two hint strings for one remote,
> defeating the fork tiebreak. Amended while no cross-peer normalized hint existed
> anywhere (workgroup-room upstream replication lands in 6.3); this is an ordering
> defect fix in the original freeze, not a format break.

Worked examples:

| Raw remote | Normalized |
|---|---|
| `https://github.com/acme/nodalmerge-studio.git` | `github.com/acme/nodalmerge-studio` |
| `https://x-access-token:ghp_xxx@github.com/acme/nodalmerge-studio.git` | `github.com/acme/nodalmerge-studio` |
| `git@github.com:acme/nodalmerge-studio.git` (scp-form) | `github.com/acme/nodalmerge-studio` |
| `ssh://git@github.com:22/acme/nodalmerge-studio.git` | `github.com/acme/nodalmerge-studio` (default ssh port 22 dropped) |
| `ssh://git@example.com:2222/acme/repo.git` | `example.com:2222/acme/repo` (non-default port kept) |
| `https://github.com/acme/nodalmerge-studio/` (trailing slash, no `.git`) | `github.com/acme/nodalmerge-studio` |
| `https://github.com/acme/nodalmerge-studio.git/` (trailing slash after `.git`) | `github.com/acme/nodalmerge-studio` (slash stripped first, then `.git`) |
| `HTTPS://GitHub.com/Acme/Nodalmerge-Studio.git` | `github.com/Acme/Nodalmerge-Studio` (scheme case-insensitive; host lowercased; path case preserved) |

`hints.remotes` is a set of these normalized strings (one per configured remote —
typically `origin`, but not assumed to be named that).

### Matching flow (summary — D2 is the authority)

1. Compute hints for the local folder: root-SHA set + normalized remotes.
2. Look up the workgroup repositories map.
3. **Exact root-SHA match** (any member of the local set matches any member of a
   registered entry's set) → join that `repoRoomId`.
4. **Fork ambiguity** (root SHAs shared by more than one registered entry — e.g. two
   forks of the same upstream) → **remote-URL tiebreak** among the ambiguous
   candidates; if still ambiguous → **one-time user prompt**.
5. **No match** → mint a new `repoId`, register the entry, create the repo room.
6. Cache the resolved binding in the peer's local workspace storage — hints are
   consulted only at first contact; a later rebase or remote rename never re-triggers
   re-identification. The single exception is a user-initiated re-link (below); no
   background, timer, event, or inbound-pack path may ever re-identify a binding.
7. Degraded cases (shallow clone: empty root-SHA set; no remote: empty remotes set;
   empty repo: no `HEAD`) → one-time user prompt, same as an unresolved ambiguity.

Full rationale for why no single signal is sufficient (and why identity is minted, not
derived) is D2, not repeated here.

### User-initiated re-link

*Amendment, 2026-07-20 (plans/vision-punchlist-remediation.md, Items 1+2). Narrows the
"never re-derived" rule above to "never re-derived **on its own**".*

Deterministic ids (the earlier amendment) converge two clones that both have a root-SHA
signal. Two populations are still left stranded, and no automatic mechanism can rescue
them: a **degraded** checkout (shallow / empty / no `HEAD`) minted a guid and will keep
it forever, and installs that **already diverged** before deterministic ids existed stay
in separate rooms. Both need a human, because in both cases the machine genuinely cannot
tell whether two entries are the same repository.

A re-link may therefore re-read git and re-run matching for an already-bound repository,
under these rules:

1. **Explicit human action only.** No background sweep, timer, startup pass, inbound-pack
   handler, or cache-refresh coordinator may trigger it. This is what keeps a settled
   binding settled, which is D2's actual concern — a rebase or a remote rename must still
   never move a binding on its own.
2. **Fresh git read is permitted, and is the point.** A cached degraded hint set can never
   converge; only re-reading can discover that a once-shallow clone now has its root
   history.
3. **Automatic mode commits only an unambiguous match**, and never moves a binding whose
   provenance is `HumanResolved`. A fork, a degraded signal, or several candidates commits
   nothing and returns the candidates for the person to choose from.
4. **Content does not migrate.** Re-pointing changes which room is read and written from
   here on; nodes already in the old room stay there and drop out of the read fan-out. The
   flow MUST report what will stop appearing before committing — an informed choice, never
   a silent orphaning. (Re-keying old content is deliberately not in scope; no such
   machinery exists.)
5. **Provenance is recorded.** `RepositoryV1.Provenance` distinguishes `Deterministic`,
   `ProvisionalMint`, and `HumanResolved`, so re-link can tell a settled canonical binding
   from a provisional one worth converging, rather than inferring it.

## (c) Pinned cross-repo reference triple

Per D3 ("Cross-repo *read* references become pinned triples `(repoId, generationId,
path)`").

### Canonical JSON form

```json
{"v": 1, "repoId": "repo-3fa85f6457174562b3fc2c963f66afa6", "generationId": "gen-000123", "path": "src/lib/util.ts"}
```

- `repoId` — the minted ID from (b).
- `generationId` — identifies a snapshot/generation node in that repo's room DAG
  (D1's "snapshot/generation DAG" — the same identity space `RepositorySnapshotId`
  already occupies; this triple does not mint a new ID scheme, it references the
  existing one).
- `path` — **repo-relative, forward-slash-separated, no leading `/`** — exactly the
  path rules already frozen for tree-object entries in `TREE_OBJECT_FORMAT.md`
  ("`/` separators only, no leading `/`, no `.` or `..` segments"), reused rather than
  redefined so a path string means the same thing whether it appears in a tree object
  or a pinned reference.

### Resolution chain

1. **Repo room** — resolve `repoId` → `repoRoomId` (via the repositories map, (b)) →
   join/read that room.
2. **Generation node** — look up `generationId` in that room's snapshot/generation
   DAG → its `TreeHash`.
3. **CAS walk** — resolve `TreeHash` → root tree object (per
   `TREE_OBJECT_FORMAT.md`) → walk directory entries along `path`'s segments → the
   file's blob hash → `IBlobStoreProvider.TryGetBlobAsync` (chained provider, so a
   peer with no local clone of the referenced repo still resolves it, per D3's
   "works on peers that never cloned the referenced repo").

Strictly better than the current `IRepositoryRegistryService.ReadFileAsync` live-disk
read it's meant to replace (D3): reproducible (pinned to a specific generation, not
"whatever's on disk right now"), and works without a local clone.

## Vectors

`tests/NodalMerge.Studio.Integration.Tests/TestData/studio-room-schema-vectors.v1.json`
pins one example of each encoding: a work-unit envelope, a merge-proposal envelope
whose `entityId` contains `/` (`base/MP-1`, exercising the parsing rule in (a)), a
repositories-map entry with two root SHAs and a normalized ssh-remote hint, and a
pinned reference triple. `StudioRoomSchemaVectorTests`
(`NodalMerge.Studio.Integration.Tests`) loads each vector and asserts:

1. The `(kind, entityId)` pair for the two `studio`-namespace vectors round-trips
   through the key-building/parsing rule in (a) — including the `entityId`-with-`/`
   case, which is the one naive splitting gets wrong.
2. Every vector's `value` JSON is stable under deserialize → re-serialize (structural
   equality via `JsonNode.DeepEquals`, not byte-identity — see below for why).

**Pack-level byte encoding is explicitly out of scope for these vectors.** Unlike
`tree-format-vectors.v1.json` (which pins exact bytes because those bytes are hashed —
`TreeHash` is BLAKE3 *of the serialized bytes*, so canonical byte encoding is part of
the contract), nothing in this doc's encodings is content-addressed. A `MapSet` value
is carried as a parsed JSON value across the engine's own wire/pack format, whose exact
byte-level encoding is the engine's property (`nodalmerge/engine/host-core`,
`nodalmerge-core`), not Studio's. These vectors therefore assert *value stability*
(the JSON shape survives a round trip without silently losing or reordering data in
some future serializer swap), not *byte identity*.

## Future migration note

Same trajectory as `TREE_OBJECT_FORMAT.md`: today only .NET Studio writes these
encodings. If a future phase needs the Rust server to interpret `"studio"`-namespace
map values directly (e.g. a server-side reachability walk over Studio's own DAG,
foreshadowed by Phase 5.3's need to "walk studio snapshot nodes in every repo room it
persists"), this doc and its vectors migrate to the nodalmerge repo's parity mechanism,
exactly as `BLOB_STORAGE_LAYOUT.md`/`blob-layout-vectors.v1.json` and
`TREE_OBJECT_FORMAT.md`/`tree-format-vectors.v1.json` did.
