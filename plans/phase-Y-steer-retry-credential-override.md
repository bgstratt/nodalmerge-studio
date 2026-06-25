# Phase Y — Steer & Retry with Credential Override

## Problem
When a dead-lettered work unit is retried via "Steer & Retry", `ResolveRetryCredentials` deliberately prefers the credentials captured on the dead-letter entry (Model, BaseUrl, ApiKey, Provider) over the live in-memory orchestrator registry. This means a user who wants to retry with a different model/profile (e.g., switching from vscode-lm to deepseek) has no way to do so — the credentials are frozen at failure time.

## Solution
1. **Backend**: Add optional credential override parameters to `RetryAsync`/`RetryWithContextAsync` that, when supplied, take priority over the dead-letter entry's captured credentials.
2. **Frontend**: Update the Decision Lens dead-letter UI to:
   - Show a text box for steering context directly in the panel (instead of `vscode.window.showInputBox`)
   - Show a checkbox "Use new agent profiles" that, when checked, shows a profile dropdown from Model & Agent Studio
   - Send the steering context + optional credential override to the retry endpoint

## Files to change
- `src/NodalMerge.Studio.Core/Services/ServiceContracts.cs` — Update `IDeadLetterService` interface
- `src/NodalMerge.Studio.Storage/InMemoryDeadLetterService.cs` — Update `RetryAsync`, `RetryWithContextAsync`, `ResolveRetryCredentials`
- `src/NodalMerge.Studio.Host/StudioRestEndpoints.cs` — Update REST bodies
- `clients/vscode-extension/src/panels/ArtifactExplorerPanel.ts` — Update steering UI