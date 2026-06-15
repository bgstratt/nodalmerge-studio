# Slice 7g — Studio Write-Through (Domain State in the DAG)

Status: **Planned**

## Problem

Studio domain events (WorkUnit created, Task assigned, Merge proposed, Agent spawned) are stored only in in-memory services. They never land in the NodalMerge CRDT room. This means scrubbing the DAG cursor does not reconstruct Studio state at that historical point — the replay panel shows raw CRDT ops from agent scratch-space, not the project coordination history.

`IStudioNodeStore` / `InMemoryStudioNodeStore` already exist as the write-through seam. Nothing calls `PutAsync` yet.

## Goal

When you scrub to any node in the DAG, the Studio's WorkUnit list, Task list, Merge proposals, and Agent assignments reflect the state as it was at that point in history. True time-travel for coordination state.

## Design (to be fleshed out)

Wire `IStudioNodeStore.PutAsync` calls into the service layer so every mutation emits a CRDT `map-set` into the branch room:

```
map-set  studio/workunit/{workUnitId}   { ...WorkUnit json }
map-set  studio/task/{taskId}           { ...StudioTask json }
map-set  studio/merge/{proposalId}      { ...MergeProposal json }
map-set  studio/agent/{agentId}         { ...AgentRecord json }
```

On scrub to node X:
1. `request-server-pack` up to node X's frontier
2. Reconstruct the room map at that point
3. Read all `studio/*` keys → deserialize back to domain objects
4. Feed to WebView as historical Studio state

## Dependencies

- Slices 7d + 7e (DAG panel with scrubbing) must be complete
- Requires deciding which room each WorkUnit's events go into (the WorkUnit's own branch room, or a shared workspace room)
- Requires `IStudioNodeStore` to actually call into the NodalMerge room API (currently an in-memory dict with no room connection)

## Out of scope (defer to this slice)

- Code content reconstruction (what agent text edits look like at cursor X) — separate concern, much larger
- Cross-branch Studio state aggregation

## Success criteria (placeholder — flesh out when slicing)

- [ ] Creating a WorkUnit emits `map-set studio/workunit/{id}` into the room
- [ ] Creating a Task emits `map-set studio/task/{id}`
- [ ] Merge proposal lifecycle emits events at each transition
- [ ] Scrubbing the DAG cursor to node X shows the WorkUnit/Task/Merge state as it was at that point
- [ ] Existing 81+ tests still pass (write-through is additive, not replacing in-memory)
