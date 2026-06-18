import * as vscode from 'vscode';
import { scopeViewCss, wrapViewScript } from './sharedWebviewChrome';
import type { AgentConfigService } from '../AgentConfigService';
import type { ProposalFileChange } from './MergeReviewPanel';
import { COMMANDS } from '../constants';

const POLL_INTERVAL_MS = 2_000;

// ── Domain types matching Studio Host REST responses ───────────────────────

interface WorkUnitFanOutInfo {
  sliceId?: string | null;
  seedFromBranchId?: string | null;
  // Slice 14b — set when a BeforeEnqueue policy rule (e.g. non-overlapping fileScope) rejected
  // this slice. Only meaningful while status is still "Created"; stale once it later enqueues.
  blockedReason?: string | null;
}

interface WorkUnit {
  workUnitId: string;
  goal: string;
  branchId: string;
  status: string;
  parentWorkUnitId?: string | null;
  dependsOn: string[];
  fileScope: string[];
  currentStage?: string | null;
  owner: string;
  assignedAgent?: string | null;
  successCriteria?: string | null;
  branchedFromProposalId?: string | null;
  proposalCount: number;
  fanOutInfo?: WorkUnitFanOutInfo | null;
}

interface StudioOptions {
  useLlmProfileSelection: boolean;
  blockOverlappingFileScope: boolean;
  maxConcurrentWorkers: number;
  schedulerPollIntervalMs: number;
}

interface ExecutionSession {
  sessionId: string;
  rootWorkUnitId: string;
  status: string;
  startedAt: string;
}

interface ArtifactRef {
  artifactId: string;
  type: string;
  parentArtifactId?: string | null;
  status: string;
  createdAt: string;
  ownedByWorkUnitId?: string | null;
  ownedByAgentId?: string | null;
  title?: string | null;
  body?: string | null;
}

interface OrchestrationEvent {
  eventId: string;
  workUnitId: string;
  orchestratorAgentId: string;
  inputStage: string;
  inputProjectionSnapshot: string;
  action: string;
  spawnedIds: string[];
  reason?: string | null;
  occurredAt: string;
}

interface ProposalDetail {
  proposalId: string;
  sourceBranch: string;
  goal: string;
  status: string;
  confidence?: number | null;
  workUnitId?: string | null;
  filesTouched?: string[];
}

// ── Panel ──────────────────────────────────────────────────────────────────

export class ArtifactExplorerPanel {
  static readonly containerId = 'shell-pane-home';

  private readonly panel: vscode.WebviewPanel;
  private readonly baseUrl: string;
  private readonly configService: AgentConfigService | undefined;
  private readonly secrets: vscode.SecretStorage | undefined;
  private readonly lmProxyBaseUrl: string | undefined;
  private pollTimer?: ReturnType<typeof setInterval>;
  private selectedSessionId?: string;

  constructor(
    panel: vscode.WebviewPanel,
    baseUrl: string,
    configService?: AgentConfigService,
    secrets?: vscode.SecretStorage,
    lmProxyBaseUrl?: string,
  ) {
    this.panel          = panel;
    this.baseUrl         = baseUrl;
    this.configService   = configService;
    this.secrets         = secrets;
    this.lmProxyBaseUrl  = lmProxyBaseUrl;
  }

  static getFragment(): { css: string; html: string; script: string } {
    return {
      css: scopeViewCss(EXPLORER_CSS, ArtifactExplorerPanel.containerId),
      html: `<div id="${ArtifactExplorerPanel.containerId}" class="nm-shell-pane active">${EXPLORER_HTML}</div>`,
      script: wrapViewScript(EXPLORER_JS, ArtifactExplorerPanel.containerId),
    };
  }

  activate(): void {
    void this.sendTemplates();
    void this.sendWsInit();
    void this.sendSettings();
    void this.refreshSessions();
    this.pollTimer = setInterval(() => {
      void this.refreshSessions();
      if (this.selectedSessionId) { void this.refreshTree(this.selectedSessionId); }
    }, POLL_INTERVAL_MS);
  }

  dispose(): void {
    if (this.pollTimer) { clearInterval(this.pollTimer); this.pollTimer = undefined; }
  }

  private async sendTemplates(): Promise<void> {
    if (!this.configService) { return; }
    void this.panel.webview.postMessage({
      type: 'templates',
      templates: this.configService.getTemplates(),
    });
  }

  // Slice 12c — live stage badges. The webview-side script opens this WebSocket itself (same
  // room the DAG Replay pane already connects to: clients/vscode-extension/src/panels/
  // DagReplayPanel.ts), so all it needs from the extension host is the URL.
  private async sendWsInit(): Promise<void> {
    void this.panel.webview.postMessage({
      type: 'explorerWsInit',
      wsUrl: this.baseUrl.replace(/^http/, 'ws') + '/ws/runtime',
    });
  }

  private async sendSettings(): Promise<void> {
    try {
      const opts = await this.get<StudioOptions>('/studio/options');
      void this.panel.webview.postMessage({ type: 'explorerSettings', ...opts });
    } catch {
      // host not ready yet — same suppress-and-poll-later convention as refreshSessions
    }
  }

  // Slice 14e — UpdateOptionsBody on the host side has defaults for every field but
  // useLlmProfileSelection, so a partial POST silently resets whatever's omitted back to that
  // default instead of leaving it alone. Fetch-merge-post avoids that regardless of which single
  // setting the gear panel just changed.
  private async updateOptions(patch: Partial<StudioOptions>): Promise<void> {
    const current = await this.get<StudioOptions>('/studio/options');
    const updated = await this.post<StudioOptions>('/studio/options', { ...current, ...patch });
    void this.panel.webview.postMessage({ type: 'explorerSettings', ...updated });
  }

  private async refreshSessions(): Promise<void> {
    try {
      const sessions = await this.get<ExecutionSession[]>('/studio/sessions');
      void this.panel.webview.postMessage({
        type: 'sessions', sessions, selectedSessionId: this.selectedSessionId ?? '',
      });
    } catch {
      // host not ready yet — suppress until healthy, same convention as WorkspaceDashboardPanel
    }
  }

  private async refreshTree(sessionId: string): Promise<void> {
    try {
      const workUnits = await this.get<WorkUnit[]>('/studio/sessions/' + sessionId + '/workunits');
      void this.panel.webview.postMessage({ type: 'tree', sessionId, workUnits });
    } catch {
      // session may have just been created and not yet visible — next poll picks it up
    }
  }

  private async loadTimeline(workUnitId: string): Promise<void> {
    try {
      const [artifacts, events] = await Promise.all([
        this.get<ArtifactRef[]>('/studio/workunits/' + workUnitId + '/artifacts'),
        this.get<OrchestrationEvent[]>('/studio/workunits/' + workUnitId + '/orchestration-events'),
      ]);
      void this.panel.webview.postMessage({ type: 'timeline', workUnitId, artifacts, events });
    } catch (err) {
      void vscode.window.showErrorMessage('NodalMerge: failed to load timeline — ' + String(err));
    }
  }

  private async loadProposal(proposalId: string): Promise<void> {
    try {
      const proposal = await this.get<ProposalDetail>('/studio/merges/' + proposalId);
      void this.panel.webview.postMessage({ type: 'proposal', proposal });
    } catch (err) {
      void vscode.window.showErrorMessage('NodalMerge: failed to load proposal — ' + String(err));
    }
  }

  async handleMessage(msg: Record<string, unknown>): Promise<void> {
    try {
      switch (msg.type as string) {
        case 'explorerSelectSession':
          this.selectedSessionId = (msg.sessionId as string) || undefined;
          if (this.selectedSessionId) { await this.refreshTree(this.selectedSessionId); }
          break;
        case 'explorerRun':
          await this.handleRun(msg.templateName as string, msg.goal as string);
          break;
        case 'explorerSelectWorkUnit':
          await this.loadTimeline(msg.workUnitId as string);
          break;
        case 'explorerSelectProposal':
          await this.loadProposal(msg.proposalId as string);
          break;
        case 'explorerWorkUnitAction':
          await this.handleWorkUnitAction(
            msg.action as string, msg.workUnitId as string,
          );
          break;
        case 'explorerProposalAction':
          await this.handleProposalAction(
            msg.action as string,
            msg.proposalId as string,
            (msg.candidates as { proposalId: string; title?: string }[] | undefined) ?? [],
          );
          break;
        case 'explorerSetUseLlmProfileSelection':
          await this.updateOptions({ useLlmProfileSelection: msg.value as boolean });
          break;
        case 'explorerSetMaxConcurrentWorkers':
          await this.updateOptions({ maxConcurrentWorkers: msg.value as number });
          break;
        case 'explorerSetSchedulerPollIntervalMs':
          await this.updateOptions({ schedulerPollIntervalMs: msg.value as number });
          break;
        default:
          return;
      }
    } catch (err) {
      void vscode.window.showErrorMessage('NodalMerge: ' + String(err));
    }
  }

  private async handleRun(templateName: string, goal: string): Promise<void> {
    if (!goal || !goal.trim()) {
      void vscode.window.showWarningMessage('NodalMerge: enter a goal before running.');
      return;
    }
    const templates = this.configService?.getTemplates() ?? [];
    const template = templates.find(t => t.name === templateName);
    if (!template) {
      void vscode.window.showErrorMessage(`NodalMerge: Template "${templateName}" not found.`);
      void this.panel.webview.postMessage({ type: 'runResult', success: false, message: 'Template not found.' });
      return;
    }
    if (!this.configService || !this.secrets || !this.lmProxyBaseUrl) {
      void vscode.window.showWarningMessage(
        'NodalMerge: Spawning without LLM credentials is not possible from here — configure Agent Config first.',
      );
      void this.panel.webview.postMessage({ type: 'runResult', success: false, message: 'No LLM credentials configured.' });
      return;
    }

    try {
      const orchCfg = await this.configService.resolveSpawnLlmConfig(
        template.orchestrator, this.secrets, this.lmProxyBaseUrl,
      );
      if (!orchCfg) {
        throw new Error(
          `Profile "${template.orchestrator}" is missing LLM credentials — set it up in Agent Config.`,
        );
      }

      const repositoryPath = vscode.workspace.workspaceFolders?.[0]?.uri?.fsPath;
      const rootWu = await this.post<{ workUnitId: string }>('/studio/workunits', {
        goal,
        owner: template.orchestrator,
        ...(repositoryPath ? { repositoryPath } : {}),
      });

      const session = await this.post<ExecutionSession>('/studio/sessions', {
        rootWorkUnitId: rootWu.workUnitId,
        profileIds: [template.orchestrator],
      });

      await this.post('/studio/agents/spawn', {
        agentType: 'orchestrator',
        workUnitId: rootWu.workUnitId,
        ...orchCfg,
      });

      this.selectedSessionId = session.sessionId;
      void this.panel.webview.postMessage({ type: 'runResult', success: true, sessionId: session.sessionId });
      await this.refreshSessions();
      await this.refreshTree(session.sessionId);
      void vscode.window.showInformationMessage(`NodalMerge: Started "${goal}".`);
    } catch (err) {
      void this.panel.webview.postMessage({ type: 'runResult', success: false, message: String(err) });
      void vscode.window.showErrorMessage('NodalMerge: Run failed — ' + String(err));
    }
  }

  private async handleWorkUnitAction(action: string, workUnitId: string): Promise<void> {
    if (action === 'split') {
      const goalA = await vscode.window.showInputBox({
        prompt: 'Goal for the first child work unit', ignoreFocusOut: true,
      });
      if (!goalA) { return; }
      const scopeARaw = await vscode.window.showInputBox({
        prompt: 'File scope for the first child (comma-separated, optional)', ignoreFocusOut: true,
      });
      const goalB = await vscode.window.showInputBox({
        prompt: 'Goal for the second child work unit', ignoreFocusOut: true,
      });
      if (!goalB) { return; }
      const scopeBRaw = await vscode.window.showInputBox({
        prompt: 'File scope for the second child (comma-separated, optional)', ignoreFocusOut: true,
      });
      const parseScope = (s: string | undefined) =>
        s ? s.split(',').map(x => x.trim()).filter(Boolean) : undefined;

      await this.post('/studio/workunits', {
        goal: goalA, owner: 'user', parentWorkUnitId: workUnitId, fileScope: parseScope(scopeARaw),
      });
      await this.post('/studio/workunits', {
        goal: goalB, owner: 'user', parentWorkUnitId: workUnitId, fileScope: parseScope(scopeBRaw),
      });
      void vscode.window.showInformationMessage('NodalMerge: Split into 2 child work units.');
      if (this.selectedSessionId) { await this.refreshTree(this.selectedSessionId); }
      return;
    }

    if (action === 'rerun') {
      const profile = await this.configService?.pickProfile('Select profile to re-run this work unit');
      if (!profile) { return; }
      await this.post('/studio/scheduler/enqueue', { workUnitId, profileId: profile.id });
      void vscode.window.showInformationMessage('NodalMerge: Re-enqueued work unit.');
      return;
    }

    if (action === 'branchLatest') {
      const artifacts = await this.get<ArtifactRef[]>('/studio/workunits/' + workUnitId + '/artifacts');
      const proposals = artifacts.filter(a => a.type === 'MergeProposal');
      if (proposals.length === 0) {
        void vscode.window.showWarningMessage('NodalMerge: no proposal found for this work unit yet.');
        return;
      }
      const latest = proposals[proposals.length - 1];
      await this.branchFromProposal(latest.artifactId);
    }
  }

  private async handleProposalAction(
    action: string, proposalId: string, candidates: { proposalId: string; title?: string }[],
  ): Promise<void> {
    if (action === 'openReview') {
      void vscode.commands.executeCommand(COMMANDS.OPEN_MERGE_REVIEW, proposalId);
      return;
    }
    if (action === 'branch') {
      await this.branchFromProposal(proposalId);
      return;
    }
    if (action === 'restore') {
      const result = await this.post<{ branchId: string }>(
        '/studio/merges/' + proposalId + '/restore-workspace', {});
      const changesRes = await this.get<{ fileChanges: ProposalFileChange[] }>(
        '/studio/merges/' + proposalId + '/file-changes');
      let opened = 0;
      for (const fc of changesRes.fileChanges ?? []) {
        if (fc.beforeContent == null) { continue; }
        const lang = fc.path.includes('.') ? fc.path.split('.').pop() : 'plaintext';
        const doc = await vscode.workspace.openTextDocument({ language: lang, content: fc.beforeContent });
        await vscode.window.showTextDocument(doc, { preview: false });
        opened++;
      }
      void vscode.window.showInformationMessage(
        'NodalMerge: Restored workspace to branch ' + result.branchId
        + ' (' + opened + ' file(s) opened read-only).');
      return;
    }
    if (action === 'compare') {
      if (candidates.length === 0) {
        void vscode.window.showWarningMessage('NodalMerge: no other proposals on this work unit to compare with.');
        return;
      }
      const picked = await vscode.window.showQuickPick(
        candidates.map(c => ({ label: c.title || c.proposalId, detail: c.proposalId, candidate: c })),
        { placeHolder: 'Compare with…' },
      );
      if (!picked) { return; }
      const compareResult = await this.get<unknown>(
        '/studio/merges/compare?ids=' + encodeURIComponent(proposalId + ',' + picked.candidate.proposalId));
      void this.panel.webview.postMessage({ type: 'compareResult', result: compareResult });
    }
  }

  private async branchFromProposal(proposalId: string): Promise<void> {
    const goal = await vscode.window.showInputBox({
      prompt: 'Goal for the new branch', placeHolder: 'e.g. Retry with a different model', ignoreFocusOut: true,
    });
    if (!goal) { return; }
    const profile = await this.configService?.pickProfile('Select profile to run the new branch');
    if (!profile) { return; }
    const result = await this.post<{ workUnitId: string }>(
      '/studio/merges/' + proposalId + '/branch',
      { goal, profileId: profile.id, ...(this.selectedSessionId ? { sessionId: this.selectedSessionId } : {}) });
    void vscode.window.showInformationMessage('NodalMerge: Branched new work unit ' + result.workUnitId + '.');
    if (this.selectedSessionId) { await this.refreshTree(this.selectedSessionId); }
  }

  private async get<T>(path: string): Promise<T> {
    const res = await fetch(this.baseUrl + path);
    if (!res.ok) { throw new Error('GET ' + path + ' → ' + String(res.status)); }
    return res.json() as Promise<T>;
  }

  private async post<T = unknown>(path: string, body: unknown): Promise<T> {
    const res = await fetch(this.baseUrl + path, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
    });
    if (!res.ok) {
      const text = await res.text();
      throw new Error('POST ' + path + ' → ' + String(res.status) + ': ' + text);
    }
    return res.json() as Promise<T>;
  }
}

// ── HTML builder ───────────────────────────────────────────────────────────

const EXPLORER_CSS = `
  :root {
    --nm-bg:         var(--vscode-editor-background);
    --nm-fg:         var(--vscode-editor-foreground);
    --nm-border:     var(--vscode-widget-border, #444);
    --nm-section-bg: var(--vscode-sideBar-background, var(--vscode-editor-background));
    --nm-btn:        var(--vscode-button-background);
    --nm-btn-fg:     var(--vscode-button-foreground);
    --nm-btn-hover:  var(--vscode-button-hoverBackground);
    --nm-input-bg:   var(--vscode-input-background, #3c3c3c);
    --nm-input-fg:   var(--vscode-input-foreground, #ccc);
    --nm-input-bdr:  var(--vscode-input-border, #555);
    --nm-font:       var(--vscode-font-family);
    --nm-mono:       var(--vscode-editor-font-family, monospace);
    --nm-size:       var(--vscode-font-size, 13px);
    --nm-success:    #4dac26;
    --nm-warn:       #cca700;
    --nm-error:      #f14c4c;
    --nm-info:       var(--vscode-textLink-foreground, #3794ff);
  }
  * { box-sizing: border-box; }
  body { background: var(--nm-bg); color: var(--nm-fg); font-family: var(--nm-font); font-size: var(--nm-size); margin: 0; padding: 0; }
  :scope { display: flex; flex-direction: column; height: 100%; }
  .ex-topbar {
    flex-shrink: 0; padding: 10px 14px; border-bottom: 1px solid var(--nm-border);
    display: flex; gap: 8px; flex-wrap: wrap; align-items: flex-end;
    background: var(--nm-section-bg);
  }
  .ex-field { display: flex; flex-direction: column; gap: 2px; }
  .ex-field label { font-size: 0.72em; opacity: 0.6; text-transform: uppercase; letter-spacing: 0.05em; }
  select, textarea, input[type=text] {
    background: var(--nm-input-bg); color: var(--nm-input-fg); border: 1px solid var(--nm-input-bdr);
    border-radius: 3px; padding: 4px 6px; font-family: var(--nm-font); font-size: 0.9em;
  }
  textarea#ex-goal { width: 320px; height: 32px; min-height: 32px; resize: vertical; }
  button {
    background: var(--nm-btn); color: var(--nm-btn-fg); border: none; border-radius: 3px;
    padding: 5px 14px; font-size: 0.88em; cursor: pointer; font-family: var(--nm-font);
  }
  button:hover:not(:disabled) { background: var(--nm-btn-hover); }
  button.ghost { background: transparent; color: var(--nm-fg); border: 1px solid var(--nm-border); }
  button.ghost:hover { background: color-mix(in srgb, var(--nm-border) 50%, transparent); }
  .ex-settings-panel { flex-shrink: 0; padding: 8px 14px; border-bottom: 1px solid var(--nm-border); background: var(--nm-section-bg); }
  .ex-settings-row { display: flex; align-items: center; gap: 6px; font-size: 0.85em; cursor: pointer; }
  .ex-body { flex: 1; display: flex; overflow: hidden; min-height: 0; }
  .ex-col { overflow-y: auto; padding: 10px 12px; }
  .ex-col-tree { width: 280px; flex-shrink: 0; border-right: 1px solid var(--nm-border); }
  .ex-col-timeline { flex: 1; min-width: 0; border-right: 1px solid var(--nm-border); }
  .ex-col-inspector { width: 320px; flex-shrink: 0; }
  h2 {
    font-size: 0.78em; font-weight: 700; text-transform: uppercase; letter-spacing: 0.07em;
    opacity: 0.5; margin: 0 0 8px;
  }
  .empty { opacity: 0.42; font-style: italic; padding: 4px 0; font-size: 0.9em; }
  .wu-node { border-radius: 4px; padding: 6px 8px; margin-bottom: 3px; cursor: pointer; border: 1px solid transparent; }
  .wu-node:hover { background: color-mix(in srgb, var(--nm-border) 30%, transparent); }
  .wu-node.selected { border-color: var(--nm-info); background: color-mix(in srgb, var(--nm-info) 12%, transparent); }
  .wu-title { font-weight: 600; font-size: 0.92em; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .wu-meta { display: flex; gap: 6px; margin-top: 3px; flex-wrap: wrap; }
  .badge {
    display: inline-block; border-radius: 9px; padding: 1px 8px; font-size: 0.74em; white-space: nowrap;
    background: var(--vscode-badge-background); color: var(--vscode-badge-foreground);
  }
  .badge.completed, .badge.merged { background: var(--nm-success); color: #fff; }
  .badge.failed, .badge.deadlettered, .badge.cancelled { background: var(--nm-error); color: #fff; }
  .badge.reviewing, .badge.proposed { background: var(--nm-info); color: #fff; }
  .badge.executing, .badge.queued, .badge.retrying { background: var(--nm-warn); color: #000; }
  .badge.blocked { background: var(--nm-error); color: #fff; }
  .badge.stage { background: transparent; border: 1px solid var(--nm-border); color: var(--nm-fg); opacity: 0.8; }
  .badge.stage.plan { background: var(--nm-info); color: #fff; border-color: transparent; opacity: 1; }
  .badge.stage.execute { background: var(--nm-warn); color: #000; border-color: transparent; opacity: 1; }
  .badge.stage.review { background: #b180d7; color: #fff; border-color: transparent; opacity: 1; }
  .badge.stage.merge { background: #2da198; color: #fff; border-color: transparent; opacity: 1; }
  .tl-item { border: 1px solid var(--nm-border); border-radius: 4px; margin-bottom: 6px; padding: 6px 10px; cursor: default; }
  .tl-item.clickable { cursor: pointer; }
  .tl-item.clickable:hover { background: color-mix(in srgb, var(--nm-border) 25%, transparent); }
  .tl-kind { font-size: 0.7em; text-transform: uppercase; opacity: 0.5; letter-spacing: 0.05em; }
  .tl-title { font-size: 0.9em; margin-top: 2px; }
  .tl-time { font-size: 0.72em; opacity: 0.4; float: right; }
  .mono { font-family: var(--nm-mono); font-size: 0.85em; opacity: 0.7; }
  .meta-grid { display: grid; grid-template-columns: max-content 1fr; gap: 3px 10px; margin: 8px 0; font-size: 0.88em; }
  .meta-label { opacity: 0.55; font-size: 0.85em; }
  .inspector-actions { display: flex; gap: 6px; flex-wrap: wrap; margin-top: 12px; }
  pre.snapshot { font-family: var(--nm-mono); font-size: 0.78em; white-space: pre-wrap; word-break: break-word; background: color-mix(in srgb, var(--nm-border) 15%, transparent); padding: 6px 8px; border-radius: 3px; max-height: 220px; overflow-y: auto; }
  .diff-pre { font-family: var(--nm-mono); font-size: 0.8em; white-space: pre; overflow-x: auto; }
  .diff-add { color: var(--nm-success); }
  .diff-del { color: var(--nm-error); }
`;

const EXPLORER_HTML = `
  <div class="ex-topbar">
    <div class="ex-field">
      <label>Session</label>
      <select id="ex-session"><option value="">(no session)</option></select>
    </div>
    <div class="ex-field">
      <label>Template</label>
      <select id="ex-template"></select>
    </div>
    <div class="ex-field">
      <label>Goal</label>
      <textarea id="ex-goal" placeholder="Paste a goal — e.g. Add dark mode support across the settings UI"></textarea>
    </div>
    <button id="ex-run">&#x25B6; Run</button>
    <button id="ex-settings-btn" class="ghost" title="Settings">&#9881;</button>
  </div>
  <div id="ex-settings-panel" class="ex-settings-panel" style="display:none">
    <label class="ex-settings-row">
      <input type="checkbox" id="ex-llm-profile-checkbox"/>
      Use LLM profile selection (orchestrator asks the LLM which profile fits each child work unit)
    </label>
    <label class="ex-settings-row">
      Max concurrent workers
      <input type="number" id="ex-max-concurrent-workers" min="1" step="1" style="width:60px"/>
    </label>
    <label class="ex-settings-row">
      Scheduler poll interval (ms)
      <input type="number" id="ex-scheduler-poll-interval" min="100" step="100" style="width:80px"/>
    </label>
  </div>
  <div class="ex-body">
    <div class="ex-col ex-col-tree">
      <h2>Work Units</h2>
      <div id="ex-tree"><p class="empty">Select a session to view its work unit DAG.</p></div>
    </div>
    <div class="ex-col ex-col-timeline">
      <h2>Artifact Timeline</h2>
      <div id="ex-timeline"><p class="empty">Select a work unit to see its artifacts.</p></div>
    </div>
    <div class="ex-col ex-col-inspector">
      <h2>Inspector</h2>
      <div id="ex-inspector"><p class="empty">Nothing selected.</p></div>
    </div>
  </div>
`;

const EXPLORER_JS = `
  var vscode = acquireVsCodeApi();
  var state = { workUnits: [], selectedWorkUnitId: null, timelineArtifacts: [], timelineEvents: [] };

  function esc(s) {
    return String(s || '').replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;');
  }

  function badge(status) {
    var s = (status || '').toLowerCase().replace(/\\s+/g, '');
    return '<span class="badge ' + s + '">' + esc(status || '—') + '</span>';
  }

  // Slice 14b — blockedReason is stale once the slice has moved past Created (it enqueued, so
  // the block was resolved), so only show it while still Created.
  function isBlocked(wu) {
    return !!(wu && wu.fanOutInfo && wu.fanOutInfo.blockedReason && (wu.status || '').toLowerCase() === 'created');
  }

  function stageBadge(stage) {
    if (!stage) { return '—'; }
    var s = stage.toLowerCase();
    return '<span class="badge stage ' + s + '">' + esc(stage) + '</span>';
  }

  function fmtTime(iso) {
    try { return new Date(iso).toLocaleTimeString(); } catch (e) { return ''; }
  }

  // ── Top bar ──────────────────────────────────────────────────────────────

  document.getElementById('ex-session').addEventListener('change', function(ev) {
    vscode.postMessage({ type: 'explorerSelectSession', sessionId: ev.target.value });
    document.getElementById('ex-tree').innerHTML = '<p class="empty">Loading…</p>';
  });

  document.getElementById('ex-run').addEventListener('click', function() {
    var goal = document.getElementById('ex-goal').value.trim();
    var templateName = document.getElementById('ex-template').value;
    if (!goal) { return; }
    var btn = document.getElementById('ex-run');
    btn.disabled = true;
    btn.textContent = 'Running…';
    vscode.postMessage({ type: 'explorerRun', templateName: templateName, goal: goal });
  });

  // ── Settings (Slice 12d) ─────────────────────────────────────────────────

  document.getElementById('ex-settings-btn').addEventListener('click', function() {
    var panel = document.getElementById('ex-settings-panel');
    panel.style.display = panel.style.display === 'none' ? 'block' : 'none';
  });

  document.getElementById('ex-llm-profile-checkbox').addEventListener('change', function(ev) {
    vscode.postMessage({ type: 'explorerSetUseLlmProfileSelection', value: ev.target.checked });
  });

  document.getElementById('ex-max-concurrent-workers').addEventListener('change', function(ev) {
    var value = parseInt(ev.target.value, 10);
    if (!value || value < 1) { return; }
    vscode.postMessage({ type: 'explorerSetMaxConcurrentWorkers', value: value });
  });

  document.getElementById('ex-scheduler-poll-interval').addEventListener('change', function(ev) {
    var value = parseInt(ev.target.value, 10);
    if (!value || value < 100) { return; }
    vscode.postMessage({ type: 'explorerSetSchedulerPollIntervalMs', value: value });
  });

  // ── Live stage updates (Slice 12c) ──────────────────────────────────────
  // Raw WebSocket straight to the Studio Host's existing /ws/runtime room broker — same
  // pattern the DAG Replay pane already uses (clients/vscode-extension/src/webviews/dag-replay/
  // wsClient.ts), just inlined here since this view's script is a plain string, not a TS module.

  function connectStageSocket(wsUrl) {
    var ws;
    try { ws = new WebSocket(wsUrl); } catch (e) { return; }
    ws.onopen = function() {
      ws.send(JSON.stringify({ type: 'hello', room: 'studio-main', pubkey: 'studio-explorer', frontier: [] }));
    };
    ws.onmessage = function(e) {
      var msg;
      try { msg = JSON.parse(e.data); } catch (err) { return; }
      if (msg && msg.type === 'work-unit-stage-changed') {
        applyStageChange(msg.workUnitId, msg.stage);
      }
    };
    ws.onclose = function() { setTimeout(function() { connectStageSocket(wsUrl); }, 2000); };
    ws.onerror = function() { ws.close(); };
  }

  function applyStageChange(workUnitId, stage) {
    var wu = state.workUnits.find(function(w) { return w.workUnitId === workUnitId; });
    if (!wu) { return; }
    wu.currentStage = stage || null;
    renderTree(state.workUnits);
    if (state.selectedWorkUnitId === workUnitId) {
      document.getElementById('ex-inspector').innerHTML = renderWorkUnitInspector(wu);
      bindWorkUnitActionButtons();
    }
  }

  // ── Tree ─────────────────────────────────────────────────────────────────

  function renderTree(workUnits) {
    state.workUnits = workUnits || [];
    var el = document.getElementById('ex-tree');
    if (!workUnits || !workUnits.length) {
      el.innerHTML = '<p class="empty">No work units in this session yet.</p>';
      return;
    }
    var byParent = {};
    var roots = [];
    workUnits.forEach(function(wu) {
      var p = wu.parentWorkUnitId || null;
      if (p && workUnits.some(function(w) { return w.workUnitId === p; })) {
        (byParent[p] = byParent[p] || []).push(wu);
      } else {
        roots.push(wu);
      }
    });
    var html = '';
    function renderNode(wu, depth) {
      var sel = wu.workUnitId === state.selectedWorkUnitId ? ' selected' : '';
      html += '<div class="wu-node' + sel + '" style="margin-left:' + (depth * 14) + 'px" data-wu="' + esc(wu.workUnitId) + '">';
      html += '<div class="wu-title" title="' + esc(wu.goal) + '">' + esc(wu.goal) + '</div>';
      html += '<div class="wu-meta">' + badge(wu.status);
      if (isBlocked(wu)) { html += '<span class="badge blocked" title="' + esc(wu.fanOutInfo.blockedReason) + '">blocked</span>'; }
      if (wu.currentStage) { html += stageBadge(wu.currentStage); }
      if (wu.proposalCount) { html += '<span class="mono">' + wu.proposalCount + ' proposal(s)</span>'; }
      html += '</div></div>';
      (byParent[wu.workUnitId] || []).forEach(function(child) { renderNode(child, depth + 1); });
    }
    roots.forEach(function(r) { renderNode(r, 0); });
    el.innerHTML = html;
    el.querySelectorAll('.wu-node').forEach(function(node) {
      node.addEventListener('click', function() {
        var id = node.getAttribute('data-wu');
        state.selectedWorkUnitId = id;
        renderTree(state.workUnits);
        document.getElementById('ex-timeline').innerHTML = '<p class="empty">Loading…</p>';
        document.getElementById('ex-inspector').innerHTML = renderWorkUnitInspector(state.workUnits.find(function(w) { return w.workUnitId === id; }));
        bindWorkUnitActionButtons();
        vscode.postMessage({ type: 'explorerSelectWorkUnit', workUnitId: id });
      });
    });
    // Right-click work unit actions via the browser contextmenu event (native quick-pick prompts
    // live on the extension-host side; the webview only routes the action + id).
    el.querySelectorAll('.wu-node').forEach(function(node) {
      node.addEventListener('contextmenu', function(ev) {
        ev.preventDefault();
        var id = node.getAttribute('data-wu');
        renderActionMenu(id);
      });
    });
  }

  function renderActionMenu(workUnitId) {
    var el = document.getElementById('ex-inspector');
    var html = '<div class="meta-grid"><span class="meta-label">Work unit</span><span class="mono">' + esc(workUnitId) + '</span></div>';
    html += '<div class="inspector-actions">';
    html += '<button class="ghost" data-wu-action="split" data-wu="' + esc(workUnitId) + '">Split</button>';
    html += '<button class="ghost" data-wu-action="rerun" data-wu="' + esc(workUnitId) + '">Re-run</button>';
    html += '<button class="ghost" data-wu-action="branchLatest" data-wu="' + esc(workUnitId) + '">Branch from latest proposal</button>';
    html += '</div>';
    el.innerHTML = html;
    bindWorkUnitActionButtons();
  }

  function bindWorkUnitActionButtons() {
    document.querySelectorAll('[data-wu-action]').forEach(function(btn) {
      btn.addEventListener('click', function() {
        vscode.postMessage({
          type: 'explorerWorkUnitAction',
          action: btn.getAttribute('data-wu-action'),
          workUnitId: btn.getAttribute('data-wu'),
        });
      });
    });
  }

  function renderWorkUnitInspector(wu) {
    if (!wu) { return '<p class="empty">Nothing selected.</p>'; }
    var html = '<div class="meta-grid">';
    html += '<span class="meta-label">Status</span>' + badge(wu.status);
    html += '<span class="meta-label">Stage</span><span>' + stageBadge(wu.currentStage) + '</span>';
    html += '<span class="meta-label">Owner</span><span class="mono">' + esc(wu.owner) + '</span>';
    html += '<span class="meta-label">Agent</span><span class="mono">' + esc(wu.assignedAgent || '—') + '</span>';
    html += '<span class="meta-label">Branch</span><span class="mono">' + esc(wu.branchId) + '</span>';
    html += '<span class="meta-label">File scope</span><span class="mono">' + esc((wu.fileScope || []).join(', ') || '—') + '</span>';
    html += '<span class="meta-label">Depends on</span><span class="mono">' + esc((wu.dependsOn || []).join(', ') || '—') + '</span>';
    if (isBlocked(wu)) {
      html += '<span class="meta-label">Blocked</span><span>' + esc(wu.fanOutInfo.blockedReason) + '</span>';
    }
    html += '</div>';
    html += '<p>' + esc(wu.goal) + '</p>';
    if (wu.successCriteria) { html += '<p style="opacity:0.75"><em>' + esc(wu.successCriteria) + '</em></p>'; }
    html += '<div class="inspector-actions">';
    html += '<button class="ghost" data-wu-action="split" data-wu="' + esc(wu.workUnitId) + '">Split</button>';
    html += '<button class="ghost" data-wu-action="rerun" data-wu="' + esc(wu.workUnitId) + '">Re-run</button>';
    html += '<button class="ghost" data-wu-action="branchLatest" data-wu="' + esc(wu.workUnitId) + '">Branch from latest proposal</button>';
    html += '</div>';
    return html;
  }

  // ── Timeline ─────────────────────────────────────────────────────────────

  function renderTimeline(artifacts, events) {
    state.timelineArtifacts = artifacts || [];
    state.timelineEvents = events || [];
    var el = document.getElementById('ex-timeline');
    var rows = [];
    (artifacts || []).forEach(function(a) {
      rows.push({ sortKey: a.createdAt, kind: 'artifact', data: a });
    });
    (events || []).forEach(function(e) {
      rows.push({ sortKey: e.occurredAt, kind: 'event', data: e });
    });
    rows.sort(function(a, b) { return new Date(a.sortKey) - new Date(b.sortKey); });
    if (!rows.length) {
      el.innerHTML = '<p class="empty">No artifacts yet for this work unit.</p>';
      return;
    }
    var html = '';
    rows.forEach(function(row) {
      if (row.kind === 'artifact') {
        var a = row.data;
        var clickable = a.type === 'MergeProposal';
        html += '<div class="tl-item' + (clickable ? ' clickable' : '') + '"' +
          (clickable ? ' data-proposal="' + esc(a.artifactId) + '"' : '') + '>';
        html += '<span class="tl-time">' + fmtTime(a.createdAt) + '</span>';
        html += '<div class="tl-kind">' + esc(a.type) + '</div>';
        html += '<div class="tl-title">' + esc(a.title || a.artifactId) + ' ' + badge(a.status) + '</div>';
        if (a.body) { html += '<details><summary style="cursor:pointer;opacity:0.7;font-size:0.85em">details</summary><pre class="snapshot">' + esc(a.body) + '</pre></details>'; }
        html += '</div>';
      } else {
        var e = row.data;
        html += '<div class="tl-item clickable" data-event="' + esc(e.eventId) + '">';
        html += '<span class="tl-time">' + fmtTime(e.occurredAt) + '</span>';
        html += '<div class="tl-kind">orchestration event</div>';
        html += '<div class="tl-title">' + esc(e.inputStage) + ' &rarr; ' + esc(e.action) + '</div>';
        html += '</div>';
      }
    });
    el.innerHTML = html;
    el.querySelectorAll('[data-proposal]').forEach(function(node) {
      node.addEventListener('click', function() {
        var id = node.getAttribute('data-proposal');
        document.getElementById('ex-inspector').innerHTML = '<p class="empty">Loading…</p>';
        vscode.postMessage({ type: 'explorerSelectProposal', proposalId: id });
      });
    });
    el.querySelectorAll('[data-event]').forEach(function(node) {
      node.addEventListener('click', function() {
        var id = node.getAttribute('data-event');
        var e = state.timelineEvents.find(function(x) { return x.eventId === id; });
        if (e) { document.getElementById('ex-inspector').innerHTML = renderEventInspector(e); }
      });
    });
  }

  function renderEventInspector(e) {
    var html = '<div class="meta-grid">';
    html += '<span class="meta-label">Stage</span><span>' + esc(e.inputStage) + '</span>';
    html += '<span class="meta-label">Action</span><span>' + esc(e.action) + '</span>';
    html += '<span class="meta-label">Orchestrator</span><span class="mono">' + esc(e.orchestratorAgentId) + '</span>';
    html += '<span class="meta-label">Spawned</span><span class="mono">' + esc((e.spawnedIds || []).join(', ') || '—') + '</span>';
    html += '</div>';
    if (e.reason) { html += '<p>' + esc(e.reason) + '</p>'; }
    html += '<h2 style="margin-top:14px">Input projection snapshot</h2>';
    var pretty = e.inputProjectionSnapshot;
    try { pretty = JSON.stringify(JSON.parse(e.inputProjectionSnapshot), null, 2); } catch (err) {}
    html += '<pre class="snapshot">' + esc(pretty) + '</pre>';
    return html;
  }

  // ── Proposal inspector ───────────────────────────────────────────────────

  function renderProposalInspector(proposal) {
    var html = '<div class="meta-grid">';
    html += '<span class="meta-label">Status</span>' + badge(proposal.status);
    html += '<span class="meta-label">Source</span><span class="mono">' + esc(proposal.sourceBranch) + '</span>';
    html += '<span class="meta-label">Confidence</span><span>' + (proposal.confidence != null ? Math.round(proposal.confidence * 100) + '%' : '—') + '</span>';
    html += '<span class="meta-label">Files touched</span><span>' + ((proposal.filesTouched || []).length) + '</span>';
    html += '</div>';
    html += '<p>' + esc(proposal.goal) + '</p>';

    var others = state.timelineArtifacts
      .filter(function(a) { return a.type === 'MergeProposal' && a.artifactId !== proposal.proposalId; })
      .map(function(a) { return { proposalId: a.artifactId, title: a.title }; });
    window.__nmCandidates = others;
    window.__nmProposalId = proposal.proposalId;

    html += '<div class="inspector-actions">';
    html += '<button data-p-action="openReview">Open in Merge Review &rarr;</button>';
    html += '<button class="ghost" data-p-action="branch">Branch from here</button>';
    html += '<button class="ghost" data-p-action="restore">Restore workspace</button>';
    html += '<button class="ghost" data-p-action="compare">Compare with…</button>';
    html += '</div>';
    html += '<div id="ex-compare-result"></div>';
    return html;
  }

  function bindProposalActionButtons() {
    document.querySelectorAll('[data-p-action]').forEach(function(btn) {
      btn.addEventListener('click', function() {
        vscode.postMessage({
          type: 'explorerProposalAction',
          action: btn.getAttribute('data-p-action'),
          proposalId: window.__nmProposalId,
          candidates: window.__nmCandidates || [],
        });
      });
    });
  }

  function renderDiffText(text) {
    return String(text || '').split('\\n').map(function(line) {
      var cls = line.startsWith('+') ? 'diff-add' : line.startsWith('-') ? 'diff-del' : '';
      return cls ? '<span class="' + cls + '">' + esc(line) + '</span>' : esc(line);
    }).join('\\n');
  }

  function renderCompareResult(result) {
    var el = document.getElementById('ex-compare-result');
    if (!el) { return; }
    var html = '<h2 style="margin-top:14px">Compare</h2>';
    html += '<p class="mono">overlapping files: ' + ((result.overlappingFiles || []).join(', ') || 'none') + '</p>';
    html += '<div style="display:grid;grid-template-columns:1fr 1fr;gap:8px">';
    html += '<pre class="diff-pre">' + renderDiffText(result.diffA) + '</pre>';
    html += '<pre class="diff-pre">' + renderDiffText(result.diffB) + '</pre>';
    html += '</div>';
    el.innerHTML = html;
  }

  // ── Messages from extension host ────────────────────────────────────────

  window.addEventListener('message', function(event) {
    var msg = event.data;
    if (msg.type === 'explorerWsInit') {
      connectStageSocket(msg.wsUrl);
      return;
    }
    if (msg.type === 'templates') {
      var sel = document.getElementById('ex-template');
      sel.innerHTML = (msg.templates || []).map(function(t) {
        return '<option value="' + esc(t.name) + '">' + esc(t.name) + '</option>';
      }).join('');
      return;
    }
    if (msg.type === 'sessions') {
      var sel2 = document.getElementById('ex-session');
      var options = '<option value="">(no session)</option>' + (msg.sessions || []).map(function(s) {
        return '<option value="' + esc(s.sessionId) + '">' + esc(s.sessionId) + ' — ' + esc(s.status) + '</option>';
      }).join('');
      sel2.innerHTML = options;
      sel2.value = msg.selectedSessionId || '';
      return;
    }
    if (msg.type === 'tree') {
      renderTree(msg.workUnits);
      return;
    }
    if (msg.type === 'timeline') {
      renderTimeline(msg.artifacts, msg.events);
      return;
    }
    if (msg.type === 'proposal') {
      document.getElementById('ex-inspector').innerHTML = renderProposalInspector(msg.proposal);
      bindProposalActionButtons();
      return;
    }
    if (msg.type === 'compareResult') {
      renderCompareResult(msg.result);
      return;
    }
    if (msg.type === 'explorerSettings') {
      document.getElementById('ex-llm-profile-checkbox').checked = !!msg.useLlmProfileSelection;
      document.getElementById('ex-max-concurrent-workers').value = msg.maxConcurrentWorkers;
      document.getElementById('ex-scheduler-poll-interval').value = msg.schedulerPollIntervalMs;
      return;
    }
    if (msg.type === 'runResult') {
      var btn = document.getElementById('ex-run');
      btn.disabled = false;
      btn.textContent = '\\u25B6 Run';
      if (msg.success) {
        document.getElementById('ex-goal').value = '';
      }
      return;
    }
  });
`;
