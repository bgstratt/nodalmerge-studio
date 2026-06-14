# NodalMerge Studio v1 Architecture Specification

## Status

Draft v1

## Purpose

NodalMerge Studio is an agent-native collaborative workspace platform built on top of NodalMerge.

NodalMerge provides:

* Convergent DAG storage
* Branching
* Replay
* Promotion
* CRDT-based replication
* Local-first operation
* Peer convergence

NodalMerge Studio provides:

* Agent orchestration
* Task management
* Work unit management
* Projection generation
* MCP integration
* Human review workflows
* Workspace visualization
* VS Code integration

The primary goal is to enable multiple humans and multiple AI agents to collaborate within a shared convergent workspace while maintaining deterministic storage and replay guarantees.

---

# Architectural Principles

## AP-1: NodalMerge Remains the Source of Truth

All persistent state MUST reside in NodalMerge.

No separate memory database shall be introduced in v1.

No vector database shall be required.

No agent-specific memory store shall be required.

Persistent state MUST be represented as NodalMerge nodes.

---

## AP-2: Agents Reason Over Projections

Agents MUST NOT consume raw DAG history as their primary context.

Agents consume projections.

Projection generation is performed by the Projection Manager.

Replay is considered a recovery, debugging, and inspection capability.

Replay is NOT the primary reasoning model.

---

## AP-3: Work Unit Centric Execution

The primary execution abstraction is a Work Unit.

Definition:

WorkUnit = Goal + Branch

Every agent execution session MUST be associated with exactly one Work Unit.

---

## AP-4: Human-Governed Promotion

Agents may propose changes.

Agents may not directly merge changes into the authoritative branch.

Human approval is required for merge in v1.

---

## AP-5: Immutable History

NodalMerge nodes remain immutable.

Updates create new nodes.

History remains append-only.

---

# System Architecture

## Layers

### Layer 1: NodalMerge Core (Rust)

Responsibilities:

* DAG storage
* CRDT convergence
* Replication
* Replay
* Branching
* Promotion
* Persistence
* Sync

Studio MUST consume existing NodalMerge APIs.

Studio MUST NOT reimplement core storage logic.

---

### Layer 2: Studio Services (.NET)

Responsibilities:

* Agent Runtime
* Orchestrator
* Projection Manager
* MCP Server
* Task Services
* Merge Services

Studio Services are the primary business logic layer.

---

### Layer 3: User Experience

Components:

* VS Code Extension
* Web Dashboard
* Administrative Tools

These layers are presentation only.

No authoritative state is stored here.

---

# Language Strategy

## Rust

Used for:

* nodalmerge-core
* nodalmerge-server
* replay engine
* storage engine
* projection execution primitives
* convergence logic

---

## .NET

Used for:

* Studio.Core
* Studio.AgentRuntime
* Studio.Orchestrator
* Studio.McpServer
* Studio.Projections
* Studio.Tasks
* Studio.Merge

---

## TypeScript

Used for:

* VS Code Extension
* Web Dashboard

No business rules should reside here.

---

# Core Domain Model

## WorkUnit

Represents a unit of execution.

Fields:

* WorkUnitId
* Goal
* BranchId
* Status
* CreatedAt
* UpdatedAt
* Owner
* AssignedAgent
* SuccessCriteria
* Metadata

Status:

* Created
* Active
* Waiting
* Completed
* Failed
* Cancelled

---

## Task

Represents actionable work.

Fields:

* TaskId
* WorkUnitId
* Title
* Description
* Status
* Assignee
* Priority

Task MUST NOT contain DAG node references.

Task represents intent.

Not storage topology.

---

## MergeProposal

Represents an agent-generated proposal.

Fields:

* ProposalId
* SourceBranch
* TargetBranch
* Goal
* Summary
* ChangeDescription
* VerificationResults
* RollbackPlan
* Confidence
* Status

Status:

* Draft
* ReadyForReview
* Approved
* Rejected
* Merged

---

## KnownGoodState

Represents a verified branch state.

Fields:

* StateId
* BranchId
* Description
* VerificationResults
* CreatedAt
* CreatedBy

KnownGoodState is used for rollback and recovery workflows.

---

## ExecutionSnapshot

Represents a derived execution view.

Fields:

* AgentId
* WorkUnitId
* CurrentGoal
* CurrentTask
* WorkingHypothesis
* RecentActions
* Constraints
* FailureCount
* RollbackCount
* NextSuggestedAction

ExecutionSnapshots are derived.

ExecutionSnapshots are NOT authoritative storage.

---

# Projection Manager

## Purpose

Projection Manager is responsible for transforming DAG state into agent-consumable context.

Agents never directly read DAG structures.

Agents request projections.

Projection Manager generates projections.

---

## Responsibilities

* Projection generation
* Projection caching
* Projection invalidation
* Projection compaction
* Snapshot generation

---

## Projection Types

### WorkUnitProjection

Primary projection for agents.

Contains:

* Goal
* Current status
* Active tasks
* Dependencies
* Success criteria
* Assigned agents

---

### AuthoritativeStateProjection

Represents current accepted state.

Contains only merged state.

No branch-local changes.

---

### TaskProjection

Task-centric execution view.

Contains:

* Open tasks
* Blocked tasks
* Completed tasks
* Assignments

---

### MergeProposalProjection

Contains:

* Pending proposals
* Review status
* Verification results

---

### ExecutionSnapshotProjection

Contains:

* Current reasoning state
* Failure history
* Recovery hints

---

# Projection Compression Levels

All projection types SHOULD support:

## Full

Maximum detail.

## Normal

Default detail.

## Compact

Token-efficient.

## Emergency

Minimal operational state.

API:

projection.get(type, level)

---

# Agent Model

## Agent Types

### OrchestratorAgent

Responsibilities:

* Create work units
* Assign work
* Spawn workers
* Review results
* Coordinate execution

The OrchestratorAgent never performs direct implementation work.

---

### WorkerAgent

Responsibilities:

* Execute tasks
* Generate changes
* Produce merge proposals
* Verify outcomes

Workers operate inside a single Work Unit.

---

# Agent Execution Loop

Observe

Read projections.

Think

Determine next action.

Act

Perform workspace operation.

Verify

Validate outcome.

Propose

Submit merge proposal.

The loop ends at proposal submission.

Merge authority remains external.

---

# MCP Contract

## Versioning

MCP contracts are strictly versioned.

v1 changes require backward compatibility.

Breaking changes require v2.

---

## Projection Namespace

projection.get

projection.compact

---

## Task Namespace

task.create

task.update

task.list

---

## Branch Namespace

branch.create

branch.checkout

branch.list

---

## Merge Namespace

merge.propose

merge.validate

merge.review

merge.apply

---

## Replay Namespace

replay.range

replay.rollback

replay.inspect

---

## Agent Namespace

agent.spawn

agent.pause

agent.resume

agent.status

---

## Known Good Namespace

state.markKnownGood

state.findKnownGood

state.checkoutKnownGood

---

## Snapshot Namespace

snapshot.get

snapshot.compare

---

# Replay Model

Replay exists for:

* Debugging
* Recovery
* Rollback
* Auditing
* Human inspection

Replay is not intended to be the primary reasoning path for agents.

Projection consumption is preferred.

---

# Human Review Model

Merge workflow:

Draft
→
ReadyForReview
→
Approved
→
Merged

or

Draft
→
ReadyForReview
→
Rejected

Human approval is mandatory in v1.

---

# VS Code Extension

VS Code acts as the Studio Control Tower.

Capabilities:

* View branches
* View replay timeline
* Spawn agents
* Pause agents
* Resume agents
* Inspect projections
* Review merge proposals
* Approve merges
* Rollback to KnownGoodState

The extension is an orchestration surface, not merely a visualization layer.

---

# Out of Scope for v1

The following are explicitly deferred:

* Autonomous self-directed agents
* Agent-to-agent approval chains
* Agent-controlled merges
* Long-term memory databases
* Vector databases
* Dreaming/distillation pipelines
* Cross-workspace reasoning
* Autonomous goal generation
* Enterprise RBAC systems
* Multi-tenant SaaS architecture

---

# Success Criteria

v1 is successful when:

1. A human creates a Work Unit.
2. An OrchestratorAgent creates tasks.
3. WorkerAgents execute tasks.
4. Changes are stored in NodalMerge branches.
5. Agents consume projections instead of replay.
6. Merge proposals are generated.
7. Humans review and merge proposals.
8. Replay and KnownGoodState support recovery.
9. Multiple peers converge through NodalMerge replication.
10. The complete workflow operates from VS Code and MCP.

---

## Related docs

* [MCP v1 contract](../contracts/mcp-v1-contract.md) — frozen operating-system API
* [Projection v1 contract](../contracts/projection-v1-contract.md)
* [CRDT vs cognition layer](./crdt-vs-cognition-layer.md)
* [Node schemas](./node-schemas.md) — persistence conventions for Studio entities in NodalMerge
* [ADR-001: Embedded NodalMerge host](../adr/001-embedded-nodalmerge-host.md) — host integration decision
