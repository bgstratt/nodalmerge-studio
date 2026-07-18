# Multi-user repo-identity convergence — findings

**Date:** 2026-07-17
**Context:** hand test of the studio-on-NuGet 0.2.3 host across two machines (desktop
`192.168.1.110` running the room server on `0.0.0.0:7878` + a local extension runtime;
laptop connecting to `192.168.1.110:7878`). Both clones of one small eval repo.
**Status:** root cause confirmed. Fix is designed but **not yet implemented** — see
[`plans/repo-identity-convergence.md`](../plans/repo-identity-convergence.md).

---

## Symptom

A goal run on either machine is visible on the other (its **goal node** shows up), but
the repo's **pathways / branch activity / per-repo DAG** never appear on the peer. The
user's words: "not seeing the pathways or anything on the remote."

## What actually works (proven this session)

- Cross-machine WebSocket connectivity to the room server (firewall/IP/identity-hint
  mechanics all correct once `192.168.1.110:7878` + a firewall rule for 7878 were set).
- Repo binding + repo-room **joining** (laptop went from 1 room → 3 after it bound the repo).
- **Goal-node replication** across machines — goals ride the shared `workgroup1` room, so
  a goal created on the laptop appeared on the desktop, status and all.

So the workgroup-coordination plane replicates. The **per-repo plane does not** — and the
reason is not a wiring bug in replication itself.

## Root cause: two clones minted different repoIds

On the replicated work unit we could see the two clones had bound the same physical repo
to **different** workgroup repoIds:

| Machine | Minted repoId | Repo room |
|---|---|---|
| Desktop | `repo-c3296306…` | `repo/repo-c3296306…` |
| Laptop  | `repo-cc249077…` | `repo/repo-cc249077…` |

…despite **identical identity inputs**: same root commit SHA
(`76d1bfae5adf4ae2130fba0f88990ac5cc86ddbd`) and a shared GitHub remote. Different
repoId ⟹ different `repo/{repoId}` room ⟹ the per-repo DAG (branches, files, pathways,
activity) lives in two rooms that never see each other. Only the goal node crosses,
because it lives in `workgroup1`.

### Why it diverges — the binding race

Repo → workgroup binding happens **exactly once, edge-triggered**, in
`RepositoryRegistryService.BindToWorkgroupAsync`, at `RegisterAsync` time:

1. Compute identity hints for the local folder (`RepositoryIdentityHintsService`: root-SHA
   set + normalized remotes — both deterministic, both correct here).
2. `WorkgroupRepositoryDirectory.MatchAsync(hints)` → `ListAsync()` → reads **only the
   local in-process** `repositories` map, hydrated from whatever inbound workgroup packs
   have landed **and been persisted so far** (`EnsureInitializedAsync` runs
   `HydrateAndReplayAsync` once, against local durable state).
3. `RepositoryIdentityMatcher.Match` keys on **root-SHA intersection** — count 1 = Matched,
   0 = `NoMatch`, >1 = fork tiebreak on remotes.
4. `NoMatch` ⟹ **mint a fresh `repo-{guid:N}`** and register it.

Matching *would* converge (both clones share the root SHA) — **iff** the peer's authoritative
entry were already in the local map at the instant of registration. It usually isn't:

- The extension registers the open repo **eagerly at host start**, which is exactly when
  `RoomPeerClient`'s membership loop (5 s tick) + connect + `hello` + `welcome` +
  catch-up-pack apply for the `workgroup1` room is still spinning up.
- So the registering peer reads an empty/incomplete `repositories` map → `NoMatch` → mints.

Two concrete failure modes, both observed-plausible:

- **(A) Late-join race** — peer B registers before peer A's entry has replicated into B's
  live workgroup map.
- **(B) Simultaneous mint** — both peers register before either's entry replicates. Now the
  workgroup map ends up with **two** entries sharing the same root SHA → the matcher returns
  `NeedsDisambiguation` (fork ambiguity) forever, and no genuine fork exists.

### Why it never self-heals

Binding is **sticky**. Once `WorkgroupRepoId` is set it is never re-evaluated:

- `RehydrateAsync` / `RefreshAsync` only re-read this peer's own `RepositoryV1` rows — they
  do not re-run matching.
- `docs/STUDIO_ROOM_SCHEMA.md` (b), decision **D2**, explicitly forbids re-deriving identity
  after first contact: *"hints consulted only at first contact; never re-derived to re-identify
  a binding."*

So even when peer A's entry arrives on peer B a few seconds later, nothing reconsiders B's
prematurely-minted id. **The divergence is permanent by design.**

## The design tension

D2's stance — *"git supplies matching hints, never identity; identity is minted, not derived"* —
is exactly what makes minting a **random per-peer** id inherently divergent under concurrency.
The whole "workgroup map is the authority; match-before-mint; replicate the entry" scheme is a
first-writer-wins consensus that loses to the startup race, with no repair path afterward.

Fixing convergence therefore requires **amending D2**, not just patching code. See the plan.

## Fix direction (summary — full plan in the plan doc)

1. **Deterministic content-derived repoId** (primary): when hints are non-degraded, derive
   `repoId = repo-<hash(rootShas)>` — **root-SHA set only, remotes excluded** (the eval repo had
   different remote sets on the two machines, so any remote-inclusive key would re-create the
   divergence). Two clones of the same repo compute the same id independently — the race is gone,
   convergence needs no replication at all. Genuine forks share a root SHA and are split by
   one-time disambiguation (disjoint remotes); guid mint + disambiguation stays the fallback for
   degraded hints (shallow/no-remote/empty).
2. **Re-resolution safety net**: on an inbound **workgroup** pack, re-run matching for any
   provisionally/degraded-bound repo and migrate a self-minted binding onto the canonical id
   (join its room, leave the abandoned one). Deterministic winner for duplicate entries. Hooks
   the existing `IStudioCacheRefreshCoordinator.RefreshAfterInboundPackAsync(roomId)` seam.
3. **One-time repair pass** to collapse the already-diverged eval ids so the current two
   machines converge without re-cloning.

## Key code references

- `src/NodalMerge.Studio.Storage/RepositoryRegistryService.cs` — `BindToWorkgroupAsync`
  (one-shot bind), `ResolveDisambiguationAsync`, `RehydrateAsync`/`RefreshAsync` (no re-match).
- `src/NodalMerge.Studio.Storage/WorkgroupRepositoryDirectory.cs` — `RegisterAsync` (guid mint),
  `MatchAsync`, `RepositoryIdentityMatcher.Match` (root-SHA keying).
- `src/NodalMerge.Studio.Storage/RepositoryIdentityHintsService.cs` — deterministic hint inputs.
- `src/NodalMerge.Studio.Host/RoomPeerClient.cs` — workgroup-room join + inbound pack apply +
  `RefreshAfterInboundPackAsync` fan-out (the re-resolution hook).
- `docs/STUDIO_ROOM_SCHEMA.md` (b) + D2 — the frozen contract that must be amended.

## Separate finding from the same session

`RuntimeWebSocketLoopRunner` (in the nodalmerge repo's DotNetHost) persists a **full-room
snapshot on every mutation** with no debounce — a real O(n²) storm when seeding a large repo
(the docs repo stalled under a flood of `snapshot-on-mutation` writes). Independent of the
convergence bug; tracked separately.
