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
  forkType?: string | null;
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

interface EvidenceEntry {
  evidenceId: string;
  kind: string;
  buildSystem?: string;
  command?: string;
  success: boolean;
  exitCode?: number;
  totalTests?: number;
  passed?: number;
  failed?: number;
  skipped?: number;
  summary?: string;
  attachedAt?: string;
}

// Slice 18f — Reasoning Commit Graph projection payloads
interface ReasoningCommitGraphPayload {
  rootWorkUnitId: string;
  nodes: ReasoningCommitGraphNode[];
  edges: ReasoningCommitGraphEdge[];
}

interface ReasoningCommitGraphNode {
  commitId: string;
  workUnitId: string;
  agentId?: string | null;
  stage: string;
  action: string;
  reasoning?: string | null;
  agentModel?: string | null;
  agentProvider?: string | null;
  occurredAt: string;
}

interface ReasoningCommitGraphEdge {
  fromCommitId: string;
  toCommitId: string;
  edgeType: string;
}

// ── Panel ──────────────────────────────────────────────────────────────────

export class GoalWorkspacePanel {
  static readonly containerId = 'shell-pane-goal-workspace';

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
      css: scopeViewCss(GW_CSS, GoalWorkspacePanel.containerId),
      html: `<div id="${GoalWorkspacePanel.containerId}" class="nm-shell-pane active">${GW_HTML}</div>`,
      script: wrapViewScript(GW_JS, GoalWorkspacePanel.containerId),
    };
  }

  activate(): void {
    void this.sendStrategies();
    void this.sendWsInit();
    void this.sendSettings();
    void this.refreshSessions();
    this.pollTimer = setInterval(() => {
      void this.refreshSessions();
      if (this.selectedSessionId) { void this.refreshDecisionTree(this.selectedSessionId); }
    }, POLL_INTERVAL_MS);
  }

  dispose(): void {
    if (this.pollTimer) { clearInterval(this.pollTimer); this.pollTimer = undefined; }
  }

  private async sendStrategies(): Promise<void> {
    if (!this.configService) { return; }
    const templates = this.configService.getTemplates();
    // Slice 18a — multi-model comparison: detect when 2+ orchestrator profiles
    // have different models, and expose a live "Multi-Model Comparison" strategy.
    const profiles = this.configService.getProfiles();
    const orchModels = new Set(
      profiles
        .filter(p => p.domain === 'orchestration' && p.model)
        .map(p => p.model!)
    );
    const strategies: Array<{ name: string; orchestrator: string; workers?: { profile: string }[]; disabled?: boolean; tooltip?: string }> = [...templates];
    if (orchModels.size >= 2) {
      strategies.push({
        name: 'Multi-Model Comparison',
        orchestrator: '',
        workers: [],
        disabled: false,
      });
    } else {
      strategies.push({
        name: '__multi_model__',
        orchestrator: '',
        workers: [],
        disabled: true,
        tooltip: 'Configure at least 2 orchestrator profiles with different models in Model & Agent Studio.',
      });
    }
    void this.panel.webview.postMessage({ type: 'strategies', strategies });
  }

  // Slice 12c — live stage badges.
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
      // host not ready yet
    }
  }

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
      // host not ready yet
    }
  }

  private async refreshDecisionTree(sessionId: string): Promise<void> {
    try {
      const workUnits = await this.get<WorkUnit[]>('/studio/sessions/' + sessionId + '/workunits');
      void this.panel.webview.postMessage({ type: 'tree', sessionId, workUnits });
    } catch {
      // session may have just been created and not yet visible
    }
  }

  private async loadTimeline(workUnitId: string): Promise<void> {
    try {
      const [artifacts, events, evidence, reasoningGraph] = await Promise.all([
        this.get<ArtifactRef[]>('/studio/workunits/' + workUnitId + '/artifacts'),
        this.get<OrchestrationEvent[]>('/studio/workunits/' + workUnitId + '/orchestration-events'),
        // Slice 18c — fetch evidence for the Decision Lens inspector
        this.get<{ evidence: EvidenceEntry[] }>('/studio/evidence?workUnitId=' + encodeURIComponent(workUnitId)).catch(() => null),
        // Slice 18f — fetch reasoning commit graph for the Reasoning Chain view
        this.get<{ data: ReasoningCommitGraphPayload }>('/studio/projections/ReasoningCommitGraph?workUnitId=' + encodeURIComponent(workUnitId) + '&level=Normal').catch(() => null),
      ]);
      void this.panel.webview.postMessage({
        type: 'timeline', workUnitId, artifacts, events,
        evidence: evidence?.evidence ?? [],
        reasoningGraph: reasoningGraph?.data ?? null,
      });
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
          if (this.selectedSessionId) { await this.refreshDecisionTree(this.selectedSessionId); }
          break;
        case 'explorerRun':
          await this.handleRun(msg.strategy as string, msg.goal as string);
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

  private async handleRun(strategy: string, goal: string): Promise<void> {
    if (!goal || !goal.trim()) {
      void vscode.window.showWarningMessage('NodalMerge: enter a goal before running.');
      return;
    }

    // Slice 18a — multi-model comparison: spawn 2 orchestrators with different models
    // as siblings under a shared parent work unit.
    if (strategy === 'Multi-Model Comparison') {
      if (!this.configService || !this.secrets || !this.lmProxyBaseUrl) {
        void vscode.window.showWarningMessage(
          'NodalMerge: LLM credentials required — configure profiles in Model & Agent Studio.',
        );
        return;
      }
      const profiles = this.configService.getProfiles();
      const orchProfiles = profiles.filter(p => p.domain === 'orchestration' && p.model);
      if (orchProfiles.length < 2) {
        void vscode.window.showErrorMessage(
          'Multi-Model Comparison requires at least 2 orchestrator profiles with different models.',
        );
        return;
      }
      const modelAProfile = orchProfiles[0];
      const modelBProfile = orchProfiles[1];

      try {
        const [cfgA, cfgB] = await Promise.all([
          this.configService.resolveSpawnLlmConfig(modelAProfile.id, this.secrets, this.lmProxyBaseUrl),
          this.configService.resolveSpawnLlmConfig(modelBProfile.id, this.secrets, this.lmProxyBaseUrl),
        ]);
        if (!cfgA || !cfgB) {
          throw new Error('One or both orchestrator profiles missing LLM credentials.');
        }

        const repositoryPath = vscode.workspace.workspaceFolders?.[0]?.uri?.fsPath;
        // Create a parent work unit to hold both model runs
        const parentWu = await this.post<{ workUnitId: string }>('/studio/workunits', {
          goal,
          owner: 'user',
          ...(repositoryPath ? { repositoryPath } : {}),
        });

        // Create two child work units — one per model
        const [childA, childB] = await Promise.all([
          this.post<{ workUnitId: string }>('/studio/workunits', {
            goal: `[${modelAProfile.model ?? modelAProfile.id}] ${goal}`,
            owner: modelAProfile.id,
            parentWorkUnitId: parentWu.workUnitId,
            ...(repositoryPath ? { repositoryPath } : {}),
          }),
          this.post<{ workUnitId: string }>('/studio/workunits', {
            goal: `[${modelBProfile.model ?? modelBProfile.id}] ${goal}`,
            owner: modelBProfile.id,
            parentWorkUnitId: parentWu.workUnitId,
            ...(repositoryPath ? { repositoryPath } : {}),
          }),
        ]);

        const session = await this.post<ExecutionSession>('/studio/sessions', {
          rootWorkUnitId: parentWu.workUnitId,
          profileIds: [modelAProfile.id, modelBProfile.id],
        });

        // Spawn orchestrator for each child
        await Promise.all([
          this.post('/studio/agents/spawn', {
            agentType: 'orchestrator',
            workUnitId: childA.workUnitId,
            profileId: modelAProfile.id,
            ...cfgA,
          }),
          this.post('/studio/agents/spawn', {
            agentType: 'orchestrator',
            workUnitId: childB.workUnitId,
            profileId: modelBProfile.id,
            ...cfgB,
          }),
        ]);

        this.selectedSessionId = session.sessionId;
        void this.panel.webview.postMessage({ type: 'runResult', success: true, sessionId: session.sessionId });
        await this.refreshSessions();
        await this.refreshDecisionTree(session.sessionId);
        void vscode.window.showInformationMessage(
          `Multi-model comparison started: ${modelAProfile.model ?? modelAProfile.id} vs ${modelBProfile.model ?? modelBProfile.id}`,
        );
      } catch (err) {
        void this.panel.webview.postMessage({ type: 'runResult', success: false, message: String(err) });
        void vscode.window.showErrorMessage('NodalMerge: Multi-model run failed — ' + String(err));
      }
      return;
    }

    const templates = this.configService?.getTemplates() ?? [];
    const template = templates.find(t => t.name === strategy);
    if (!template) {
      void vscode.window.showErrorMessage(`NodalMerge: Strategy "${strategy}" not found.`);
      void this.panel.webview.postMessage({ type: 'runResult', success: false, message: 'Strategy not found.' });
      return;
    }
    if (!this.configService || !this.secrets || !this.lmProxyBaseUrl) {
      void vscode.window.showWarningMessage(
        'NodalMerge: Spawning without LLM credentials is not possible from here — configure Model & Agent Studio first.',
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
          `Profile "${template.orchestrator}" is missing LLM credentials — set it up in Model & Agent Studio.`,
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
      await this.refreshDecisionTree(session.sessionId);
      void vscode.window.showInformationMessage(`Goal created: ${goal}`);
    } catch (err) {
      void this.panel.webview.postMessage({ type: 'runResult', success: false, message: String(err) });
      void vscode.window.showErrorMessage('NodalMerge: Run failed — ' + String(err));
    }
  }

  private async handleWorkUnitAction(action: string, workUnitId: string): Promise<void> {
    if (action === 'forkHypothesis' || action === 'split') {
      // Slice 18e — fork type selector before goal collection
      const forkTypes: Array<{ label: string; description: string }> = [
        { label: 'Code', description: 'Implementation change' },
        { label: 'Reasoning', description: 'Different reasoning path' },
        { label: 'Model', description: 'Different model or provider' },
        { label: 'Research', description: 'Investigation or analysis' },
        { label: 'Architecture', description: 'Structural/design change' },
        { label: 'Product', description: 'User-facing behavior change' },
      ];
      const forkTypePick = await vscode.window.showQuickPick(forkTypes, {
        placeHolder: 'Hypothesis fork type', ignoreFocusOut: true,
      });
      if (!forkTypePick) { return; }

      const goalA = await vscode.window.showInputBox({
        prompt: 'Goal for the first hypothesis fork', ignoreFocusOut: true,
      });
      if (!goalA) { return; }
      const scopeARaw = await vscode.window.showInputBox({
        prompt: 'File scope for the first fork (comma-separated, optional)', ignoreFocusOut: true,
      });
      const goalB = await vscode.window.showInputBox({
        prompt: 'Goal for the second hypothesis fork', ignoreFocusOut: true,
      });
      if (!goalB) { return; }
      const scopeBRaw = await vscode.window.showInputBox({
        prompt: 'File scope for the second fork (comma-separated, optional)', ignoreFocusOut: true,
      });
      const parseScope = (s: string | undefined) =>
        s ? s.split(',').map(x => x.trim()).filter(Boolean) : undefined;

      await this.post('/studio/hypotheses/fork', {
        goal: goalA, forkType: forkTypePick.label, parentWorkUnitId: workUnitId,
      });
      await this.post('/studio/hypotheses/fork', {
        goal: goalB, forkType: forkTypePick.label, parentWorkUnitId: workUnitId,
      });
      void vscode.window.showInformationMessage('NodalMerge: Forked hypothesis (' + forkTypePick.label + ') into 2 child work units.');
      if (this.selectedSessionId) { await this.refreshDecisionTree(this.selectedSessionId); }
      return;
    }

    if (action === 'reexplore' || action === 'rerun') {
      const profile = await this.configService?.pickProfile('Select profile to re-explore this node');
      if (!profile) { return; }
      await this.post('/studio/scheduler/enqueue', { workUnitId, profileId: profile.id });
      void vscode.window.showInformationMessage('NodalMerge: Re-enqueued work unit.');
      return;
    }

    if (action === 'forkLatest' || action === 'branchLatest') {
      const artifacts = await this.get<ArtifactRef[]>('/studio/workunits/' + workUnitId + '/artifacts');
      const proposals = artifacts.filter(a => a.type === 'MergeProposal');
      if (proposals.length === 0) {
        void vscode.window.showWarningMessage('NodalMerge: no decision candidate found for this node yet.');
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
    if (action === 'forkHypothesis' || action === 'branch') {
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
        void vscode.window.showWarningMessage('NodalMerge: no other candidates on this node to compare with.');
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
      prompt: 'Goal for the new hypothesis fork', placeHolder: 'e.g. Retry with a different model', ignoreFocusOut: true,
    });
    if (!goal) { return; }
    const profile = await this.configService?.pickProfile('Select profile to run the new fork');
    if (!profile) { return; }
    const result = await this.post<{ workUnitId: string }>(
      '/studio/merges/' + proposalId + '/branch',
      { goal, profileId: profile.id, ...(this.selectedSessionId ? { sessionId: this.selectedSessionId } : {}) });
    void vscode.window.showInformationMessage('NodalMerge: Forked new work unit ' + result.workUnitId + '.');
    if (this.selectedSessionId) { await this.refreshDecisionTree(this.selectedSessionId); }
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

const GW_CSS = `
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
  .gw-topbar {
    flex-shrink: 0; padding: 10px 14px; border-bottom: 1px solid var(--nm-border);
    display: flex; gap: 8px; flex-wrap: wrap; align-items: flex-end;
    background: var(--nm-section-bg);
  }
  .gw-field { display: flex; flex-direction: column; gap: 2px; }
  .gw-field label { font-size: 0.72em; opacity: 0.6; text-transform: uppercase; letter-spacing: 0.05em; }
  select, textarea, input[type=text] {
    background: var(--nm-input-bg); color: var(--nm-input-fg); border: 1px solid var(--nm-input-bdr);
    border-radius: 3px; padding: 4px 6px; font-family: var(--nm-font); font-size: 0.9em;
  }
  textarea#gw-goal { width: 320px; height: 32px; min-height: 32px; resize: vertical; }
  button {
    background: var(--nm-btn); color: var(--nm-btn-fg); border: none; border-radius: 3px;
    padding: 5px 14px; font-size: 0.88em; cursor: pointer; font-family: var(--nm-font);
  }
  button:hover:not(:disabled) { background: var(--nm-btn-hover); }
  button.ghost { background: transparent; color: var(--nm-fg); border: 1px solid var(--nm-border); }
  button.ghost:hover { background: color-mix(in srgb, var(--nm-border) 50%, transparent); }
  .gw-settings-panel { flex-shrink: 0; padding: 8px 14px; border-bottom: 1px solid var(--nm-border); background: var(--nm-section-bg); }
  .gw-settings-row { display: flex; align-items: center; gap: 6px; font-size: 0.85em; cursor: pointer; }
  .gw-body { flex: 1; display: flex; overflow: hidden; min-height: 0; }
  .gw-col { overflow-y: auto; padding: 10px 12px; }
  .gw-decision-tree { width: 280px; flex-shrink: 0; border-right: 1px solid var(--nm-border); }
  .gw-timeline { flex: 1; min-width: 0; border-right: 1px solid var(--nm-border); }
  .gw-inspector { width: 320px; flex-shrink: 0; }
  h2 {
    font-size: 0.78em; font-weight: 700; text-transform: uppercase; letter-spacing: 0.07em;
    opacity: 0.5; margin: 0 0 8px;
  }
  .empty { opacity: 0.42; font-style: italic; padding: 4px 0; font-size: 0.9em; }
  .dn-node { border-radius: 4px; padding: 6px 8px; margin-bottom: 3px; cursor: pointer; border: 1px solid transparent; }
  .dn-node:hover { background: color-mix(in srgb, var(--nm-border) 30%, transparent); }
  .dn-node.selected { border-color: var(--nm-info); background: color-mix(in srgb, var(--nm-info) 12%, transparent); }
  .dn-title { font-weight: 600; font-size: 0.92em; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .dn-meta { display: flex; gap: 6px; margin-top: 3px; flex-wrap: wrap; }
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

  /* Slice 18f — Reasoning Chain vertical timeline */
  .rc-chain { margin-top: 14px; }
  .rc-node { position: relative; padding-left: 22px; margin-bottom: 10px; }
  .rc-node:last-child { margin-bottom: 0; }
  .rc-dot {
    position: absolute; left: 0; top: 10px;
    width: 10px; height: 10px; border-radius: 50%;
    background: var(--nm-info); border: 2px solid var(--nm-info);
    z-index: 1;
  }
  .rc-node:not(:last-child)::after {
    content: '';
    position: absolute; left: 4px; top: 22px; bottom: -10px;
    width: 2px; background: var(--nm-border);
  }
  .rc-card {
    border: 1px solid var(--nm-border); border-radius: 4px;
    padding: 6px 10px; background: var(--nm-section-bg);
    cursor: pointer;
  }
  .rc-card:hover { border-color: var(--nm-info); }
  .rc-header { display: flex; gap: 6px; align-items: center; flex-wrap: wrap; }
  .rc-header .badge { font-size: 0.68em; }
  .rc-body { font-size: 0.82em; opacity: 0.75; margin-top: 3px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .rc-footer { font-size: 0.72em; opacity: 0.4; margin-top: 3px; display: flex; justify-content: space-between; }
  .rc-edge-badge {
    display: inline-block; border-radius: 4px; padding: 0 6px;
    font-size: 0.64em; font-weight: 600; text-transform: uppercase; letter-spacing: 0.04em;
    margin-left: 4px;
  }
  .rc-edge-badge.refine { background: rgba(128,128,128,0.2); color: var(--nm-fg); border: 1px solid rgba(128,128,128,0.4); }
  .rc-edge-badge.fork { background: rgba(177,128,215,0.2); color: #b180d7; border: 1px solid rgba(177,128,215,0.4); }
  .rc-edge-badge.decided { background: rgba(77,172,38,0.2); color: var(--nm-success); border: 1px solid rgba(77,172,38,0.4); }
  .rc-edge-badge.evidenceattached { background: rgba(55,148,255,0.15); color: var(--nm-info); border: 1px solid rgba(55,148,255,0.35); }
`;

const GW_HTML = `
  <div class="gw-topbar">
    <div class="gw-field">
      <label>Active Exploration</label>
      <select id="gw-session"><option value="">(no exploration)</option></select>
    </div>
    <div class="gw-field">
      <label>Exploration Strategy</label>
      <select id="gw-strategy"></select>
    </div>
    <div class="gw-field">
      <label>Goal</label>
      <textarea id="gw-goal" placeholder="Describe a goal — e.g. Add dark mode support across the settings UI"></textarea>
    </div>
    <button id="gw-run">&#x25B6; Run</button>
    <button id="gw-settings-btn" class="ghost" title="Exploration Settings">&#9881;</button>
  </div>
  <div id="gw-settings-panel" class="gw-settings-panel" style="display:none">
    <label class="gw-settings-row">
      <input type="checkbox" id="gw-llm-profile-checkbox"/>
      Auto-select agent profiles by capability
    </label>
    <label class="gw-settings-row">
      Max concurrent workers
      <input type="number" id="gw-max-concurrent-workers" min="1" step="1" style="width:60px"/>
    </label>
    <label class="gw-settings-row">
      Scheduler poll interval (ms)
      <input type="number" id="gw-scheduler-poll-interval" min="100" step="100" style="width:80px"/>
    </label>
  </div>
  <div class="gw-body">
    <div class="gw-col gw-decision-tree">
      <h2>Decision Tree</h2>
      <div id="gw-tree"><p class="empty">Create a goal to start exploring decisions.</p></div>
    </div>
    <div class="gw-col gw-timeline">
      <h2>Reasoning & Execution Timeline</h2>
      <div id="gw-timeline"><p class="empty">Select a decision node to see its reasoning and execution timeline.</p></div>
    </div>
    <div class="gw-col gw-inspector">
      <h2>Decision Lens</h2>
      <div id="gw-inspector"><p class="empty">Select a decision node or timeline item to inspect.</p></div>
    </div>
  </div>
`;

const GW_JS = `
  var vscode = acquireVsCodeApi();
  var state = { decisionNodes: [], selectedNodeId: null, timelineArtifacts: [], timelineEvents: [] };

  function esc(s) {
    return String(s || '').replace(/&/g,'&').replace(/</g,'<').replace(/>/g,'>');
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

  // ── Artifact classifcation for typed labels ─────────────────────────────

  function classifyArtifact(artifactType) {
    var map = {
      Goal:             { label: 'Goal',             icon: '🎯' },
      Plan:             { label: 'Plan Proposal',    icon: '📐' },
      Decision:         { label: 'Reasoning Step',   icon: '🧠' },
      Research:         { label: 'Research',         icon: '🔍' },
      Constraint:       { label: 'Constraint',       icon: '🔒' },
      Task:             { label: 'Task',             icon: '📋' },
      BranchChangeset:  { label: 'Code Change',      icon: '📁' },
      MergeProposal:    { label: 'Decision Candidate', icon: '📐' },
      MergeResult:      { label: 'Merged',           icon: '✅' },
    };
    return map[artifactType] || { label: artifactType, icon: '' };
  }

  // ── Top bar ──────────────────────────────────────────────────────────────

  document.getElementById('gw-session').addEventListener('change', function(ev) {
    vscode.postMessage({ type: 'explorerSelectSession', sessionId: ev.target.value });
    document.getElementById('gw-tree').innerHTML = '<p class="empty">Loading…</p>';
  });

  document.getElementById('gw-run').addEventListener('click', function() {
    var goal = document.getElementById('gw-goal').value.trim();
    var strategy = document.getElementById('gw-strategy').value;
    if (!goal) { return; }
    var btn = document.getElementById('gw-run');
    btn.disabled = true;
    btn.textContent = 'Running…';
    vscode.postMessage({ type: 'explorerRun', strategy: strategy, goal: goal });
  });

  // ── Exploration Settings ─────────────────────────────────────────────────

  document.getElementById('gw-settings-btn').addEventListener('click', function() {
    var panel = document.getElementById('gw-settings-panel');
    panel.style.display = panel.style.display === 'none' ? 'block' : 'none';
  });

  document.getElementById('gw-llm-profile-checkbox').addEventListener('change', function(ev) {
    vscode.postMessage({ type: 'explorerSetUseLlmProfileSelection', value: ev.target.checked });
  });

  document.getElementById('gw-max-concurrent-workers').addEventListener('change', function(ev) {
    var value = parseInt(ev.target.value, 10);
    if (!value || value < 1) { return; }
    vscode.postMessage({ type: 'explorerSetMaxConcurrentWorkers', value: value });
  });

  document.getElementById('gw-scheduler-poll-interval').addEventListener('change', function(ev) {
    var value = parseInt(ev.target.value, 10);
    if (!value || value < 100) { return; }
    vscode.postMessage({ type: 'explorerSetSchedulerPollIntervalMs', value: value });
  });

  // ── Live stage updates ───────────────────────────────────────────────────

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
    var node = state.decisionNodes.find(function(w) { return w.workUnitId === workUnitId; });
    if (!node) { return; }
    node.currentStage = stage || null;
    renderDecisionTree(state.decisionNodes);
    if (state.selectedNodeId === workUnitId) {
      document.getElementById('gw-inspector').innerHTML = renderDecisionInspector(node);
      bindWorkUnitActionButtons();
    }
  }

  // ── Decision Tree ────────────────────────────────────────────────────────

  function renderDecisionTree(decisionNodes) {
    state.decisionNodes = decisionNodes || [];
    var el = document.getElementById('gw-tree');
    if (!decisionNodes || !decisionNodes.length) {
      el.innerHTML = '<p class="empty">No decision nodes in this exploration yet.</p>';
      return;
    }
    var byParent = {};
    var roots = [];
    decisionNodes.forEach(function(wu) {
      var p = wu.parentWorkUnitId || null;
      if (p && decisionNodes.some(function(w) { return w.workUnitId === p; })) {
        (byParent[p] = byParent[p] || []).push(wu);
      } else {
        roots.push(wu);
      }
    });
    var html = '';
    function renderNode(wu, depth) {
      var sel = wu.workUnitId === state.selectedNodeId ? ' selected' : '';
      html += '<div class="dn-node' + sel + '" style="margin-left:' + (depth * 14) + 'px" data-wu="' + esc(wu.workUnitId) + '">';
      html += '<div class="dn-title" title="' + esc(wu.goal) + '">' + esc(wu.goal) + '</div>';
      html += '<div class="dn-meta">' + badge(wu.status);
      if (isBlocked(wu)) { html += '<span class="badge blocked" title="' + esc(wu.fanOutInfo.blockedReason) + '">blocked</span>'; }
      // Slice 18b — fork-type badge
      if (wu.forkType && (wu.forkType || '').toLowerCase() !== 'unknown') {
        html += '<span class="badge fork-type">' + esc(wu.forkType) + '</span>';
      }
      if (wu.currentStage) { html += stageBadge(wu.currentStage); }
      if (wu.proposalCount) { html += '<span class="mono">' + wu.proposalCount + ' candidate(s)</span>'; }
      html += '</div></div>';
      (byParent[wu.workUnitId] || []).forEach(function(child) { renderNode(child, depth + 1); });
    }
    roots.forEach(function(r) { renderNode(r, 0); });
    el.innerHTML = html;
    el.querySelectorAll('.dn-node').forEach(function(node) {
      node.addEventListener('click', function() {
        var id = node.getAttribute('data-wu');
        state.selectedNodeId = id;
        renderDecisionTree(state.decisionNodes);
        document.getElementById('gw-timeline').innerHTML = '<p class="empty">Loading…</p>';
        document.getElementById('gw-inspector').innerHTML = renderDecisionInspector(state.decisionNodes.find(function(w) { return w.workUnitId === id; }));
        bindWorkUnitActionButtons();
        vscode.postMessage({ type: 'explorerSelectWorkUnit', workUnitId: id });
      });
    });
    el.querySelectorAll('.dn-node').forEach(function(node) {
      node.addEventListener('contextmenu', function(ev) {
        ev.preventDefault();
        var id = node.getAttribute('data-wu');
        renderNodeActionMenu(id);
      });
    });
  }

  function renderNodeActionMenu(workUnitId) {
    var el = document.getElementById('gw-inspector');
    var html = '<div class="meta-grid"><span class="meta-label">Decision node</span><span class="mono">' + esc(workUnitId) + '</span></div>';
    html += '<div class="inspector-actions">';
    html += '<button class="ghost" data-wu-action="forkHypothesis" data-wu="' + esc(workUnitId) + '">Fork Hypothesis</button>';
    html += '<button class="ghost" data-wu-action="reexplore" data-wu="' + esc(workUnitId) + '">Re-explore</button>';
    html += '<button class="ghost" data-wu-action="forkLatest" data-wu="' + esc(workUnitId) + '">Fork from latest candidate</button>';
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

  function renderDecisionInspector(wu) {
    if (!wu) { return '<p class="empty">Select a decision node or timeline item to inspect.</p>'; }
    var html = '<div class="meta-grid">';
    html += '<span class="meta-label">Decision Status</span>' + badge(wu.status);
    html += '<span class="meta-label">Phase</span><span>' + stageBadge(wu.currentStage) + '</span>';
    html += '<span class="meta-label">Initiator</span><span class="mono">' + esc(wu.owner) + '</span>';
    html += '<span class="meta-label">Executor</span><span class="mono">' + esc(wu.assignedAgent || '—') + '</span>';
    html += '<span class="meta-label">Hypothesis Fork</span><span class="mono">' + esc(wu.branchId) + '</span>';
    // Slice 18b/18e — fork-type metadata
    if (wu.forkType && wu.forkType.toLowerCase() !== 'unknown') {
      html += '<span class="meta-label">Fork Type</span><span class="badge fork-type">' + esc(wu.forkType) + '</span>';
    }
    html += '<span class="meta-label">File scope</span><span class="mono">' + esc((wu.fileScope || []).join(', ') || '—') + '</span>';
    html += '<span class="meta-label">Depends on</span><span class="mono">' + esc((wu.dependsOn || []).join(', ') || '—') + '</span>';
    if (isBlocked(wu)) {
      html += '<span class="meta-label">Blocked</span><span>' + esc(wu.fanOutInfo.blockedReason) + '</span>';
    }
    html += '</div>';
    html += '<p>' + esc(wu.goal) + '</p>';
    if (wu.successCriteria) { html += '<p style="opacity:0.75"><em>' + esc(wu.successCriteria) + '</em></p>'; }
    // Slice 18c — Evidence section
    if (state.selectedNodeEvidence && state.selectedNodeEvidence.length) {
      html += '<h2 style="margin-top:12px">Evidence</h2>';
      state.selectedNodeEvidence.forEach(function(ev) {
        var icon = ev.success ? '✅' : '❌';
        var summary = ev.summary || (ev.kind === 'Build'
          ? (ev.buildSystem || 'build') + ': ' + (ev.success ? 'passed' : 'failed (exit ' + (ev.exitCode || '?') + ')')
          : ev.kind === 'Test'
            ? (ev.buildSystem || 'test') + ': ' + (ev.success ? (ev.passed || 0) + '/' + (ev.totalTests || 0) + ' passed' : (ev.failed || 0) + ' failed')
            : ev.kind + ': ' + (ev.success ? 'ok' : 'fail'));
        html += '<div style="font-size:0.85em;padding:2px 0">' + icon + ' ' + esc(summary) + '</div>';
      });
    }
    // Slice 18f — Reasoning Chain section
    if (state.reasoningGraph) {
      html += renderReasoningChain(state.reasoningGraph);
    }
    html += '<div class="inspector-actions">';
    html += '<button class="ghost" data-wu-action="forkHypothesis" data-wu="' + esc(wu.workUnitId) + '">Fork Hypothesis</button>';
    html += '<button class="ghost" data-wu-action="reexplore" data-wu="' + esc(wu.workUnitId) + '">Re-explore</button>';
    html += '<button class="ghost" data-wu-action="forkLatest" data-wu="' + esc(wu.workUnitId) + '">Fork from latest candidate</button>';
    html += '</div>';
    return html;
  }

  // ── Timeline ─────────────────────────────────────────────────────────────

  function renderTimeline(artifacts, events) {
    state.timelineArtifacts = artifacts || [];
    state.timelineEvents = events || [];
    var el = document.getElementById('gw-timeline');
    var rows = [];
    (artifacts || []).forEach(function(a) {
      rows.push({ sortKey: a.createdAt, kind: 'artifact', data: a });
    });
    (events || []).forEach(function(e) {
      rows.push({ sortKey: e.occurredAt, kind: 'event', data: e });
    });
    rows.sort(function(a, b) { return new Date(a.sortKey) - new Date(b.sortKey); });
    if (!rows.length) {
      el.innerHTML = '<p class="empty">No artifacts yet for this decision node.</p>';
      return;
    }
    var html = '';
    rows.forEach(function(row) {
      if (row.kind === 'artifact') {
        var a = row.data;
        var classified = classifyArtifact(a.type);
        var clickable = a.type === 'MergeProposal';
        html += '<div class="tl-item' + (clickable ? ' clickable' : '') + '"' +
          (clickable ? ' data-proposal="' + esc(a.artifactId) + '"' : '') + '>';
        html += '<span class="tl-time">' + fmtTime(a.createdAt) + '</span>';
        html += '<div class="tl-kind">' + classified.icon + ' ' + classified.label + '</div>';
        html += '<div class="tl-title">' + esc(a.title || a.artifactId) + ' ' + badge(a.status) + '</div>';
        if (a.body) { html += '<details><summary style="cursor:pointer;opacity:0.7;font-size:0.85em">details</summary><pre class="snapshot">' + esc(a.body) + '</pre></details>'; }
        html += '</div>';
      } else {
        var e = row.data;
        html += '<div class="tl-item clickable" data-event="' + esc(e.eventId) + '">';
        html += '<span class="tl-time">' + fmtTime(e.occurredAt) + '</span>';
        html += '<div class="tl-kind">🤖 Agent Action</div>';
        html += '<div class="tl-title">' + esc(e.inputStage) + ' &rarr; ' + esc(e.action) + '</div>';
        html += '</div>';
      }
    });
    el.innerHTML = html;
    el.querySelectorAll('[data-proposal]').forEach(function(node) {
      node.addEventListener('click', function() {
        var id = node.getAttribute('data-proposal');
        document.getElementById('gw-inspector').innerHTML = '<p class="empty">Loading…</p>';
        vscode.postMessage({ type: 'explorerSelectProposal', proposalId: id });
      });
    });
    el.querySelectorAll('[data-event]').forEach(function(node) {
      node.addEventListener('click', function() {
        var id = node.getAttribute('data-event');
        var e = state.timelineEvents.find(function(x) { return x.eventId === id; });
        if (e) { document.getElementById('gw-inspector').innerHTML = renderEventInspector(e); }
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
    html += '<span class="meta-label">Decision Status</span>' + badge(proposal.status);
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
    html += '<button data-p-action="openReview">Open in Decision Convergence &rarr;</button>';
    html += '<button class="ghost" data-p-action="forkHypothesis">Fork Hypothesis from here</button>';
    html += '<button class="ghost" data-p-action="restore">Restore workspace</button>';
    html += '<button class="ghost" data-p-action="compare">Compare with…</button>';
    html += '</div>';
    html += '<div id="gw-compare-result"></div>';
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
    var el = document.getElementById('gw-compare-result');
    if (!el) { return; }
    var html = '<h2 style="margin-top:14px">Compare</h2>';
    html += '<p class="mono">overlapping files: ' + ((result.overlappingFiles || []).join(', ') || 'none') + '</p>';
    html += '<div style="display:grid;grid-template-columns:1fr 1fr;gap:8px">';
    html += '<pre class="diff-pre">' + renderDiffText(result.diffA) + '</pre>';
    html += '<pre class="diff-pre">' + renderDiffText(result.diffB) + '</pre>';
    html += '</div>';
    el.innerHTML = html;
  }

  // ── Slice 18f — Reasoning Chain vertical timeline ───────────────────────

  function renderReasoningChain(graph) {
    if (!graph || !graph.nodes || !graph.nodes.length) { return ''; }
    var nodes = graph.nodes;
    var edges = graph.edges || [];
    // Build lookup: commitId → list of edge labels for that node
    var edgeLabelsByNode = {};
    edges.forEach(function(e) {
      var labels = edgeLabelsByNode[e.fromCommitId] || [];
      if (labels.indexOf(e.edgeType) === -1) { labels.push(e.edgeType); }
      edgeLabelsByNode[e.fromCommitId] = labels;
      // Also tag the target with an incoming marker
      var toLabels = edgeLabelsByNode[e.toCommitId] || [];
      var incoming = '←' + e.edgeType;
      if (toLabels.indexOf(incoming) === -1) { toLabels.push(incoming); }
      edgeLabelsByNode[e.toCommitId] = toLabels;
    });

    // Only show nodes for the currently selected work unit
    var filtered = nodes.filter(function(n) { return n.workUnitId === (state.selectedNodeId || ''); });
    if (!filtered.length) { return ''; }
    filtered.sort(function(a, b) { return new Date(a.occurredAt) - new Date(b.occurredAt); });

    var html = '<div class="rc-chain"><h2>Reasoning Chain</h2>';
    filtered.forEach(function(node) {
      var labels = edgeLabelsByNode[node.commitId] || [];
      var labelHtml = labels.map(function(l) {
        var cls = l.toLowerCase().replace(/[^a-z]/g, '');
        return '<span class="rc-edge-badge ' + cls + '">' + esc(l) + '</span>';
      }).join('');

      var modelStr = node.agentModel || node.agentProvider || '';
      if (modelStr && node.agentModel && node.agentProvider) { modelStr = node.agentModel + ' @ ' + node.agentProvider; }

      var reasoningExcerpt = node.reasoning || '';
      if (reasoningExcerpt.length > 100) { reasoningExcerpt = reasoningExcerpt.substring(0, 100) + '…'; }

      html += '<div class="rc-node" data-rc-commit="' + esc(node.commitId) + '">';
      html += '<div class="rc-dot"></div>';
      html += '<div class="rc-card">';
      html += '<div class="rc-header">';
      html += stageBadge(node.stage);
      html += '<span class="badge">' + esc(node.action) + '</span>';
      html += labelHtml;
      html += '</div>';
      if (reasoningExcerpt) { html += '<div class="rc-body">' + esc(reasoningExcerpt) + '</div>'; }
      html += '<div class="rc-footer">';
      html += '<span>' + fmtTime(node.occurredAt) + '</span>';
      html += '<span class="mono">' + esc(node.agentId || '') + (modelStr ? ' · ' + esc(modelStr) : '') + '</span>';
      html += '</div>';
      html += '</div></div>';
    });
    html += '</div>';
    return html;
  }

  // ── Messages from extension host ────────────────────────────────────────

  window.addEventListener('message', function(event) {
    var msg = event.data;
    if (msg.type === 'explorerWsInit') {
      connectStageSocket(msg.wsUrl);
      return;
    }
    if (msg.type === 'strategies') {
      var sel = document.getElementById('gw-strategy');
      sel.innerHTML = (msg.strategies || []).map(function(t) {
        var disabled = t.disabled ? ' disabled' : '';
        var title = t.tooltip ? ' title="' + esc(t.tooltip) + '"' : '';
        return '<option value="' + esc(t.name) + '"' + disabled + title + '>' + esc(t.name) + '</option>';
      }).join('');
      return;
    }
    if (msg.type === 'sessions') {
      var sel2 = document.getElementById('gw-session');
      var options = '<option value="">(no exploration)</option>' + (msg.sessions || []).map(function(s) {
        return '<option value="' + esc(s.sessionId) + '">' + esc(s.sessionId) + ' — ' + esc(s.status) + '</option>';
      }).join('');
      sel2.innerHTML = options;
      sel2.value = msg.selectedSessionId || '';
      return;
    }
    if (msg.type === 'tree') {
      renderDecisionTree(msg.workUnits);
      return;
    }
    if (msg.type === 'timeline') {
      renderTimeline(msg.artifacts, msg.events);
      // Slice 18c — store evidence and re-render inspector if node is still selected
      if (msg.evidence) {
        state.selectedNodeEvidence = msg.evidence || [];
      }
      // Slice 18f — store reasoning graph and re-render inspector
      if (msg.reasoningGraph) {
        state.reasoningGraph = msg.reasoningGraph;
      }
      if (state.selectedNodeId && msg.workUnitId === state.selectedNodeId) {
        var wu = state.decisionNodes.find(function(w) { return w.workUnitId === state.selectedNodeId; });
        if (wu) {
          document.getElementById('gw-inspector').innerHTML = renderDecisionInspector(wu);
          bindWorkUnitActionButtons();
        }
      }
      return;
    }
    if (msg.type === 'proposal') {
      document.getElementById('gw-inspector').innerHTML = renderProposalInspector(msg.proposal);
      bindProposalActionButtons();
      return;
    }
    if (msg.type === 'compareResult') {
      renderCompareResult(msg.result);
      return;
    }
    if (msg.type === 'explorerSettings') {
      document.getElementById('gw-llm-profile-checkbox').checked = !!msg.useLlmProfileSelection;
      document.getElementById('gw-max-concurrent-workers').value = msg.maxConcurrentWorkers;
      document.getElementById('gw-scheduler-poll-interval').value = msg.schedulerPollIntervalMs;
      return;
    }
    if (msg.type === 'runResult') {
      var btn = document.getElementById('gw-run');
      btn.disabled = false;
      btn.textContent = '\\u25B6 Run';
      if (msg.success) {
        document.getElementById('gw-goal').value = '';
      }
      return;
    }
  });
`;