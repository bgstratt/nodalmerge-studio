# Slice 7f — Agent Config + Topology Templates

Status: **Planned**

## Problem

The Studio backend accepts `agentType` as a free string and routes tasks via `StudioTask.Domain`. But there is no way for a user to define what agent profiles exist, what model/capability they use, what domains they handle, or how a workspace's agent topology should be arranged. Every spawn requires knowing the right `agentType` string by hand.

## Concepts

**Agent Profile** — a named, reusable agent definition:
```
id:         "docs-writer"          (becomes the agentType string)
label:      "Documentation Writer"
domain:     "docs"                 (matches StudioTask.Domain)
modelHint:  "claude-opus-4-8"      (passed to the spawning mechanism)
systemPromptHint: "You focus on..."  (optional context)
```

**Topology Template** — a set of profiles pre-configured for a workspace:
```
name:         "NodalMerge Docs Site"
orchestrator: "orchestrator"
workers:
  - { profile: "docs-writer",    branch: "work/docs" }
  - { profile: "demo-builder",   branch: "work/demos" }
  - { profile: "api-documenter", branch: "work/api-docs" }
```

## Storage

Settings live in VS Code workspace settings (`/.vscode/settings.json`) so they travel with the project. Global profiles live in user settings.

```jsonc
// .vscode/settings.json
{
  "nodalmerge.agentProfiles": [
    {
      "id": "orchestrator",
      "label": "Orchestrator",
      "domain": "orchestration",
      "modelHint": "claude-opus-4-8"
    },
    {
      "id": "docs-writer",
      "label": "Documentation Writer",
      "domain": "docs",
      "modelHint": "claude-sonnet-4-6"
    },
    {
      "id": "demo-builder",
      "label": "Demo Builder",
      "domain": "demo",
      "modelHint": "claude-sonnet-4-6"
    }
  ],
  "nodalmerge.topologyTemplates": [
    {
      "name": "Single Worker (default)",
      "orchestrator": "orchestrator",
      "workers": [{ "profile": "worker", "branch": "work/main" }]
    }
  ],
  "nodalmerge.defaultTopology": "Single Worker (default)"
}
```

## Files touched

### Updated: `extension/package.json`

Add `"contributes": { "configuration": { ... } }` for:
- `nodalmerge.agentProfiles` — array of AgentProfile objects
- `nodalmerge.topologyTemplates` — array of TopologyTemplate objects
- `nodalmerge.defaultTopology` — string (template name)

JSON schema validation in `package.json` means VS Code IntelliSense validates the settings file.

### New: `extension/src/AgentConfigService.ts`

```ts
class AgentConfigService {
  getProfiles(): AgentProfile[]
  getTemplates(): TopologyTemplate[]
  getDefault(): TopologyTemplate | undefined
  getProfileById(id: string): AgentProfile | undefined
  getProfilesForDomain(domain: string): AgentProfile[]
}
```

Reads from `vscode.workspace.getConfiguration('nodalmerge')`. No persistence logic — VS Code manages the settings file.

### New: `extension/src/panels/AgentConfigPanel.ts`

WebView panel opened from the gear icon in the activity bar sidebar or via `nodalmerge.openAgentConfig` command.

### New: `extension/src/webviews/agent-config/`

Config UI with three sections:

**Agent Profiles tab**
```
[ + Add Profile ]

  docs-writer          Documentation Writer    docs    claude-sonnet-4-6   [Edit] [Delete]
  orchestrator         Orchestrator            orchestration  claude-opus-4-8   [Edit] [Delete]
```

Inline edit form per row (no modal):
```
  id:          [____________]   label: [____________________]
  domain:      [____________]   modelHint: [________________]
  systemPromptHint: [multiline textarea]
  [Save]  [Cancel]
```

**Topology Templates tab**
```
[ + Add Template ]

  Single Worker (default)    orchestrator + 1 worker     [Edit] [Delete] [Set Default ✓]
  NodalMerge Docs Site       orchestrator + 3 workers    [Edit] [Delete]
```

**Quick Spawn tab**

Shortcut panel without going to the dashboard:
```
Spawn from template: [ Single Worker (default) ▼ ]
Goal: [____________________________________________]
[ Spawn Workspace ]
```

Calls `POST /studio/workunits` for orchestrator WU, then `POST /studio/agents/spawn` for each worker profile in the template.

### Updated: `extension/src/panels/WorkspaceDashboardPanel.ts`

"Spawn Agent" in the dashboard now uses `AgentConfigService.getProfiles()` to show a dropdown of known profiles instead of a free-text field.

### Updated: Task creation flow

When creating a new task from the dashboard, show a "Domain" dropdown populated from `AgentConfigService.getProfiles().map(p => p.domain)`. This populates `StudioTask.Domain` which the orchestrator uses for routing.

## Default profile

If no `nodalmerge.agentProfiles` is configured, `AgentConfigService` returns a built-in default:
```ts
const DEFAULT_PROFILES: AgentProfile[] = [
  { id: 'orchestrator', label: 'Orchestrator', domain: 'orchestration', modelHint: '' },
  { id: 'worker',       label: 'Worker',       domain: 'general',       modelHint: '' },
];
```

This means the extension works out of the box with no settings required.

## Out of scope

- Model API key management (user handles credentials through their AI extension — Copilot, Continue, etc.)
- Automatic task routing (the orchestrator AI reasons about domain matching; this config gives it the vocabulary)
- Agent profile version control (profiles are workspace settings, git-tracked with the project naturally)
- Template import/export

## Success criteria

- [ ] `nodalmerge.agentProfiles` setting is recognized with schema validation in VS Code
- [ ] Agent Config panel opens from gear icon in sidebar
- [ ] Profiles can be added, edited, and deleted via the WebView UI
- [ ] Templates can be defined with orchestrator + worker profile assignments
- [ ] "Quick Spawn" creates WorkUnits and spawns agents per the selected template
- [ ] Dashboard "Spawn Agent" uses profile dropdown instead of free-text field
- [ ] Task creation shows domain dropdown from configured profiles
- [ ] Default profiles work when no settings are configured

## Completing Slice 7

This is the final planned extension slice. At this point the extension provides:
- Host lifecycle management (7a)
- Live workspace dashboard (7b)
- Human AP-4 merge gate UI (7c)
- Live DAG visualization (7d)
- Historical scrubbing + branch-from-cursor (7e)
- Agent profile config + topology templates (7f)

Future slices will iterate based on usage.
