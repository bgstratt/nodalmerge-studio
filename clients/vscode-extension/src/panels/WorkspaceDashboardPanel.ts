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
      const [summary, workUnits, agents, awaitingResume, merges, deadLetters, fileLeases, opts, findings] = await Promise.all([
        this.get<WorkspaceSummary>('/studio/workspace-summary' + params),
        this.get<WorkUnit[]>('/studio/workunits' + params),
        this.get<AgentInfo[]>('/studio/agents?all=true' + (sessionId ? '&sessionId=' + encodeURIComponent(sessionId) : '')),
        this.get<ScheduledItem[]>('/studio/scheduler/awaiting-resume'),
        this.get<MergeProposal[]>('/studio/merges' + params),
        this.get<DeadLetterEntry[]>('/studio/dead-letter' + params),
        this.get<FileLeaseInfo[]>('/studio/file-leases'),
        this.get<{ usePromotionBranch?: boolean; candidateBranchId?: string }>('/studio/options'),
        this.get<FindingSignal[]>('/studio/findings?status=Open'),
      ]);
      this.usePromotionBranch = opts.usePromotionBranch ?? false;
      void this.panel.webview.postMessage({
        type: 'data', summary, workUnits, agents, awaitingResume, merges, deadLetters, fileLeases,
        usePromotionBranch: this.usePromotionBranch,
        candidateBranchId: opts.candidateBranchId ?? 'candidate',
      });
      this.notifications?.update(merges, workUnits, findings);
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

  <h2>Pending Decisions</h2>
  <div id="decisions"><p class="empty">No pending decisions.</p></div>

  <h2>Blocked Explorations</h2>
  <div id="blocked"><p class="empty">No blocked explorations.</p></div>

  <h2>File Lease Conflicts</h2>
  <div id="file-leases"><p class="empty">No file lease conflicts.</p></div>
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

  function renderActiveGoals(goals) {
    var el = document.getElementById('active-goals');
    if (!goals || !goals.length) {
      el.innerHTML = '<p class="empty">No active goals.</p>';
      return;
    }
    var html = '';
    for (var i = 0; i < goals.length; i++) {
      var wu = goals[i];
      var status = (wu.status || '').toLowerCase();
      var isReviewing = status === 'reviewing';
      var isStoppable = ['cancelled', 'completed', 'merged'].indexOf(status) === -1;
      html += '<div class="card">';
      html += '<div class="row">';
      html += '<span class="title" title="' + esc(wu.goal) + '">' + esc(wu.goal) + '</span>';
      html += badge(wu.status);
      html += '<div class="actions">';
      if (isReviewing) {
        html += '<button class="ghost" data-action="openConflictReview" data-wu="' + esc(wu.workUnitId) + '">View Conflict →</button>';
      }
      html += '<button class="ghost" data-action="spawnAgent" data-wu="' + esc(wu.workUnitId) + '">Spawn</button>';
      if (isStoppable) {
        html += '<button class="danger" data-action="cancelWorkUnit" data-wu="' + esc(wu.workUnitId) + '">Stop</button>';
      }
      html += '</div>';
      html += '</div>';
      html += '<div class="row">';
      html += '<span class="mono">' + esc(wu.workUnitId) + '</span>';
      html += '<span class="mono">fork: ' + esc(wu.branchId) + '</span>';
      html += '<span class="mono">owner: ' + esc(wu.owner) + '</span>';
      if (wu.reviewPolicy && wu.reviewPolicy !== 'HumanRequired') {
        var rp = wu.reviewPolicy === 'AgentApproval' ? '🤖 Agent Approval' : '⏱ Hybrid';
        html += '<span class="badge reviewing">' + rp + '</span>';
      }
      if (globalUsePromotionBranch) {
        html += '<span class="badge" title="Applies land on ' + esc(globalCandidateBranchId) + '; promote to main manually">→ ' + esc(globalCandidateBranchId) + '</span>';
      }
      html += '</div>';
      html += '</div>';
    }
    el.innerHTML = html;
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

  function renderBlockedExplorations(deadLetters, goals) {
    var el = document.getElementById('blocked');
    if (!deadLetters || !deadLetters.length) {
      el.innerHTML = '<p class="empty">No blocked explorations.</p>';
      return;
    }
    var goalMap = {};
    for (var j = 0; j < (goals || []).length; j++) { goalMap[goals[j].workUnitId] = goals[j]; }
    var html = '';
    for (var i = 0; i < deadLetters.length; i++) {
      var dl = deadLetters[i];
      var wu = goalMap[dl.workUnitId];
      var goal = wu ? wu.goal : dl.workUnitId;
      var canRetry = !dl.maxAttemptsReached && dl.attemptCount < 3;
      html += '<div class="card">';
      html += '<div class="row">';
      html += '<span class="title" title="' + esc(goal) + '">' + esc(goal) + '</span>';
      html += badge('failed');
      html += '<div class="actions">';
      if (canRetry) {
        html += '<button class="ghost" data-action="retryDeadLetter" data-id="' + esc(dl.entryId) + '">Retry</button>';
      } else {
        html += '<span class="mono" style="opacity:0.6">Max attempts reached</span>';
      }
      html += '</div></div>';
      html += '<div class="row">';
      html += '<span class="mono">phase: ' + esc(dl.stage) + '</span>';
      html += '<span class="mono">model: ' + esc(dl.profileId) + '</span>';
      html += '<span class="mono">attempt ' + esc(String(dl.attemptCount)) + '/3</span>';
      html += '</div>';
      html += '<div class="row"><span class="mono">' + esc(dl.reason) + '</span></div>';
      html += '</div>';
    }
    el.innerHTML = html;
    el.querySelectorAll('[data-action="retryDeadLetter"]').forEach(function(btn) {
      btn.addEventListener('click', function() {
        vscode.postMessage({ type: 'retryDeadLetter', entryId: btn.getAttribute('data-id') });
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
    renderActiveGoals(msg.workUnits);
    renderAgents(msg.agents, msg.workUnits);
    renderAwaitingResume(msg.awaitingResume || []);
    renderPendingDecisions(msg.merges);
    renderBlockedExplorations(msg.deadLetters || [], msg.workUnits);
    renderFileLeases(msg.fileLeases || [], msg.workUnits);
    var ts = document.getElementById('last-updated');
    if (ts) { ts.textContent = 'updated ' + new Date().toLocaleTimeString(); }
  });
`;