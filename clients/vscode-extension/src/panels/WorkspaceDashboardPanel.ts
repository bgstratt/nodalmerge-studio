import * as vscode from 'vscode';
import type { MergeProposal } from './MergeReviewPanel';
import type { NotificationManager } from '../NotificationManager';
import type { AgentConfigService } from '../AgentConfigService';
import { resolveRepositoryPath } from '../repositoryPath';
import { scopeViewCss, wrapViewScript } from './sharedWebviewChrome';

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

  static getFragment(): { css: string; html: string; script: string } {
    return {
      css: scopeViewCss(ET_CSS, ExecutionTimelinePanel.containerId),
      html: `<div id="${ExecutionTimelinePanel.containerId}" class="nm-shell-pane">${ET_HTML}</div>`,
      script: wrapViewScript(ET_JS, ExecutionTimelinePanel.containerId),
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

const ET_JS = `
  const vscode = acquireVsCodeApi();
  var globalUsePromotionBranch = false;
  var globalCandidateBranchId = 'candidate';

  document.getElementById('btn-new-goal').addEventListener('click', function() {
    vscode.postMessage({ type: 'createWorkUnit' });
  });
  document.getElementById('btn-start-agent').addEventListener('click', function() {
    vscode.postMessage({ type: 'spawnAgent' });
  });
  document.getElementById('btn-resume-all').addEventListener('click', function() {
    vscode.postMessage({ type: 'resumeAllWorkers' });
  });
  document.getElementById('btn-stop-all').addEventListener('click', function() {
    vscode.postMessage({ type: 'stopAll' });
  });

  var etSessionOverride = document.getElementById('et-session-override');
  if (etSessionOverride) {
    etSessionOverride.addEventListener('change', function() {
      vscode.postMessage({ type: 'sessionOverrideChanged', panelId: 'shell-pane-execution-timeline', sessionId: etSessionOverride.value || undefined });
    });
  }

  function esc(str) {
    return String(str || '')
      .replace(/&/g, '&')
      .replace(/</g, '<')
      .replace(/>/g, '>')
      .replace(/"/g, '"')
      .replace(/'/g, '&#39;');
  }

  function badge(status) {
    var s = (status || '').toLowerCase();
    return '<span class="badge ' + s + '">' + esc(status || '—') + '</span>';
  }

  // isGoalStore=true when items come from /studio/goals (have goalId + Paused status);
  // false when falling back to /studio/workunits (no goalId, no goal-level pause support).
  function renderActiveGoals(goals, isGoalStore, guardrailStatuses) {
    var el = document.getElementById('active-goals');
    if (!goals || !goals.length) {
      el.innerHTML = '<p class="empty">No active goals.</p>';
      return;
    }
    // Phase 2 item 1 — alert-only guardrail badge. Keyed by workUnitId since that's the one ID
    // both the goal-store and work-unit-fallback shapes always carry (goalId only exists in the
    // former). Never disables or hides anything else on the card — this is purely informational.
    var guardrailByWorkUnit = {};
    for (var gi = 0; gi < (guardrailStatuses || []).length; gi++) {
      guardrailByWorkUnit[guardrailStatuses[gi].workUnitId] = guardrailStatuses[gi];
    }
    var html = '';
    for (var i = 0; i < goals.length; i++) {
      var g = goals[i];
      var goalId = g.goalId || g.workUnitId;
      var gr = guardrailByWorkUnit[g.workUnitId];
      var status = (g.status || '').toLowerCase();
      var isPaused   = status === 'paused';
      var isReviewing = status === 'reviewing';
      var isTerminal  = ['cancelled', 'completed', 'merged', 'abandoned', 'converged'].indexOf(status) !== -1;
      html += '<div class="card">';
      html += '<div class="row">';
      html += '<span class="title" title="' + esc(g.goal) + '">' + esc(g.goal) + '</span>';
      html += badge(g.status);
      html += '<div class="actions">';
      if (isGoalStore && isPaused) {
        html += '<button class="ghost" data-action="resumeGoal" data-gid="' + esc(goalId) + '" style="color:var(--nm-success);border-color:var(--nm-success)">▶ Resume</button>';
      } else if (isGoalStore && !isTerminal) {
        html += '<button class="ghost" data-action="pauseGoal" data-gid="' + esc(goalId) + '" style="color:var(--nm-warn);border-color:var(--nm-warn)">⏸ Pause</button>';
      }
      if (isReviewing) {
        html += '<button class="ghost" data-action="openConflictReview" data-wu="' + esc(g.workUnitId) + '">View Conflict →</button>';
      }
      if (!isTerminal && !isPaused) {
        html += '<button class="ghost" data-action="spawnAgent" data-wu="' + esc(g.workUnitId) + '">Spawn</button>';
      }
      html += '<button class="ghost" data-action="markKnownGood" data-wu="' + esc(g.workUnitId) + '" data-branch="' + esc(g.branchId) + '" title="Tag this work unit\\'s current branch as a Known Good State">Tag KGS</button>';
      if (!isTerminal) {
        html += '<button class="danger" data-action="cancelWorkUnit" data-wu="' + esc(g.workUnitId) + '">Stop</button>';
      }
      html += '</div>';
      html += '</div>';
      if (isPaused && g.pauseReason) {
        html += '<div class="row"><span class="mono" style="color:var(--nm-warn)">⏸ ' + esc(g.pauseReason) + '</span></div>';
      }
      if (gr && (gr.tokensExceeded || gr.durationExceeded)) {
        var grParts = [];
        if (gr.tokensExceeded) { grParts.push(gr.totalTokens.toLocaleString() + ' tokens (cap ' + gr.maxGoalTokens.toLocaleString() + ')'); }
        if (gr.durationExceeded) { grParts.push(Math.round(gr.elapsedMinutes) + ' min (cap ' + gr.maxGoalDurationMinutes + ')'); }
        html += '<div class="row"><span class="mono" style="color:var(--nm-warn)" title="Alert only — this goal keeps running; guardrails never auto-stop work">⚠ Guardrail exceeded: ' + esc(grParts.join(', ')) + '</span></div>';
      }
      html += '<div class="row">';
      html += '<span class="mono">' + esc(g.workUnitId) + '</span>';
      html += '<span class="mono">fork: ' + esc(g.branchId) + '</span>';
      if (g.owner) { html += '<span class="mono">owner: ' + esc(g.owner) + '</span>'; }
      if (g.reviewPolicy && g.reviewPolicy !== 'HumanRequired') {
        var rp = g.reviewPolicy === 'AgentApproval' ? '🤖 Agent Approval' : '⏱ Hybrid';
        html += '<span class="badge reviewing">' + rp + '</span>';
      }
      if (globalUsePromotionBranch) {
        html += '<span class="badge" title="Applies land on ' + esc(globalCandidateBranchId) + '; promote to main manually">→ ' + esc(globalCandidateBranchId) + '</span>';
      }
      html += '</div>';
      html += '</div>';
    }
    el.innerHTML = html;
    el.querySelectorAll('[data-action="pauseGoal"]').forEach(function(btn) {
      btn.addEventListener('click', function() {
        vscode.postMessage({ type: 'dashboardGoalPause', goalId: btn.getAttribute('data-gid') });
      });
    });
    el.querySelectorAll('[data-action="resumeGoal"]').forEach(function(btn) {
      btn.addEventListener('click', function() {
        vscode.postMessage({ type: 'dashboardGoalResume', goalId: btn.getAttribute('data-gid') });
      });
    });
    el.querySelectorAll('[data-action="spawnAgent"]').forEach(function(btn) {
      btn.addEventListener('click', function() {
        vscode.postMessage({ type: 'spawnAgent', workUnitId: btn.getAttribute('data-wu') });
      });
    });
    el.querySelectorAll('[data-action="openConflictReview"]').forEach(function(btn) {
      btn.addEventListener('click', function() {
        vscode.postMessage({ type: 'openConflictReview', workUnitId: btn.getAttribute('data-wu') });
      });
    });
    el.querySelectorAll('[data-action="cancelWorkUnit"]').forEach(function(btn) {
      btn.addEventListener('click', function() {
        vscode.postMessage({ type: 'cancelWorkUnit', workUnitId: btn.getAttribute('data-wu') });
      });
    });
    el.querySelectorAll('[data-action="markKnownGood"]').forEach(function(btn) {
      btn.addEventListener('click', function() {
        vscode.postMessage({ type: 'markKnownGood', workUnitId: btn.getAttribute('data-wu'), branchId: btn.getAttribute('data-branch') });
      });
    });
  }

  function renderAgents(agents, goals) {
    var el = document.getElementById('agents');
    if (!agents || !agents.length) {
      el.innerHTML = '<p class="empty">No running agents.</p>';
      return;
    }
    var goalMap = {};
    for (var j = 0; j < (goals || []).length; j++) { goalMap[goals[j].workUnitId] = goals[j]; }
    var html = '';
    for (var i = 0; i < agents.length; i++) {
      var a = agents[i];
      var wu = goalMap[a.workUnitId];
      var statusLower = (a.status || '').toLowerCase();
      var isPaused = statusLower === 'paused';
      var isInterrupted = statusLower === 'interrupted';
      html += '<div class="card">';
      html += '<div class="row">';
      html += '<span class="title mono">' + esc(a.agentId) + '</span>';
      html += badge(a.status);
      html += '<div class="actions">';
      // Phase 11 — deep-links into Goal Workspace's Decision Lens Conversation tab; the
      // transcript is durable, so this is offered regardless of pause/interrupted/active state.
      html += '<button class="ghost" data-action="viewTranscript" data-wu="' + esc(a.workUnitId) + '">View live transcript</button>';
      if (isInterrupted) {
        html += '<button class="ghost" data-action="resumeInterrupted" data-wu="' + esc(a.workUnitId) + '">↺ Resume</button>';
      } else if (isPaused) {
        html += '<button class="ghost" data-action="resumeAgent" data-id="' + esc(a.agentId) + '">Resume</button>';
        html += '<button class="danger" data-action="stopAgent" data-id="' + esc(a.agentId) + '">Stop</button>';
      } else {
        html += '<button class="ghost" data-action="pauseAgent" data-id="' + esc(a.agentId) + '">Pause</button>';
        html += '<button class="danger" data-action="stopAgent" data-id="' + esc(a.agentId) + '">Stop</button>';
      }
      html += '</div>';
      html += '</div>';
      if (statusLower === 'active' && a.currentActivity) {
        html += '<div class="row"><span class="pulse"></span><span class="mono">' + esc(a.currentActivity) + '</span></div>';
      }
      if (wu) {
        html += '<div class="row"><span class="mono">' + esc(wu.goal) + '</span></div>';
      }
      html += '</div>';
    }
    el.innerHTML = html;
    el.querySelectorAll('[data-action="resumeInterrupted"]').forEach(function(btn) {
      btn.addEventListener('click', function() {
        vscode.postMessage({ type: 'spawnAgent', workUnitId: btn.getAttribute('data-wu') });
      });
    });
    el.querySelectorAll('[data-action="viewTranscript"]').forEach(function(btn) {
      btn.addEventListener('click', function() {
        vscode.postMessage({ type: 'activityViewTranscript', workUnitId: btn.getAttribute('data-wu') });
      });
    });
    el.querySelectorAll('[data-action="pauseAgent"],[data-action="resumeAgent"],[data-action="stopAgent"]').forEach(function(btn) {
      btn.addEventListener('click', function() {
        vscode.postMessage({ type: btn.getAttribute('data-action'), agentId: btn.getAttribute('data-id') });
      });
    });
  }

  // Phase 8c — worker-level scheduler items a Host restart interrupted mid-execution. Mirrors
  // the orchestrator-level "Interrupted" card above: no silent auto-resume, a human must click
  // Resume (or Resume All for a busy fan-out with many interrupted children).
  function renderAwaitingResume(items) {
    var el = document.getElementById('awaiting-resume');
    var resumeAllBtn = document.getElementById('btn-resume-all');
    if (!items || !items.length) {
      el.innerHTML = '<p class="empty">Nothing awaiting resume.</p>';
      resumeAllBtn.style.display = 'none';
      return;
    }
    resumeAllBtn.style.display = '';
    var html = '';
    for (var i = 0; i < items.length; i++) {
      var it = items[i];
      html += '<div class="card">';
      html += '<div class="row">';
      html += '<span class="title mono">' + esc(it.workUnitId) + '</span>';
      html += '<span class="badge">' + esc(it.profileId) + '</span>';
      html += '<div class="actions">';
      html += '<button class="ghost" data-action="resumeWorker" data-wu="' + esc(it.workUnitId) + '">↺ Resume</button>';
      html += '</div>';
      html += '</div>';
      html += '</div>';
    }
    el.innerHTML = html;
    el.querySelectorAll('[data-action="resumeWorker"]').forEach(function(btn) {
      btn.addEventListener('click', function() {
        vscode.postMessage({ type: 'resumeWorker', workUnitId: btn.getAttribute('data-wu') });
      });
    });
  }

  function relTime(iso) {
    var ms = Date.now() - Date.parse(iso);
    if (!isFinite(ms)) { return 'unknown age'; }
    var mins = Math.floor(ms / 60000);
    if (mins < 1) { return 'just now'; }
    if (mins < 60) { return mins + 'm ago'; }
    var hrs = Math.floor(mins / 60);
    if (hrs < 24) { return hrs + 'h ago'; }
    var days = Math.floor(hrs / 24);
    return days + 'd ago';
  }

  function renderClarifications(items, metrics) {
    var el = document.getElementById('clarifications');
    if (!items || !items.length) {
      el.innerHTML = '<p class="empty">No active clarification requests.</p>';
    } else {
      // Group by goal label, preserving insertion order.
      var groupOrder = [];
      var groups = {};
      for (var i = 0; i < items.length; i++) {
        var c = items[i];
        var key = c.goal || c.workUnitId;
        if (!groups[key]) { groups[key] = []; groupOrder.push(key); }
        groups[key].push(c);
      }
      var html = '';
      for (var gi = 0; gi < groupOrder.length; gi++) {
        var groupKey = groupOrder[gi];
        var groupItems = groups[groupKey];
        html += '<div style="margin-bottom:10px">';
        html += '<div style="font-size:0.75em;opacity:0.55;text-transform:uppercase;letter-spacing:0.06em;margin-bottom:4px;padding:0 2px">' + esc(groupKey) + '</div>';
        for (var j = 0; j < groupItems.length; j++) {
          var c = groupItems[j];
          var statusBadge = c.awaitingResume ? 'awaiting' : c.status;
          html += '<div class="card">';
          html += '<div class="row">';
          html += '<span class="title mono" title="' + esc(c.requestId) + '">' + esc(c.question) + '</span>';
          html += '<span class="badge ' + (c.awaitingResume ? 'paused' : '') + '">' + esc(statusBadge) + '</span>';
          html += '<div class="actions">';
          html += '<button class="ghost" data-action="respondClarification" data-rid="' + esc(c.requestId) + '" data-wu="' + esc(c.workUnitId) + '" style="color:var(--nm-success);border-color:var(--nm-success)">Respond</button>';
          html += '</div>';
          html += '</div>';
          if (c.context) {
            html += '<div class="row"><span class="mono" style="opacity:0.7">context: ' + esc(c.context) + '</span></div>';
          }
          if (c.options && c.options.length) {
            html += '<div class="row"><span class="mono">options: ' + esc(c.options.join(' | ')) + '</span></div>';
          }
          if (c.timeoutAt) {
            var now = Date.now();
            var timeoutMs = new Date(c.timeoutAt).getTime() - now;
            var timeoutLabel = timeoutMs > 0
              ? 'auto-' + esc(c.timeoutBehavior || 'continue') + ' in ' + Math.ceil(timeoutMs / 1000) + 's'
              : 'timed out (auto-' + esc(c.timeoutBehavior || 'continue') + ')';
            html += '<div class="row"><span class="mono" style="color:var(--nm-warn)">⏱ ' + timeoutLabel + '</span></div>';
          }
          html += '<div class="row">';
          html += '<span class="mono">age: ' + esc(relTime(c.requestedAt)) + '</span>';
          html += '<span class="mono">blocking: ' + esc(String(!!c.blocking)) + '</span>';
          if (c.sessionId) { html += '<span class="mono">session: ' + esc(c.sessionId) + '</span>'; }
          html += '</div>';
          html += '</div>';
        }
        html += '</div>';
      }
      el.innerHTML = html;
      el.querySelectorAll('[data-action="respondClarification"]').forEach(function(btn) {
        btn.addEventListener('click', function() {
          var rid = btn.getAttribute('data-rid');
          var wu = btn.getAttribute('data-wu');
          var found = null;
          for (var j = 0; j < items.length; j++) {
            if (String(items[j].requestId) === String(rid)) { found = items[j]; break; }
          }
          vscode.postMessage({ type: 'respondClarification', requestId: rid, workUnitId: wu, options: found ? found.options : [] });
        });
      });
    }

    var met = document.getElementById('clarification-metrics');
    if (!metrics) {
      met.innerHTML = '<p class="empty">No clarification metrics yet.</p>';
      return;
    }
    var top = (metrics.perGoal || []).slice(0, 5).map(function(g) {
      return '<span class="mono">' + esc(g.goal) + ': ' + esc(String(g.requests)) + ' req / ' + esc(String(g.answered)) + ' answered / ' + esc(String(g.abandoned)) + ' abandoned</span>';
    }).join('<br/>');
    met.innerHTML =
      '<div class="card">' +
      '<div class="row"><span class="mono">requests: ' + esc(String(metrics.requests || 0)) + '</span>' +
      '<span class="mono">answered: ' + esc(String(metrics.answered || 0)) + '</span>' +
      '<span class="mono">abandoned: ' + esc(String(metrics.abandoned || 0)) + '</span></div>' +
      (top ? '<div class="row">' + top + '</div>' : '<div class="row"><span class="empty">No per-goal data.</span></div>') +
      '</div>';
  }

  var DECISION_STATUS_COLOR = {
    draft:          '',
    readyforreview: 'active',
    approved:       'active',
    rejected:       'failed',
    merged:         'stopped',
  };

  function renderPendingDecisions(merges) {
    var el = document.getElementById('decisions');
    if (!merges || !merges.length) {
      el.innerHTML = '<p class="empty">No pending decisions.</p>';
      return;
    }
    var html = '';
    for (var i = 0; i < merges.length; i++) {
      var m = merges[i];
      var statusKey = (m.status || '').toLowerCase().replace(/\\s+/g, '');
      var badgeClass = 'badge ' + (DECISION_STATUS_COLOR[statusKey] || '');
      var canReview = statusKey === 'readyforreview' || statusKey === 'approved' || statusKey === 'draft' || statusKey === 'proposed' || statusKey === 'executing' || statusKey === 'merge';
      html += '<div class="card">';
      html += '<div class="row">';
      html += '<span class="title" title="' + esc(m.goal) + '">' + esc(m.goal) + '</span>';
      html += '<span class="' + badgeClass + '">' + esc(m.status) + '</span>';
      if (canReview) {
        html += '<div class="actions">';
        html += '<button class="ghost" data-action="openMergeReview" data-pid="' + esc(m.proposalId) + '">Review Decision →</button>';
        html += '</div>';
      }
      html += '</div>';
      html += '<div class="row">';
      html += '<span class="mono">' + esc(m.sourceBranch) + ' → ' + esc(m.targetBranch) + '</span>';
      html += '</div>';
      html += '</div>';
    }
    el.innerHTML = html;
    el.querySelectorAll('[data-action="openMergeReview"]').forEach(function(btn) {
      btn.addEventListener('click', function() {
        vscode.postMessage({ type: 'openMergeReview', proposalId: btn.getAttribute('data-pid') });
      });
    });
  }

  // Groups by workUnitId so a work unit that failed more than once (e.g. "max iterations"
  // then, after a manual retry, a transient provider error) shows as ONE card with a history
  // trail, instead of one confusing card per attempt with no visible relationship between them.
  function renderBlockedExplorations(deadLetters, goals, providerRetries) {
    var el = document.getElementById('blocked');
    if (!deadLetters || !deadLetters.length) {
      el.innerHTML = '<p class="empty">No blocked explorations.</p>';
      return;
    }
    var goalMap = {};
    for (var j = 0; j < (goals || []).length; j++) { goalMap[goals[j].workUnitId] = goals[j]; }

    var groups = {};
    for (var g = 0; g < deadLetters.length; g++) {
      var entry = deadLetters[g];
      (groups[entry.workUnitId] = groups[entry.workUnitId] || []).push(entry);
    }

    var retryCounts = {};
    for (var r = 0; r < (providerRetries || []).length; r++) {
      var pr = providerRetries[r];
      if (!pr.workUnitId) { continue; }
      retryCounts[pr.workUnitId] = (retryCounts[pr.workUnitId] || 0) + 1;
    }

    var html = '';
    Object.keys(groups).forEach(function(workUnitId) {
      var chain = groups[workUnitId].slice().sort(function(a, b) { return String(a.occurredAt).localeCompare(String(b.occurredAt)); });
      var latest = chain[chain.length - 1];
      var earlier = chain.slice(0, -1);
      var wu = goalMap[workUnitId];
      var goal = wu ? wu.goal : workUnitId;
      var canRetry = !latest.maxAttemptsReached && latest.attemptCount < 3;
      var transientRetries = retryCounts[workUnitId] || 0;
      // Phase 1.4 two-track design: MaxIterationsExceeded is the Continue track — both of its
      // options are implemented now: "Continue" (resume the same work unit with reconstructed
      // prior context) and "Re-plan the slice" (decompose into fresh, independently-budgeted
      // siblings). Everything else is the Retry track ("re-plan from scratch" alongside plain
      // Retry). Same replan call either way, label differs only to match how a human would
      // describe what just happened. Unlike Retry, re-planning is deliberately NOT gated on
      // attempt count — it never resumes this same work unit; Continue does resume it, so it's
      // gated on canRetry the same way Retry is.
      var replanLabel = latest.kind === 'MaxIterationsExceeded' ? 'Re-plan the slice' : 'Re-plan from scratch';
      var isMaxIterations = latest.kind === 'MaxIterationsExceeded';

      html += '<div class="card">';
      html += '<div class="row">';
      html += '<span class="title" title="' + esc(goal) + '">' + esc(goal) + '</span>';
      html += badge('failed');
      html += '<div class="actions">';
      if (canRetry) {
        html += '<button class="ghost" data-action="retryDeadLetter" data-id="' + esc(latest.entryId) + '">Retry</button>';
        if (isMaxIterations) {
          html += '<button class="ghost" data-action="continueDeadLetter" data-id="' + esc(latest.entryId) + '">Continue</button>';
        }
      } else {
        html += '<span class="mono" style="opacity:0.6">Max attempts reached</span>';
      }
      html += '<button class="ghost" data-action="replanDeadLetter" data-id="' + esc(latest.entryId) + '">' + esc(replanLabel) + '</button>';
      html += '</div></div>';
      html += '<div class="row">';
      html += '<span class="mono">phase: ' + esc(latest.stage) + '</span>';
      html += '<span class="mono">model: ' + esc(latest.profileId) + '</span>';
      html += '<span class="mono">attempt ' + esc(String(latest.attemptCount)) + '/3</span>';
      if (transientRetries > 0) {
        html += '<span class="mono" title="Transient provider errors (e.g. rate limit/overload) retried automatically before this failure">' + esc(String(transientRetries)) + ' transient retr' + (transientRetries === 1 ? 'y' : 'ies') + '</span>';
      }
      html += '</div>';
      html += '<div class="row"><span class="mono">' + esc(latest.reason) + '</span></div>';
      if (earlier.length > 0) {
        html += '<details><summary class="mono" style="cursor:pointer;opacity:0.7">' + earlier.length + ' earlier attempt' + (earlier.length === 1 ? '' : 's') + '</summary>';
        for (var e = 0; e < earlier.length; e++) {
          html += '<div class="row" style="opacity:0.7"><span class="mono">' + esc(new Date(earlier[e].occurredAt).toLocaleString()) + ' — ' + esc(earlier[e].reason) + '</span></div>';
        }
        html += '</details>';
      }
      html += '</div>';
    });
    el.innerHTML = html;
    el.querySelectorAll('[data-action="retryDeadLetter"]').forEach(function(btn) {
      btn.addEventListener('click', function() {
        vscode.postMessage({ type: 'retryDeadLetter', entryId: btn.getAttribute('data-id') });
      });
    });
    el.querySelectorAll('[data-action="replanDeadLetter"]').forEach(function(btn) {
      btn.addEventListener('click', function() {
        vscode.postMessage({ type: 'replanDeadLetter', entryId: btn.getAttribute('data-id') });
      });
    });
    el.querySelectorAll('[data-action="continueDeadLetter"]').forEach(function(btn) {
      btn.addEventListener('click', function() {
        vscode.postMessage({ type: 'continueDeadLetter', entryId: btn.getAttribute('data-id') });
      });
    });
  }

  // Phase 12 — only leases with a non-empty wait queue are actionable (a held lease with no
  // waiter is just normal in-flight work); "Force Release" is the manual-override path for a
  // holder that crashed mid-write, leaving no live agent to Stop and no proposal to reject.
  function renderFileLeases(leases, goals) {
    var el = document.getElementById('file-leases');
    var contested = (leases || []).filter(function(l) { return l.waitQueue && l.waitQueue.length > 0; });
    if (!contested.length) {
      el.innerHTML = '<p class="empty">No file lease conflicts.</p>';
      return;
    }
    var goalMap = {};
    for (var j = 0; j < (goals || []).length; j++) { goalMap[goals[j].workUnitId] = goals[j]; }
    function label(workUnitId) {
      var wu = goalMap[workUnitId];
      return wu ? wu.goal : workUnitId;
    }
    var html = '';
    for (var i = 0; i < contested.length; i++) {
      var l = contested[i];
      html += '<div class="card">';
      html += '<div class="row">';
      html += '<span class="title mono" title="' + esc(l.path) + '">' + esc(l.path) + '</span>';
      html += '<span class="badge interrupted">' + esc(String(l.waitQueue.length)) + ' waiting</span>';
      html += '<div class="actions">';
      html += '<button class="danger" data-action="releaseFileLease" data-wu="' + esc(l.holderWorkUnitId) + '">Force Release</button>';
      html += '</div>';
      html += '</div>';
      html += '<div class="row"><span class="mono">held by: ' + esc(label(l.holderWorkUnitId)) + '</span></div>';
      html += '<div class="row"><span class="mono">queued: ' + esc(l.waitQueue.map(label).join(', ')) + '</span></div>';
      html += '</div>';
    }
    el.innerHTML = html;
    el.querySelectorAll('[data-action="releaseFileLease"]').forEach(function(btn) {
      btn.addEventListener('click', function() {
        vscode.postMessage({ type: 'releaseFileLease', workUnitId: btn.getAttribute('data-wu') });
      });
    });
  }

  window.addEventListener('message', function(event) {
    var msg = event.data;
    if (msg.type === 'updateSessionPicker' && msg.panelId === 'shell-pane-execution-timeline') {
      var sel = document.getElementById('et-session-override');
      if (sel) {
        var shellLabel = msg.shellSessionId ? ' (' + String(msg.shellSessionId).slice(0, 8) + '…)' : '';
        sel.innerHTML = '<option value="">Follow Workspace' + esc(shellLabel) + '</option>';
        for (var i = 0; i < (msg.sessions || []).length; i++) {
          var s = msg.sessions[i];
          var opt = document.createElement('option');
          opt.value = s.sessionId;
          opt.textContent = String(s.sessionId).slice(0, 12) + '… (' + s.status + ')';
          sel.appendChild(opt);
        }
        sel.value = msg.overrideSessionId || '';
      }
      return;
    }
    if (msg.type !== 'data') { return; }
    if (typeof msg.usePromotionBranch !== 'undefined') {
      globalUsePromotionBranch = !!msg.usePromotionBranch;
      globalCandidateBranchId = msg.candidateBranchId || 'candidate';
    }
    renderActiveGoals(msg.goals && msg.goals.length ? msg.goals : msg.workUnits, !!msg.goals, msg.guardrailStatuses || []);
    renderAgents(msg.agents, msg.workUnits);
    renderAwaitingResume(msg.awaitingResume || []);
    renderClarifications(msg.clarifications || [], msg.clarificationMetrics || null);
    renderPendingDecisions(msg.merges);
    renderBlockedExplorations(msg.deadLetters || [], msg.workUnits, msg.providerRetries || []);
    renderFileLeases(msg.fileLeases || [], msg.workUnits);
    renderSyncGraph(msg.syncGraph || { frontierHeads: [] });
    var ts = document.getElementById('last-updated');
    if (ts) { ts.textContent = 'updated ' + new Date().toLocaleTimeString(); }
  });

  function renderSyncGraph(data) {
    var el = document.getElementById('sync-graph');
    if (!el) { return; }
    var heads = (data && data.frontierHeads) ? data.frontierHeads : [];
    if (heads.length === 0) {
      el.innerHTML = '<div class="sync-graph-card"><div class="sg-label">CRDT Causal Graph</div>' +
        '<span class="sg-empty">No promoted checkpoints — frontier is empty.</span></div>';
      return;
    }
    var headsHtml = heads.map(function(h) {
      return '<span class="sg-badge">' + h.slice(0, 8) + '…' + h.slice(-4) + '</span>';
    }).join('');
    el.innerHTML = '<div class="sync-graph-card">' +
      '<div class="sg-label">CRDT Causal Graph</div>' +
      '<span class="sg-promoted">&#x25CF; ' + heads.length + ' frontier head' + (heads.length === 1 ? '' : 's') + '</span>' +
      '<div class="sg-heads">' + headsHtml + '</div>' +
      '</div>';
  }
`;