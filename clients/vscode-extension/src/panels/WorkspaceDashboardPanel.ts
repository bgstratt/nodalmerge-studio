import * as vscode from 'vscode';
import type { MergeProposal } from './MergeReviewPanel';
import type { NotificationManager } from '../NotificationManager';
import type { AgentConfigService } from '../AgentConfigService';

const POLL_INTERVAL_MS = 2_000;

// ── Domain types matching Studio Host REST responses ───────────────────────

interface WorkUnit {
  workUnitId: string;
  branchId: string;
  goal: string;
  owner: string;
  status: string;
  successCriteria?: string | null;
}

interface AgentInfo {
  agentId: string;
  workUnitId: string;
  status: string;
}

interface WorkspaceSummary {
  activeWorkUnits: string[];
  activeAgents: string[];
  pendingMerges: string[];
  failures: string[];
  knownGoodStates: string[];
}

// ── Panel ──────────────────────────────────────────────────────────────────

export class WorkspaceDashboardPanel implements vscode.Disposable {
  static current: WorkspaceDashboardPanel | undefined;
  private static readonly viewType = 'nodalmerge.dashboard';

  private readonly panel: vscode.WebviewPanel;
  private readonly baseUrl: string;
  private readonly notifications: NotificationManager | undefined;
  private readonly configService: AgentConfigService | undefined;
  private readonly secrets: vscode.SecretStorage | undefined;
  private readonly lmProxyBaseUrl: string | undefined;
  private readonly disposables: vscode.Disposable[] = [];
  private pollTimer?: ReturnType<typeof setInterval>;

  private constructor(
    panel: vscode.WebviewPanel,
    baseUrl: string,
    notifications?: NotificationManager,
    configService?: AgentConfigService,
    secrets?: vscode.SecretStorage,
    lmProxyBaseUrl?: string,
  ) {
    this.panel         = panel;
    this.baseUrl       = baseUrl;
    this.notifications = notifications;
    this.configService = configService;
    this.secrets       = secrets;
    this.lmProxyBaseUrl = lmProxyBaseUrl;
    this.panel.webview.html = buildDashboardHtml();
    this.panel.onDidDispose(() => this.dispose(), null, this.disposables);
    this.panel.onDidChangeViewState(e => {
      if (e.webviewPanel.visible) { this.startPolling(); }
      else { this.stopPolling(); }
    }, null, this.disposables);
    this.panel.webview.onDidReceiveMessage(
      (msg: Record<string, unknown>) => { void this.handleMessage(msg); },
      null,
      this.disposables
    );
    this.startPolling();
  }

  static createOrShow(
    baseUrl: string,
    notifications?: NotificationManager,
    configService?: AgentConfigService,
    secrets?: vscode.SecretStorage,
    lmProxyBaseUrl?: string,
  ): void {
    if (WorkspaceDashboardPanel.current) {
      WorkspaceDashboardPanel.current.panel.reveal(vscode.ViewColumn.Two);
      return;
    }
    const panel = vscode.window.createWebviewPanel(
      WorkspaceDashboardPanel.viewType,
      'NodalMerge — Workspace',
      vscode.ViewColumn.Two,
      { enableScripts: true, retainContextWhenHidden: true }
    );
    WorkspaceDashboardPanel.current = new WorkspaceDashboardPanel(
      panel, baseUrl, notifications, configService, secrets, lmProxyBaseUrl,
    );
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
      const [summary, workUnits, agents, merges] = await Promise.all([
        this.get<WorkspaceSummary>('/studio/workspace-summary'),
        this.get<WorkUnit[]>('/studio/workunits'),
        this.get<AgentInfo[]>('/studio/agents?all=true'),
        this.get<MergeProposal[]>('/studio/merges'),
      ]);
      void this.panel.webview.postMessage({ type: 'data', summary, workUnits, agents, merges });
      this.notifications?.update(merges);
    } catch {
      // host not yet ready — suppress until healthy
    }
  }

  private async handleMessage(msg: Record<string, unknown>): Promise<void> {
    try {
      switch (msg.type as string) {
        case 'createWorkUnit': {
          const goal = await vscode.window.showInputBox({
            prompt: 'Work unit goal',
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
          const repositoryPath = vscode.workspace.workspaceFolders?.[0]?.uri?.fsPath;
          await this.post('/studio/workunits', { goal, owner, ...(repositoryPath ? { repositoryPath } : {}) });
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
                `NodalMerge: Profile "${agentType}" has no LLM credentials — set VS Code LM or an API key in Agent Config.`,
              );
              return;
            }
            spawnBody = { ...spawnBody, ...llm };
          } else {
            void vscode.window.showWarningMessage(
              'NodalMerge: Spawning without LLM credentials — the agent loop will not start. Use Agent Config → Quick Spawn instead.',
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
        case 'openMergeReview':
          void vscode.commands.executeCommand('nodalmerge.openMergeReview', msg.proposalId as string);
          break;
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
    WorkspaceDashboardPanel.current = undefined;
    this.panel.dispose();
    for (const d of this.disposables) { d.dispose(); }
    this.disposables.length = 0;
  }
}

// ── HTML builder ───────────────────────────────────────────────────────────

function buildNonce(): string {
  let text = '';
  const chars = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789';
  for (let i = 0; i < 32; i++) { text += chars[Math.floor(Math.random() * chars.length)]; }
  return text;
}

function buildDashboardHtml(): string {
  const n = buildNonce();
  return [
    '<!DOCTYPE html>',
    '<html lang="en">',
    '<head>',
    '  <meta charset="UTF-8">',
    '  <meta http-equiv="Content-Security-Policy"',
    '        content="default-src \'none\'; style-src \'nonce-' + n + '\'; script-src \'nonce-' + n + '\';">',
    '  <meta name="viewport" content="width=device-width, initial-scale=1.0">',
    '  <title>NodalMerge Studio</title>',
    '  <style nonce="' + n + '">',
    DASHBOARD_CSS,
    '  </style>',
    '</head>',
    '<body>',
    DASHBOARD_HTML,
    '<script nonce="' + n + '">',
    DASHBOARD_JS,
    '</script>',
    '</body>',
    '</html>',
  ].join('\n');
}

const DASHBOARD_CSS = `
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
  .badge.completed { background: #555; color: #ccc; }
  .badge.failed   { background: var(--nm-error); color: #fff; }
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

const DASHBOARD_HTML = `
  <div class="header">
    <span class="header-title">NodalMerge Studio<span class="pulse"></span></span>
    <span id="last-updated"></span>
  </div>

  <h2>Work Units</h2>
  <div id="work-units"><p class="empty">Loading…</p></div>
  <button class="add-btn" id="btn-new-wu">+ New Work Unit</button>

  <h2>Agents</h2>
  <div id="agents"><p class="empty">No agents.</p></div>
  <button class="add-btn" id="btn-spawn">+ Spawn Agent</button>

  <h2>Pending Merges</h2>
  <div id="merges"><p class="empty">No pending merges.</p></div>

  <h2>Failures</h2>
  <div id="failures"><p class="empty">No failures.</p></div>
`;

const DASHBOARD_JS = `
  const vscode = acquireVsCodeApi();

  document.getElementById('btn-new-wu').addEventListener('click', function() {
    vscode.postMessage({ type: 'createWorkUnit' });
  });
  document.getElementById('btn-spawn').addEventListener('click', function() {
    vscode.postMessage({ type: 'spawnAgent' });
  });

  function esc(str) {
    return String(str || '')
      .replace(/&/g, '&amp;')
      .replace(/</g, '&lt;')
      .replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;')
      .replace(/'/g, '&#39;');
  }

  function badge(status) {
    var s = (status || '').toLowerCase();
    return '<span class="badge ' + s + '">' + esc(status || '—') + '</span>';
  }

  function renderWorkUnits(wus) {
    var el = document.getElementById('work-units');
    if (!wus || !wus.length) {
      el.innerHTML = '<p class="empty">No work units yet.</p>';
      return;
    }
    var html = '';
    for (var i = 0; i < wus.length; i++) {
      var wu = wus[i];
      html += '<div class="card">';
      html += '<div class="row">';
      html += '<span class="title" title="' + esc(wu.goal) + '">' + esc(wu.goal) + '</span>';
      html += badge(wu.status);
      html += '<div class="actions">';
      html += '<button class="ghost" data-action="spawnAgent" data-wu="' + esc(wu.workUnitId) + '">Spawn</button>';
      html += '</div>';
      html += '</div>';
      html += '<div class="row">';
      html += '<span class="mono">' + esc(wu.workUnitId) + '</span>';
      html += '<span class="mono">branch: ' + esc(wu.branchId) + '</span>';
      html += '<span class="mono">owner: ' + esc(wu.owner) + '</span>';
      html += '</div>';
      html += '</div>';
    }
    el.innerHTML = html;
    el.querySelectorAll('[data-action="spawnAgent"]').forEach(function(btn) {
      btn.addEventListener('click', function() {
        vscode.postMessage({ type: 'spawnAgent', workUnitId: btn.getAttribute('data-wu') });
      });
    });
  }

  function renderAgents(agents, wus) {
    var el = document.getElementById('agents');
    if (!agents || !agents.length) {
      el.innerHTML = '<p class="empty">No agents.</p>';
      return;
    }
    var wuMap = {};
    for (var j = 0; j < (wus || []).length; j++) { wuMap[wus[j].workUnitId] = wus[j]; }
    var html = '';
    for (var i = 0; i < agents.length; i++) {
      var a = agents[i];
      var wu = wuMap[a.workUnitId];
      var isPaused = (a.status || '').toLowerCase() === 'paused';
      html += '<div class="card">';
      html += '<div class="row">';
      html += '<span class="title mono">' + esc(a.agentId) + '</span>';
      html += badge(a.status);
      html += '<div class="actions">';
      if (isPaused) {
        html += '<button class="ghost" data-action="resumeAgent" data-id="' + esc(a.agentId) + '">Resume</button>';
      } else {
        html += '<button class="ghost" data-action="pauseAgent" data-id="' + esc(a.agentId) + '">Pause</button>';
      }
      html += '<button class="danger" data-action="stopAgent" data-id="' + esc(a.agentId) + '">Stop</button>';
      html += '</div>';
      html += '</div>';
      if (wu) {
        html += '<div class="row"><span class="mono">' + esc(wu.goal) + '</span></div>';
      }
      html += '</div>';
    }
    el.innerHTML = html;
    el.querySelectorAll('[data-action]').forEach(function(btn) {
      btn.addEventListener('click', function() {
        vscode.postMessage({ type: btn.getAttribute('data-action'), agentId: btn.getAttribute('data-id') });
      });
    });
  }

  var MERGE_STATUS_COLOR = {
    draft:          '',
    readyforreview: 'active',
    approved:       'active',
    rejected:       'failed',
    merged:         'stopped',
  };

  function renderMerges(merges) {
    var el = document.getElementById('merges');
    if (!merges || !merges.length) {
      el.innerHTML = '<p class="empty">No merge proposals.</p>';
      return;
    }
    var html = '';
    for (var i = 0; i < merges.length; i++) {
      var m = merges[i];
      var statusKey = (m.status || '').toLowerCase().replace(/\\s+/g, '');
      var badgeClass = 'badge ' + (MERGE_STATUS_COLOR[statusKey] || '');
      var canReview = statusKey === 'readyforreview' || statusKey === 'approved' || statusKey === 'draft';
      html += '<div class="card">';
      html += '<div class="row">';
      html += '<span class="title" title="' + esc(m.goal) + '">' + esc(m.goal) + '</span>';
      html += '<span class="' + badgeClass + '">' + esc(m.status) + '</span>';
      if (canReview) {
        html += '<div class="actions">';
        html += '<button class="ghost" data-action="openMergeReview" data-pid="' + esc(m.proposalId) + '">Review →</button>';
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

  function renderFailures(failures) {
    var el = document.getElementById('failures');
    if (!failures || !failures.length) {
      el.innerHTML = '<p class="empty">No failures.</p>';
      return;
    }
    var html = '';
    for (var i = 0; i < failures.length; i++) {
      html += '<div class="card"><div class="row"><span class="mono title">' + esc(failures[i]) + '</span></div></div>';
    }
    el.innerHTML = html;
  }

  window.addEventListener('message', function(event) {
    var msg = event.data;
    if (msg.type !== 'data') { return; }
    renderWorkUnits(msg.workUnits);
    renderAgents(msg.agents, msg.workUnits);
    renderMerges(msg.merges);
    renderFailures(msg.summary.failures);
    var ts = document.getElementById('last-updated');
    if (ts) { ts.textContent = 'updated ' + new Date().toLocaleTimeString(); }
  });
`;
