# NodalMerge Studio — Strategic Vision

## The test for every feature

Before building anything, apply this test:

> **Can I inspect, branch, merge, replay, review, and audit this artifact after the agent run completes?**

If the answer is yes, you're building the platform. If the answer becomes "we have nodes and prompts and routing," you're converging toward every other agent framework.

---

## The distinction that matters

Most agent systems treat execution outputs as temporary:

- LangGraph checkpoints capture *graph state* at a point in time. They're snapshots of the runtime, not objects you can reason about independently.
- CrewAI, Autogen, OpenAI Agents — same pattern. Plans, decisions, outputs are ephemeral messages in a conversation log.

NodalMerge Studio treats execution outputs as **durable artifacts**:

```
Goal
  ↓
Plan               ← inspectable, reviewable, branchable
  ↓
Work Units         ← inspectable, has lineage, has scope
  ↓
Branch Changes     ← auditable, attributable, comparable
  ↓
Merge Proposal     ← reviewable, approvable, supersedable, replayable
  ↓
Approved State     ← committed, traceable back to the goal
```

A LangGraph checkpoint says: "Here is what the graph state was."

A NodalMerge artifact says: "Here is the proposal that changed file X because task Y required it, worker agent Z produced it, reviewer approved it, and you can replay from its base state with a different model."

Those are fundamentally different things.

---

## The differentiator in one sentence

> **Git for agent reasoning and execution** — not Git for files.

GitHub Actions manages CI pipelines. NodalMerge manages AI execution pipelines.

Every step in the pipeline produces an artifact. Every artifact is a DAG node. Every DAG node can be:
- Inspected (what did the agent produce, why, from what input state?)
- Branched (spawn alternate execution from this exact point)
- Replayed (re-run this proposal with a different model or profile)
- Merged (reconcile competing proposal branches into one candidate)
- Audited (trace every artifact back to the goal that produced it)

---

## The single architectural decision that determines everything else

The `ArtifactChain` struct is the central data model. There are two ways to design it:

**Routing struct (wrong)**:
```csharp
// Can answer: does a plan exist? do proposals exist?
// Cannot answer: what produced this? who is the parent? where does replay start?
ArtifactChain(string? Plan, IReadOnlyList<string> ProposalIds, IReadOnlyList<string> ApprovedProposalIds)
```

**Lineage graph (right)**:
```csharp
// Can answer all of the above, plus: walk to any ancestor, find all children, filter by type+status
ArtifactRef(string ArtifactId, ArtifactType Type, string? ParentArtifactId, ArtifactStatus Status, ...)
ArtifactChain(IReadOnlyList<ArtifactRef> Artifacts)
```

The difference isn't implementation complexity — it's about what questions the system can answer. The lineage graph unlocks replay, branching, DAG visualization, and conflict detection with no additional structural changes. The routing struct requires adding special-case fields for each new capability.

**Every phase after 9d depends on this model being a graph, not a flat list.**

---

## The Proposal DAG model

The fundamental data structure is a DAG of states connected by proposals:

```
S0 (base state)
 ├── Proposal A (worker-1, model-X) → S1a
 └── Proposal B (worker-2, model-Y) → S1b

Merge Proposal C (merger, reconciled A+B) → S2
```

This enables:
- **Branch**: checkout S0, spawn new worker, compare outcome
- **Replay**: checkout S0, apply A only, inspect S1a
- **Compare**: diff S1a vs S1b to understand divergence
- **Merge**: reconcile A and B into C

This is not orchestration complexity. It is the foundational data model. Everything else (parallel workers, automated reviewers, profile routing) is a feature built on top of it.

---

## What we are NOT building

- An agent framework — LangGraph, CrewAI, Autogen already do this
- A token optimizer — model providers own that layer (caching, context windows)
- A prompt engineering platform — that's a product, not a platform

---

## What token/context reduction actually means here

Artifact projections do reduce context, but the pitch is not "fewer tokens":

Instead of:
```text
Entire repo history + entire conversation + entire execution trace
```

The agent gets:
```json
{
  "workUnit": "...",
  "plan": "...",
  "proposals": ["A", "B"],
  "artifactChain": { ... },
  "currentStage": "Execute"
}
```

That is state compression — a byproduct of treating artifacts as first-class objects, not a primary goal.

---

## The strategic milestone (before adding more agents)

The question is not: "How many parallel workers can we spawn?"

The question is: **"What can I do after an agent run completes?"**

If the answer is only "look at logs" → not differentiated.

If the answer is:
- Inspect the plan, tasks, and proposals as structured objects
- Compare competing proposals from the same base state
- Replay from any proposal checkpoint
- Branch from any artifact and run with a different model or profile
- Audit the full lineage from goal to applied change

→ You are solving a problem that existing frameworks do not address.

Fan-out, parallel workers, automated reviewers — all of that becomes a **feature of the platform** rather than the platform itself once this foundation is in place. That is the architecture inflection point.

---

## The core distinction from every other agent framework

Most agent frameworks remember **state**:
```
current context
current variables
current messages
```

NodalMerge remembers **work**:
```
what was attempted
what was proposed
what was accepted / rejected
what branch produced it
what model produced it
what constraints were discovered
why a decision was made
```

That distinction sounds subtle. It isn't. State is what the system knows right now. Work is what it did and why — and work can be reused, branched, compared, replayed, and audited.

---

## What "token reduction" actually means here

DeepSeek's cache, Anthropic's prompt caching, OpenAI's prefix caching — these reduce **LLM inference cost**: same work, cheaper compute. Model providers own that layer and will keep improving it.

NodalMerge reduces **LLM work performed**: don't rerun the planner if the plan didn't change; don't rerun the reviewer if the proposal wasn't modified; skip entire phases by reusing artifacts from prior runs.

These are complementary, not competing. They stack.

---

## What replay means (three distinct systems)

Replay is currently underspecified in the plan. There are three different things it can mean, and they require different implementations:

**Event replay** — walk the `OrchestrationEvent` log (10e) in order; reconstruct exactly what happened; surface in the Artifact Explorer timeline. No re-execution. Pure audit and debugging.

**Workspace replay** — use `CheckoutProposalBaseAsync` (10f) to restore a specific workspace state. Use case: recovery after a bad apply, or starting an alternate execution from a known-good point.

**Agent replay** — branch from a proposal's base state (10f) AND submit a new goal with a different profile or model. Use case: "rerun this proposal with Claude instead of GPT-4 and compare outputs." Requires a goal to drive the new execution; not automatic re-execution of the original agent.

These must be treated as separate capabilities in the UI and API. Conflating them produces confusing UX and ambiguous replay semantics.

---

## Knowledge artifacts are what close the token savings gap

The current artifact types (`Goal → Plan → Task → BranchChangeset → MergeProposal → MergeResult`) capture execution outputs. They don't capture knowledge.

Without knowledge artifacts, agents rediscover the same information on every run:
- "The codebase uses .NET 8 and targets Windows"
- "Redis is already in the stack; don't add another cache"
- "The auth middleware must not store session tokens" (a legal constraint, not just a preference)

With knowledge artifacts, agents query the existing graph and skip the discovery phase entirely. A `Constraint` artifact attached to the work unit's ancestor becomes part of every descendant's projection. That is where large-scale token reduction actually comes from — not inference cost, but skipping work.

---

## Positioning statement

> A persistent, branchable, replayable artifact graph of all AI work — where the durable graph is the product, and agents are features of it.

Not:
> Another multi-agent orchestration framework with pipeline stages.
