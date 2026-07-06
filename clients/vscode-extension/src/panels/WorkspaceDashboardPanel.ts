import * as vscode from 'vscode';
import type { MergeProposal } from './MergeReviewPanel';
import type { NotificationManager } from '../NotificationManager';
import type { AgentConfigService } from '../AgentConfigService';
import { resolveRepositoryPath } from '../repositoryPath';
import { scopeViewCss } from './sharedWebviewChrome';

const POLL_INTERVAL_MS = 2_000;

// ── Domain types matching Studio Host REST responses ───────────────────────

interface WorkUnit {
  workUnitId: string;
  branchId: string;
  goal: string;
  owner: string;
  status: string;
  successCriteria?: string | null;
  fanOutInfo?: { blockedReason?: string | null } | null;
}

interface AgentInfo {
  agentId: string;
  workUnitId: string;
  status: string;
}

interface ScheduledItem {
  workUnitId: string;
  profileId: string;
  taskId?: string | null;
  attemptCount: number;
}

interface WorkspaceSummary {
  activeWorkUnits: string[];
  activeAgents: string[];
  pendingMerges: string[];
  failures: string[];
  knownGoodStates: string[];
}

interface DeadLetterEntry {
  entryId: string;
  workUnitId: string;
  agentId: string;
  stage: string;
  profileId: string;
  reason: string;
  attemptCount: number;
  occurredAt: string;
  maxAttemptsReached: boolean;
  // Phase 1.4 — NodalMerge.Studio.Contracts.Domain.FailureKind, serialized as its string name.
  kind: string;
}

// Matches NodalMerge.Studio.Core.Services.GoalGuardrailStatus — Phase 2 item 1's alert-only
// per-goal cost/time guardrail. TokensExceeded/DurationExceeded are computed server-side against
// WorkspaceOptions.MaxGoalTokens/MaxGoalDurationMinutes (both null = disabled by default).
interface GoalGuardrailStatus {
  workUnitId: string;
  totalTokens: number;
  maxGoalTokens: number | null;
  tokensExceeded: boolean;
  elapsedMinutes: number;
  maxGoalDurationMinutes: number | null;
  durationExceeded: boolean;
}

// Matches NodalMerge.Studio.Contracts.Domain.ExecutionEvent — payloadJson is deserialized
// client-side per-kind (only ProviderRetryAttempted is parsed here today).
interface ExecutionEventDto {
  eventId: string;
  sessionId: string;
  workUnitId?: string | null;
  kind: string;
  payloadJson: string;
  occurredAt: string;
}

// Matches NodalMerge.Studio.Contracts.Domain.ProviderRetryAttemptedPayload.
interface ProviderRetryPayload {
  agentId: string;
  provider?: string | null;
  statusCode?: number | null;
  attemptNumber: number;
  maxAttempts: number;
  delayMs: number;
  reason: string;
}

interface FindingSignal {
  findingId: string;
  title: string;
  status: string;
}

interface FileLeaseInfo {
  path: string;
  holderWorkUnitId: string | null;
  waitQueue: string[];
}

interface GoalItem {
  goalId: string;
  goal: string;
  workUnitId: string;
  branchId: string;
  status: string;
  pauseReason?: string | null;
  updatedAt?: string | null;
}

interface ClarificationInboxItem {
  requestId: string;
  sessionId?: string | null;
  workUnitId: string;
  goal: string;
  question: string;
  context?: string | null;
  blocking: boolean;
  options: string[];
  requestedByAgentId?: string | null;
  requestedAt: string;
  status: string;
  awaitingResume: boolean;
  timeoutSeconds?: number | null;
  timeoutAt?: string | null;
  timeoutBehavior?: string | null;
}

interface ClarificationGoalMetric {
  workUnitId: string;
  goal: string;
  requests: number;
  answered: number;
  abandoned: number;
}

interface ClarificationMetrics {
  requests: number;
  answered: number;
  abandoned: number;
  perGoal: ClarificationGoalMetric[];
}

// ── Panel ──────────────────────────────────────────────────────────────────

export class ExecutionTimelinePanel implements vscode.Disposable {
  static readonly containerId = 'shell-pane-execution-timeline';

  private readonly panel: vscode.WebviewPanel;
  private readonly baseUrl: string;
  private readonly notifications: NotificationManager | undefined;
  private readonly configService: AgentConfigService | undefined;
  private readonly secrets: vscode.SecretStorage | undefined;
  private readonly lmProxyBaseUrl: string | undefined;
  private readonly getSelectedSessionId?: () => string | undefined;
  private localSessionOverride?: string;
  private pollTimer?: ReturnType<typeof setInterval>;
  private usePromotionBranch = false;

  constructor(
    panel: vscode.WebviewPanel,
    baseUrl: string,
    notifications?: NotificationManager,
    configService?: AgentConfigService,
    secrets?: vscode.SecretStorage,
    lmProxyBaseUrl?: string,
    getSelectedSessionId?: () => string | undefined,
  ) {
    this.panel         = panel;
    this.baseUrl       = baseUrl;
    this.notifications = notifications;
    this.configService = configService;
    this.secrets       = secrets;
    this.lmProxyBaseUrl = lmProxyBaseUrl;
    this.getSelectedSessionId = getSelectedSessionId;
  }

  static getFragment(): { css: string; html: string } {
    return {
      css: scopeViewCss(ET_CSS, ExecutionTimelinePanel.containerId),
      html: `<div id="${ExecutionTimelinePanel.containerId}" class="nm-shell-pane">${ET_HTML}</div>`,
    };
  }

  activate(): void {
    this.startPolling();
    void this.sendSessionPicker();
  }

  /** Immediately re-polls — used by the shell when the selected session changes. */
  async triggerPoll(): Promise<void> {
    await this.poll();
    void this.sendSessionPicker();
  }

  setSessionOverride(sessionId: string | undefined): void {
    this.localSessionOverride = sessionId;
    void this.sendSessionPicker();
    void this.poll();
  }

  private getEffectiveSessionId(): string | undefined {
    return this.localSessionOverride ?? this.getSelectedSessionId?.();
  }

  private async sendSessionPicker(): Promise<void> {
    try {
      const sessions = await this.get<Array<{ sessionId: string; status: string }>>('/studio/sessions');
      void this.panel.webview.postMessage({
        type: 'updateSessionPicker',
        panelId: ExecutionTimelinePanel.containerId,
        sessions,
        shellSessionId: this.getSelectedSessionId?.(),
        overrideSessionId: this.localSessionOverride,
      });
    } catch { /* host not ready */ }
  }

  private startPolling(): void {
    if (this.pollTimer) { return; }
    void this.poll();
    this.pollTimer = setInterval(() => { void this.poll(); }, POLL_INTERVAL_MS);
  }

  private stopPolling(): void {
    if (this.pollTimer) {
      clearInterval(this.pollTimer);
      this.pollTimer = undefined;
    }
  }

  private async poll(): Promise<void> {
    try {
      const sessionId = this.getEffectiveSessionId();
      const params = sessionId ? '?sessionId=' + encodeURIComponent(sessionId) : '';
      const emptySummary: WorkspaceSummary = { activeWorkUnits: [], activeAgents: [], pendingMerges: [], failures: [], knownGoodStates: [] };
      const emptyMetrics: ClarificationMetrics = { requests: 0, answered: 0, abandoned: 0, perGoal: [] };
      const [summary, workUnits, agents, awaitingResume, clarifications, clarificationMetrics, merges, deadLetters, fileLeases, opts, findings, goalsResp, sessionEvents, guardrailStatuses] = await Promise.all([
        this.get<WorkspaceSummary>('/studio/workspace-summary' + params).catch(() => emptySummary),
        this.get<WorkUnit[]>('/studio/workunits' + params).catch(() => [] as WorkUnit[]),
        this.get<AgentInfo[]>('/studio/agents?all=true' + (sessionId ? '&sessionId=' + encodeURIComponent(sessionId) : '')).catch(() => [] as AgentInfo[]),
        this.get<ScheduledItem[]>('/studio/scheduler/awaiting-resume').catch(() => [] as ScheduledItem[]),
        this.get<ClarificationInboxItem[]>('/studio/clarifications').catch(() => [] as ClarificationInboxItem[]),
        this.get<ClarificationMetrics>('/studio/clarifications/metrics').catch(() => emptyMetrics),
        this.get<MergeProposal[]>('/studio/merges' + params).catch(() => [] as MergeProposal[]),
        this.get<DeadLetterEntry[]>('/studio/dead-letter' + params).catch(() => [] as DeadLetterEntry[]),
        this.get<FileLeaseInfo[]>('/studio/file-leases').catch(() => [] as FileLeaseInfo[]),
        this.get<{ usePromotionBranch?: boolean; candidateBranchId?: string; defaultClarificationTimeoutSeconds?: number | null; defaultClarificationTimeoutBehavior?: string }>('/studio/options').catch(() => ({} as { usePromotionBranch?: boolean; candidateBranchId?: string })),
        this.get<FindingSignal[]>('/studio/findings?status=Open').catch(() => [] as FindingSignal[]),
        this.get<{ goals: GoalItem[] }>('/studio/goals').catch(() => ({ goals: [] as GoalItem[] })),
        // Only meaningful once a specific session is selected — the endpoint is session-scoped,
        // unlike everything else above which can span all sessions.
        (sessionId
          ? this.get<ExecutionEventDto[]>('/studio/sessions/' + encodeURIComponent(sessionId) + '/events').catch(() => [] as ExecutionEventDto[])
          : Promise.resolve([] as ExecutionEventDto[])),
        this.get<GoalGuardrailStatus[]>('/studio/goals/guardrail-status').catch(() => [] as GoalGuardrailStatus[]),
      ]);
      const providerRetries = sessionEvents
        .filter(e => e.kind === 'ProviderRetryAttempted')
        .map(e => {
          try { return { workUnitId: e.workUnitId, occurredAt: e.occurredAt, payload: JSON.parse(e.payloadJson) as ProviderRetryPayload }; }
          catch { return null; }
        })
        .filter((r): r is { workUnitId: string | null | undefined; occurredAt: string; payload: ProviderRetryPayload } => r !== null);
      const syncGraph = await this.get<{ frontierHeads: string[] }>('/studio/causal/frontier').catch(() => null);
      const goals = goalsResp?.goals ?? [];
      this.usePromotionBranch = opts.usePromotionBranch ?? false;
      void this.panel.webview.postMessage({
        type: 'data', summary, workUnits, goals, agents, awaitingResume, clarifications, clarificationMetrics, merges, deadLetters, fileLeases,
        providerRetries,
        guardrailStatuses,
        usePromotionBranch: this.usePromotionBranch,
        candidateBranchId: opts.candidateBranchId ?? 'candidate',
        syncGraph: syncGraph ?? { frontierHeads: [] },
      });
      this.notifications?.update(merges, workUnits, findings);
      void this.sendSessionPicker();
    } catch {
      // host not yet ready — suppress until healthy
    }
  }

  async handleMessage(msg: Record<string, unknown>): Promise<void> {
    try {
      switch (msg.type as string) {
        case 'createWorkUnit': {
          const goal = await vscode.window.showInputBox({
            prompt: 'Goal for the new work unit',
            placeHolder: 'e.g. Build the NodalMerge docs site',
            ignoreFocusOut: true,
          });
          if (!goal) { return; }
          const owner = await vscode.window.showInputBox({
            prompt: 'Owner (agent type or name)',
            placeHolder: 'orchestrator',
            ignoreFocusOut: true,
          });
          if (!owner) { return; }
          const reviewPolicyPick = await vscode.window.showQuickPick(
            [
              { label: '$(person) Human Required', description: 'Proposal waits for manual apply (default)', value: 'HumanRequired' },
              { label: '$(robot) Agent Approval', description: 'Reviewer agent approves; merges automatically', value: 'AgentApproval' },
              { label: '$(clock) Hybrid (5 min)', description: 'Agent approves; auto-merges after 5 min unless overridden', value: 'Hybrid' },
            ],
            { placeHolder: 'Review policy', ignoreFocusOut: true }
          );
          if (!reviewPolicyPick) { return; }

          // Slice 21c — when promotion branch is on, let the user pick the effective target;
          // "Direct" sets BypassPromotionBranch so this work unit's applies skip candidate.
          let bypassPromotionBranch = false;
          if (this.usePromotionBranch) {
            const targetPick = await vscode.window.showQuickPick(
              [
                { label: '$(git-branch) Candidate Branch', description: 'Applies land on candidate; promote to main manually (session default)', value: 'candidate' },
                { label: '$(arrow-right) Direct', description: 'Bypass candidate — apply goes directly to parent branch', value: 'direct' },
              ],
              { placeHolder: 'Apply target', ignoreFocusOut: true },
            );
            if (!targetPick) { return; }
            bypassPromotionBranch = targetPick.value === 'direct';
          }

          const repositoryPath = resolveRepositoryPath();
          await this.post('/studio/workunits', {
            goal, owner,
            reviewPolicy: reviewPolicyPick.value,
            bypassPromotionBranch,
            ...(repositoryPath ? { repositoryPath } : {}),
          });
          void this.poll();
          break;
        }
        case 'spawnAgent': {
          const prefilledWuId = msg.workUnitId as string | undefined;
          let agentType: string | undefined;
          if (this.configService) {
            const profile = await this.configService.pickProfile('Select agent profile to spawn');
            agentType = profile?.id;
          } else {
            agentType = await vscode.window.showInputBox({
              prompt:         'Agent type',
              placeHolder:    'orchestrator, worker, docs-agent…',
              ignoreFocusOut: true,
            });
          }
          if (!agentType) { return; }
          const workUnitId = prefilledWuId
            ?? await vscode.window.showInputBox({ prompt: 'Work Unit ID', ignoreFocusOut: true })
            ?? '';
          if (!workUnitId) { return; }

          let spawnBody: Record<string, string> = { agentType, workUnitId };
          if (this.configService && this.secrets && this.lmProxyBaseUrl) {
            const llm = await this.configService.resolveSpawnLlmConfig(
              agentType, this.secrets, this.lmProxyBaseUrl,
            );
            if (!llm) {
              void vscode.window.showErrorMessage(
                `NodalMerge: Profile "${agentType}" has no LLM credentials — set VS Code LM or an API key in Model & Agent Studio.`,
              );
              return;
            }
            spawnBody = { ...spawnBody, ...llm };
          } else {
            void vscode.window.showWarningMessage(
              'NodalMerge: Spawning without LLM credentials — the agent loop will not start. Use Model & Agent Studio → Quick Explore instead.',
            );
          }
          await this.post('/studio/agents/spawn', spawnBody);
          void this.poll();
          break;
        }
        case 'pauseAgent':
          await this.post('/studio/agents/' + String(msg.agentId) + '/pause', {});
          void this.poll();
          break;
        case 'resumeAgent':
          await this.post('/studio/agents/' + String(msg.agentId) + '/resume', {});
          void this.poll();
          break;
        case 'stopAgent':
          await this.post('/studio/agents/' + String(msg.agentId) + '/stop', {});
          void this.poll();
          break;
        case 'cancelWorkUnit':
          await this.post('/studio/workunits/' + String(msg.workUnitId) + '/cancel', {});
          void this.poll();
          break;
        case 'stopAll': {
          const confirmed = await vscode.window.showWarningMessage(
            'Stop all active goals, agents, and pending reviews?',
            { modal: true },
            'Stop All',
          );
          if (confirmed !== 'Stop All') { return; }
          await this.post('/studio/stop-all', {});
          void this.poll();
          break;
        }
        case 'resumeWorker':
          await this.post('/studio/scheduler/' + String(msg.workUnitId) + '/resume', {});
          void this.poll();
          break;
        case 'resumeAllWorkers':
          await this.post('/studio/scheduler/resume-all', {});
          void this.poll();
          break;
        case 'respondClarification': {
          const workUnitId = String(msg.workUnitId ?? '');
          const requestId = String(msg.requestId ?? '');
          const options = Array.isArray(msg.options) ? msg.options.map(v => String(v)) : [];
          if (!workUnitId || !requestId) { return; }

          let response: string | undefined;
          if (options.length > 0) {
            const picked = await vscode.window.showQuickPick(
              options.map(o => ({ label: o, value: o })),
              { placeHolder: 'Select clarification response', ignoreFocusOut: true },
            );
            response = picked?.value;
          } else {
            response = await vscode.window.showInputBox({
              prompt: 'Clarification response',
              placeHolder: 'Enter response for the agent',
              ignoreFocusOut: true,
            });
          }
          if (!response) { return; }

          const note = await vscode.window.showInputBox({
            prompt: 'Optional note',
            placeHolder: 'Additional context for the response (optional)',
            ignoreFocusOut: true,
          });

          await this.post('/studio/clarifications/' + encodeURIComponent(workUnitId) + '/respond', {
            requestId,
            response,
            note: note || null,
            resume: true,
            respondedBy: 'vscode-user',
          });
          void this.poll();
          break;
        }
        case 'markKnownGood': {
          const description = await vscode.window.showInputBox({
            prompt: 'Description for this Known Good State',
            placeHolder: 'e.g. post-review-clean, before-refactor',
            ignoreFocusOut: true,
          });
          if (!description) { return; }
          await this.post('/studio/state/markKnownGood', {
            branchId: msg.branchId as string,
            description,
            createdBy: 'vscode-user',
          });
          void vscode.window.showInformationMessage(`NodalMerge: Tagged "${description}" as a Known Good State.`);
          break;
        }
        case 'openMergeReview':
          void vscode.commands.executeCommand('nodalmerge.openMergeReview', msg.proposalId as string);
          break;
        case 'openConflictReview':
          void vscode.commands.executeCommand('nodalmerge.openMergeReviewConflict', msg.workUnitId as string);
          break;
        case 'retryDeadLetter':
          await this.post('/studio/dead-letter/' + String(msg.entryId) + '/retry', {});
          void this.poll();
          break;
        case 'replanDeadLetter': {
          // Phase 1.4 — re-plan-the-slice: spawns a real bounded planner turn server-side, so this
          // can take several seconds; the panel just re-polls after, same as retryDeadLetter above.
          const replanResult = (await this.post(
            '/studio/dead-letter/' + String(msg.entryId) + '/replan',
            {},
          )) as { newWorkUnitIds?: string[] };
          const count = replanResult.newWorkUnitIds?.length ?? 0;
          void vscode.window.showInformationMessage(
            'NodalMerge: Re-plan complete — ' + count + ' new sub-slice' + (count === 1 ? '' : 's') + ' created.',
          );
          void this.poll();
          break;
        }
        case 'continueDeadLetter': {
          // Phase 1.4 Continue-track — resumes the SAME work unit with its own prior
          // conversation reconstructed, so this also spawns a real bounded worker turn
          // server-side and can take a while; re-poll after, same pattern as retry/replan.
          // A non-2xx outcome (NotApplicable/NotCompleted) throws from post() and is
          // surfaced by this method's own outer catch, same as every other action here.
          await this.post('/studio/dead-letter/' + String(msg.entryId) + '/continue', {});
          void vscode.window.showInformationMessage('NodalMerge: Continue completed successfully.');
          void this.poll();
          break;
        }
        case 'releaseFileLease': {
          const confirmed = await vscode.window.showWarningMessage(
            'Force-release every file lease held by "' + String(msg.workUnitId) + '"? The next queued worker will be promoted automatically.',
            { modal: true },
            'Release',
          );
          if (confirmed !== 'Release') { return; }
          await this.post('/studio/file-leases/release', { workUnitId: msg.workUnitId });
          void this.poll();
          break;
        }
        case 'dashboardGoalPause': {
          const goalId = String(msg.goalId ?? '');
          if (!goalId) { return; }
          const reason = await vscode.window.showInputBox({
            prompt: 'Reason for pausing (optional)',
            placeHolder: 'e.g. needs review before continuing',
            ignoreFocusOut: true,
          });
          await this.post('/studio/goals/' + encodeURIComponent(goalId) + '/pause', {
            reason: reason || null,
            pausedBy: 'vscode-user',
          });
          void this.poll();
          break;
        }
        case 'dashboardGoalResume': {
          const goalId = String(msg.goalId ?? '');
          if (!goalId) { return; }
          const steering = await vscode.window.showInputBox({
            prompt: 'Steering message (optional) — redirect the agent on resume',
            placeHolder: 'e.g. focus on the auth module, skip UI changes',
            ignoreFocusOut: true,
          });
          await this.post('/studio/goals/' + encodeURIComponent(goalId) + '/resume', {
            steering: steering || null,
            resumedBy: 'vscode-user',
          });
          void this.poll();
          break;
        }
      }
    } catch (err) {
      void vscode.window.showErrorMessage('NodalMerge: ' + String(err));
    }
  }

  private async get<T>(path: string): Promise<T> {
    const res = await fetch(this.baseUrl + path);
    if (!res.ok) { throw new Error('GET ' + path + ' → ' + String(res.status)); }
    return res.json() as Promise<T>;
  }

  private async post(path: string, body: unknown): Promise<unknown> {
    const res = await fetch(this.baseUrl + path, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
    });
    if (!res.ok) {
      const text = await res.text();
      throw new Error('POST ' + path + ' → ' + String(res.status) + ': ' + text);
    }
    return res.json();
  }

  dispose(): void {
    this.stopPolling();
  }
}

// ── HTML builder ───────────────────────────────────────────────────────────

const ET_CSS = `
  :root {
    --nm-bg:         var(--vscode-editor-background);
    --nm-fg:         var(--vscode-editor-foreground);
    --nm-border:     var(--vscode-widget-border, #444);
    --nm-section-bg: var(--vscode-sideBar-background, var(--vscode-editor-background));
    --nm-btn:        var(--vscode-button-background);
    --nm-btn-fg:     var(--vscode-button-foreground);
    --nm-btn-hover:  var(--vscode-button-hoverBackground);
    --nm-badge:      var(--vscode-badge-background);
    --nm-badge-fg:   var(--vscode-badge-foreground);
    --nm-font:       var(--vscode-font-family);
    --nm-mono:       var(--vscode-editor-font-family, monospace);
    --nm-size:       var(--vscode-font-size, 13px);
    --nm-success:    #4dac26;
    --nm-warn:       #cca700;
    --nm-error:      #f14c4c;
  }
  * { box-sizing: border-box; }
  body {
    background: var(--nm-bg);
    color: var(--nm-fg);
    font-family: var(--nm-font);
    font-size: var(--nm-size);
    margin: 0;
    padding: 0 16px 32px;
  }
  h2 {
    font-size: 0.8em;
    font-weight: 700;
    text-transform: uppercase;
    letter-spacing: 0.08em;
    opacity: 0.55;
    margin: 24px 0 8px;
    border-bottom: 1px solid var(--nm-border);
    padding-bottom: 4px;
  }
  .header {
    display: flex;
    align-items: center;
    justify-content: space-between;
    padding: 14px 0 4px;
    border-bottom: 1px solid var(--nm-border);
    margin-bottom: 4px;
  }
  .header-title { font-size: 1.15em; font-weight: 700; }
  .session-override-picker {
    font-size: 0.75em;
    padding: 1px 4px;
    border: 1px solid var(--nm-border);
    border-radius: 3px;
    background: var(--vscode-input-background, #3c3c3c);
    color: var(--vscode-input-foreground, #ccc);
    max-width: 150px;
  }
  .pulse {
    display: inline-block;
    width: 7px; height: 7px; border-radius: 50%;
    background: var(--nm-success);
    margin-left: 7px;
    vertical-align: middle;
    animation: pulse 2s ease-in-out infinite;
  }
  @keyframes pulse { 0%,100%{opacity:1} 50%{opacity:0.25} }
  #last-updated { font-size: 0.76em; opacity: 0.38; }
  .empty { opacity: 0.42; font-style: italic; padding: 4px 0 2px; font-size: 0.9em; }
  .card {
    background: var(--nm-section-bg);
    border: 1px solid var(--nm-border);
    border-radius: 4px;
    padding: 9px 12px;
    margin-bottom: 5px;
  }
  .row { display: flex; align-items: center; gap: 8px; flex-wrap: wrap; }
  .row + .row { margin-top: 4px; }
  .title {
    font-weight: 600;
    flex: 1;
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }
  .mono { font-family: var(--nm-mono); font-size: 0.85em; opacity: 0.65; }
  .badge {
    border-radius: 10px;
    padding: 1px 8px;
    font-size: 0.76em;
    white-space: nowrap;
    background: var(--nm-badge);
    color: var(--nm-badge-fg);
  }
  .badge.active   { background: var(--nm-success); color: #fff; }
  .badge.paused   { background: var(--nm-warn); color: #000; }
  .badge.stopped,
  .badge.completed,
  .badge.cancelled { background: #555; color: #ccc; }
  .badge.failed   { background: var(--nm-error); color: #fff; }
  .badge.interrupted { background: #c05020; color: #fff; }
  .actions { display: flex; gap: 4px; flex-shrink: 0; }
  button {
    background: var(--nm-btn);
    color: var(--nm-btn-fg);
    border: none;
    border-radius: 3px;
    padding: 2px 9px;
    font-size: 0.8em;
    cursor: pointer;
    font-family: var(--nm-font);
    line-height: 1.6;
  }
  button:hover { background: var(--nm-btn-hover); }
  button.ghost {
    background: transparent;
    color: var(--nm-fg);
    border: 1px solid var(--nm-border);
  }
  button.ghost:hover { background: color-mix(in srgb, var(--nm-border) 60%, transparent); }
  button.danger { background: var(--nm-error); color: #fff; }
  button.danger:hover { filter: brightness(1.15); }
  .add-btn {
    margin-top: 6px;
    width: 100%;
    background: transparent;
    color: var(--nm-fg);
    border: 1px dashed var(--nm-border);
    padding: 5px;
    font-size: 0.82em;
    opacity: 0.6;
    border-radius: 3px;
  }
  .add-btn:hover { opacity: 1; }
  .sync-graph-card {
    background: var(--nm-section-bg);
    border: 1px solid var(--nm-border);
    border-radius: 4px;
    padding: 8px 12px;
    margin-bottom: 6px;
  }
  .sync-graph-card .sg-label {
    font-size: 0.78em;
    text-transform: uppercase;
    letter-spacing: 0.06em;
    opacity: 0.55;
    margin-bottom: 4px;
  }
  .sync-graph-card .sg-badge {
    display: inline-block;
    background: var(--nm-badge);
    color: var(--nm-badge-fg);
    border-radius: 3px;
    padding: 1px 6px;
    font-size: 0.75em;
    margin-right: 4px;
  }
  .sync-graph-card .sg-heads {
    font-family: var(--nm-mono);
    font-size: 0.72em;
    opacity: 0.7;
    margin-top: 4px;
    word-break: break-all;
  }
  .sg-promoted { color: var(--nm-success); font-weight: 600; }
  .sg-empty    { color: var(--nm-warn); }
`;

const ET_HTML = `
  <div class="header">
    <span class="header-title">Activity Center<span class="pulse"></span></span>
    <select id="et-session-override" class="session-override-picker"><option value="">Follow Workspace</option></select>
    <button class="danger" id="btn-stop-all" title="Cancel every active goal, stop every agent, and cancel pending review timers">🛑 Stop All</button>
    <span id="last-updated"></span>
  </div>

  <h2>Active Goals</h2>
  <div id="active-goals"><p class="empty">Loading…</p></div>
  <button class="add-btn" id="btn-new-goal">+ New Goal</button>

  <h2>Running Agents</h2>
  <div id="agents"><p class="empty">No running agents.</p></div>
  <button class="add-btn" id="btn-start-agent">+ Start Agent</button>

  <h2>Awaiting Resume</h2>
  <div id="awaiting-resume"><p class="empty">Nothing awaiting resume.</p></div>
  <button class="add-btn" id="btn-resume-all" style="display:none">↺ Resume All</button>

  <h2>Clarification Inbox</h2>
  <div id="clarifications"><p class="empty">No active clarification requests.</p></div>
  <div id="clarification-metrics" class="empty">No clarification metrics yet.</div>

  <h2>Pending Decisions</h2>
  <div id="decisions"><p class="empty">No pending decisions.</p></div>

  <h2>Blocked Explorations</h2>
  <div id="blocked"><p class="empty">No blocked explorations.</p></div>

  <h2>File Lease Conflicts</h2>
  <div id="file-leases"><p class="empty">No file lease conflicts.</p></div>

  <h2>Sync Graph</h2>
  <div id="sync-graph"><p class="empty">Loading…</p></div>
`;