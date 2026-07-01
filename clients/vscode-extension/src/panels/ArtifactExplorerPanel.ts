import * as vscode from 'vscode';
import { scopeViewCss, wrapViewScript } from './sharedWebviewChrome';
import type { AgentConfigService, SpawnLlmConfig } from '../AgentConfigService';
import type { ProposalFileChange } from './MergeReviewPanel';
import { COMMANDS } from '../constants';
import { resolveRepositoryPath } from '../repositoryPath';

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
  metadata?: Record<string, string> | null;
}

interface StudioOptions {
  useLlmProfileSelection: boolean;
  blockOverlappingFileScope: boolean;
  maxConcurrentWorkers: number;
  schedulerPollIntervalMs: number;
  requireBuildBeforeProposal: boolean;
  requireTestBeforeProposal: boolean;
  buildCommand: string;
  testCommand: string;
  enforceExpectedOutputKind?: boolean;
  usePromotionBranch?: boolean;
  allowAgentGitCommits?: boolean;
  allowAgentGitPush?: boolean;
  allowAutoRequeue?: boolean;
  blockConflictingOps?: boolean;
  materializerConcurrency?: number;
  defaultClarificationTimeoutSeconds?: number | null;
  defaultClarificationTimeoutBehavior?: string;
}

interface ExecutionSession {
  sessionId: string;
  rootWorkUnitId: string;
  status: string;
  startedAt: string;
}

interface KnownGoodState {
  stateId: string;
  branchId: string;
  description: string;
  verificationResults?: string | null;
  createdAt: string;
  createdBy: string;
  snapshotBranchId?: string | null;
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

// Phase 11 — Conversation Transcripts
interface ConversationToolCall {
  toolUseId: string;
  name: string;
  inputJson: string;
}

interface ConversationToolResult {
  toolUseId: string;
  result: string;
  truncated: boolean;
}

interface ConversationLogEntry {
  logId: string;
  workUnitId: string;
  agentId: string;
  agentRole: string;
  taskId?: string | null;
  cycleNumber: number;
  assistantText?: string | null;
  toolCalls: ConversationToolCall[];
  toolResults: ConversationToolResult[];
  stopReason: string;
  occurredAt: string;
  sessionId?: string | null;
  inputTokens?: number | null;
  outputTokens?: number | null;
  provider?: string | null;
  model?: string | null;
  tokensEstimated?: boolean;
}

// ── Panel ──────────────────────────────────────────────────────────────────

export class GoalWorkspacePanel {
  static readonly containerId = 'shell-pane-goal-workspace';

  private readonly panel: vscode.WebviewPanel;
  private readonly baseUrl: string;
  private readonly configService: AgentConfigService | undefined;
  private readonly secrets: vscode.SecretStorage | undefined;
  private readonly lmProxyBaseUrl: string | undefined;
  private readonly onSessionChanged?: (sessionId: string | undefined) => void;
  private pollTimer?: ReturnType<typeof setInterval>;
  selectedSessionId?: string;

  constructor(
    panel: vscode.WebviewPanel,
    baseUrl: string,
    configService?: AgentConfigService,
    secrets?: vscode.SecretStorage,
    lmProxyBaseUrl?: string,
    onSessionChanged?: (sessionId: string | undefined) => void,
  ) {
    this.panel          = panel;
    this.baseUrl         = baseUrl;
    this.configService   = configService;
    this.secrets         = secrets;
    this.lmProxyBaseUrl  = lmProxyBaseUrl;
    this.onSessionChanged = onSessionChanged;
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
    let pollCount = 0;
    this.pollTimer = setInterval(() => {
      void this.refreshSessions();
      if (this.selectedSessionId) { void this.refreshDecisionTree(this.selectedSessionId); }
      // Re-fetch settings every ~30 s (every 15 ticks at 2 s each) so that values always
      // appear even if the server wasn't ready on the first activate() call.
      if (++pollCount % 15 === 0) { void this.sendSettings(); }
    }, POLL_INTERVAL_MS);
  }

  dispose(): void {
    if (this.pollTimer) { clearInterval(this.pollTimer); this.pollTimer = undefined; }
  }

  private async sendStrategies(): Promise<void> {
    if (!this.configService) { return; }
    const templates = this.configService.getTemplates();
    const profiles = this.configService.getProfiles();
    const orchModels = new Set(
      profiles
        .filter(p => p.domain === 'orchestration' && p.model)
        .map(p => p.model!)
    );
    const strategies: Array<{ name: string; orchestrator: string; workers?: { profile: string }[]; disabled?: boolean; tooltip?: string; experimentType?: string }> = [...templates];

    // ── Slice 22c — Experiment strategies ──────────────────────────────────
    const experimentStrategies = [
      { name: 'Multi-Model Comparison', experimentType: 'Model' },
      { name: 'Architecture Fork', experimentType: 'Architecture' },
      { name: 'Library Comparison', experimentType: 'Library' },
      { name: 'Product Strategy Fork', experimentType: 'Product' },
    ];
    for (const es of experimentStrategies) {
      if (es.name === 'Multi-Model Comparison') {
        if (orchModels.size >= 2) {
          strategies.push({ name: es.name, orchestrator: '', workers: [], disabled: false, experimentType: es.experimentType });
        } else {
          strategies.push({
            name: '__multi_model__',
            orchestrator: '',
            workers: [],
            disabled: true,
            tooltip: 'Configure at least 2 orchestrator profiles with different models in Model & Agent Studio.',
          });
        }
      } else {
        strategies.push({ name: es.name, orchestrator: '', workers: [], disabled: false, experimentType: es.experimentType });
      }
    }
    void this.panel.webview.postMessage({ type: 'strategies', strategies, profiles: profiles.map(p => ({ id: p.id, label: p.label, domain: p.domain, model: p.model })) });
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
      void this.panel.webview.postMessage({
        type: 'explorerSettings', ...opts, ...this.getRepositoryPathSettings(),
      });
    } catch {
      // host not ready yet
    }
  }

  // repositoryPath is extension-side config (nodalmerge.repositoryPath), not a backend
  // WorkspaceOptions field, so it's read directly from VS Code config rather than /studio/options.
  private getRepositoryPathSettings(): { repositoryPathOverride: string; effectiveRepositoryPath: string } {
    const override = vscode.workspace.getConfiguration('nodalmerge').get<string>('repositoryPath', '');
    const autoDetected = vscode.workspace.workspaceFolders?.[0]?.uri?.fsPath ?? '';
    return {
      repositoryPathOverride: override,
      effectiveRepositoryPath: override || autoDetected,
    };
  }

  private async handleBrowseRepositoryPath(): Promise<void> {
    const picked = await vscode.window.showOpenDialog({
      canSelectFolders: true,
      canSelectFiles: false,
      canSelectMany: false,
      openLabel: 'Use as Workspace Folder',
    });
    if (!picked || picked.length === 0) { return; }
    await vscode.workspace.getConfiguration('nodalmerge')
      .update('repositoryPath', picked[0].fsPath, vscode.ConfigurationTarget.Workspace);
    void this.panel.webview.postMessage({ type: 'explorerSettings', ...this.getRepositoryPathSettings() });
  }

  // Cross-repo file reference — "+ Add reference" in the goal box. Scoped to repos already known
  // to the backend registry (IRepositoryRegistryService), per the user's decision to keep a single
  // trust boundary rather than a free-form "any file on disk" picker. Open-but-unregistered VS Code
  // workspace folders are offered as a convenience, and a "Browse for a folder…" entry covers repos
  // that are neither registered nor open — both register the folder (reusing the registry's
  // idempotent CreateAsync) before moving on to the file picker.
  private async handleAddReference(): Promise<void> {
    const known = await this.get<Array<{ repositoryId: string; path: string; label?: string }>>('/studio/repositories');

    type RepoPickItem = vscode.QuickPickItem & { repositoryId?: string; folderPath?: string; browse?: boolean };
    const items: RepoPickItem[] = known.map(r => ({
      label: r.label || r.path,
      description: r.path,
      repositoryId: r.repositoryId,
    }));

    // Path-identity matching is registry logic (RepositoryRegistryService.NormalizePath) — ask
    // the server which open folders aren't registered yet rather than re-normalizing here.
    const openFolders = vscode.workspace.workspaceFolders ?? [];
    if (openFolders.length > 0) {
      const query = openFolders.map(f => `paths=${encodeURIComponent(f.uri.fsPath)}`).join('&');
      const { unregistered } = await this.get<{ unregistered: string[] }>(`/studio/repositories/unregistered?${query}`);
      const unregisteredSet = new Set(unregistered);
      for (const folder of openFolders) {
        if (unregisteredSet.has(folder.uri.fsPath)) {
          items.push({
            label: `$(folder) ${folder.name}`,
            description: `${folder.uri.fsPath} (open, not yet registered)`,
            folderPath: folder.uri.fsPath,
          });
        }
      }
    }
    items.push({ label: '$(folder-opened) Browse for a folder…', description: 'Register a repository that is not open in this VS Code window', browse: true });

    const repoPick = await vscode.window.showQuickPick(items, { placeHolder: 'Reference a file from which repository?' });
    if (!repoPick) { return; }

    let repositoryId = repoPick.repositoryId;
    let repositoryLabel = repoPick.label;
    let folderToRegister = repoPick.folderPath;
    if (repoPick.browse) {
      const picked = await vscode.window.showOpenDialog({
        canSelectFolders: true, canSelectFiles: false, canSelectMany: false,
        openLabel: 'Register for Reference',
      });
      if (!picked || picked.length === 0) { return; }
      folderToRegister = picked[0].fsPath;
      repositoryLabel = picked[0].fsPath.split(/[\\/]/).pop() || picked[0].fsPath;
    }
    if (!repositoryId && folderToRegister) {
      try {
        const registered = await this.post<{ repositoryId: string; path: string }>('/studio/repositories', { path: folderToRegister });
        repositoryId = registered.repositoryId;
      } catch (err) {
        void vscode.window.showErrorMessage('NodalMerge: failed to register repository — ' + String(err));
        return;
      }
    }
    if (!repositoryId) { return; }

    let files: string[];
    try {
      const result = await this.get<{ files: string[] }>(`/studio/repositories/${encodeURIComponent(repositoryId)}/files`);
      files = result.files;
    } catch (err) {
      void vscode.window.showErrorMessage('NodalMerge: failed to list files — ' + String(err));
      return;
    }
    if (files.length === 0) {
      void vscode.window.showInformationMessage('NodalMerge: that repository has no files to reference.');
      return;
    }

    const filePick = await vscode.window.showQuickPick(files, { placeHolder: 'Reference which file?' });
    if (!filePick) { return; }

    void this.panel.webview.postMessage({
      type: 'explorerReferenceAdded', repositoryId, repositoryLabel, path: filePick,
    });
  }

  private async updateOptions(patch: Partial<StudioOptions>): Promise<void> {
    const current = await this.get<StudioOptions>('/studio/options');
    const updated = await this.post<StudioOptions>('/studio/options', { ...current, ...patch });
    void this.panel.webview.postMessage({
      type: 'explorerSettings', ...updated, ...this.getRepositoryPathSettings(),
    });
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
    // Each fetch is caught individually so one slow/erroring call (e.g. a transient blip on
    // orchestration-events) can't take the whole Promise.all down — previously that left the
    // panel stuck on "Loading…" forever, since no 'timeline' message was posted at all when any
    // single call rejected.
    const [artifacts, events, evidence, reasoningGraph] = await Promise.all([
      this.get<ArtifactRef[]>('/studio/workunits/' + workUnitId + '/artifacts').catch(() => null),
      this.get<OrchestrationEvent[]>('/studio/workunits/' + workUnitId + '/orchestration-events').catch(() => null),
      // Slice 18c — fetch evidence for the Decision Lens inspector
      this.get<{ evidence: EvidenceEntry[] }>('/studio/evidence?workUnitId=' + encodeURIComponent(workUnitId)).catch(() => null),
      // Slice 18f — fetch reasoning commit graph for the Reasoning Chain view
      this.get<{ data: ReasoningCommitGraphPayload }>('/studio/projections/ReasoningCommitGraph?workUnitId=' + encodeURIComponent(workUnitId) + '&level=Normal').catch(() => null),
    ]);
    void this.panel.webview.postMessage({
      type: 'timeline', workUnitId,
      artifacts: artifacts ?? [],
      events: events ?? [],
      evidence: evidence?.evidence ?? [],
      reasoningGraph: reasoningGraph?.data ?? null,
    });
    if (artifacts === null || events === null) {
      void vscode.window.showErrorMessage('NodalMerge: failed to load part of the timeline for ' + workUnitId + ' — showing what loaded.');
    }
  }

  private async loadDecisionContext(workUnitId: string): Promise<void> {
    try {
      const result = await this.get<{
        data: {
          workUnitId: string;
          goal: string;
          plan: Array<{ sliceId: string; goal: string; fileScope: string[]; steps: string[] }>;
          assumptions: string[];
          constraints: string[];
          evidence: Array<{ kind: string; summary: string; success: boolean }>;
          allowedTools: string[];
          execution: {
            allSucceeded: boolean;
            buildSystems: string[];
            testSummary?: string | null;
            executedAt: string;
          } | null;
          agentModel?: string | null;
          agentProvider?: string | null;
          steeredFromDecisionId?: string | null;
        };
      }>('/studio/projections/DecisionContext?workUnitId=' + encodeURIComponent(workUnitId) + '&level=Normal');
      void this.panel.webview.postMessage({
        type: 'decisionContext',
        workUnitId,
        context: result.data ?? null,
      });
    } catch (err) {
      void this.panel.webview.postMessage({
        type: 'decisionContext',
        workUnitId,
        context: null,
        error: String(err),
      });
    }
  }

  // Phase 11 — deep-link entry point from Activity Center's "View live transcript" action.
  // Mirrors DecisionConvergencePanel.loadProposal/loadConflict: fetches the single work unit
  // directly by ID rather than requiring it to already be part of the currently selected
  // session's decision tree, since the agent the user clicked on may belong to a different
  // session than whatever Goal Workspace currently has selected.
  async openConversationStandalone(workUnitId: string): Promise<void> {
    try {
      const workUnit = await this.get<Record<string, unknown>>('/studio/workunits/' + workUnitId);
      void this.panel.webview.postMessage({ type: 'gwOpenConversationStandalone', workUnit });
      await this.loadConversationLog(workUnitId);
    } catch (err) {
      void vscode.window.showErrorMessage('NodalMerge: failed to open transcript — ' + String(err));
    }
  }

  // Phase 11 — fetches the durable per-cycle LLM transcript for the Conversation tab. Lazy-loaded
  // like Context, then re-fetched by the webview on a timer while the work unit is still running
  // (see 'explorerSelectConversationTab' handling below and the webview's conversation poll).
  private async loadConversationLog(workUnitId: string): Promise<void> {
    try {
      const entries = await this.get<ConversationLogEntry[]>(
        '/studio/workunits/' + workUnitId + '/conversation-log');
      void this.panel.webview.postMessage({ type: 'conversationLog', workUnitId, entries: entries ?? [] });
    } catch (err) {
      void this.panel.webview.postMessage({
        type: 'conversationLog', workUnitId, entries: [], error: String(err),
      });
    }
  }

  // Slice 25c — fetches the original-vs-counterfactual comparison for the "Compare with
  // Original" link on a counterfactual work unit's badge.
  private async loadCounterfactualComparison(workUnitId: string): Promise<void> {
    try {
      const result = await this.get<{
        data: {
          originalWorkUnitId: string;
          counterfactualWorkUnitId: string;
          originalProposalId: string;
          originals: Array<{
            proposalId: string; goal: string; status: string; model?: string | null;
            provider?: string | null; confidence?: number | null; filesTouched: string[];
            diffSummary?: string | null;
          }>;
          counterfactuals: Array<{
            proposalId: string; goal: string; status: string; model?: string | null;
            provider?: string | null; confidence?: number | null; filesTouched: string[];
            diffSummary?: string | null;
          }>;
          originalModel?: string | null;
          originalProvider?: string | null;
          counterfactualModel?: string | null;
          counterfactualProvider?: string | null;
          whichWasBetter?: string | null;
          comparedAt: string;
        };
      }>('/studio/projections/CounterfactualComparison?workUnitId=' + encodeURIComponent(workUnitId) + '&level=Normal');
      void this.panel.webview.postMessage({
        type: 'counterfactualComparison',
        comparison: result.data ?? null,
      });
    } catch (err) {
      void this.panel.webview.postMessage({
        type: 'counterfactualComparison',
        comparison: null,
        error: String(err),
      });
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
          this.onSessionChanged?.(this.selectedSessionId);
          if (this.selectedSessionId) { await this.refreshDecisionTree(this.selectedSessionId); }
          break;
        case 'explorerRun':
          await this.handleRun(
            msg.strategy as string, msg.goal as string,
            (msg.reviewPolicy as string) || undefined,
            !!msg.bypassPromotionBranch,
            (msg.forkConfig as Array<{ profileId: string; constraintHint?: string }>) || [],
            (msg.referenceFiles as Array<{ repositoryId: string; path: string }>) || [],
          );
          break;
        case 'explorerAddReference':
          await this.handleAddReference();
          break;
        case 'explorerPickWinner':
          await this.handlePickWinner(
            msg.parentId as string,
            msg.winnerId as string,
          );
          break;
        case 'explorerSelectWorkUnit':
          await this.loadTimeline(msg.workUnitId as string);
          break;
        case 'explorerLoadComparison':
          await this.loadHypothesisComparison(msg.parentId as string);
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
        case 'explorerSetRequireBuildBeforeProposal':
          await this.updateOptions({ requireBuildBeforeProposal: msg.value as boolean });
          break;
        case 'explorerSetRequireTestBeforeProposal':
          await this.updateOptions({ requireTestBeforeProposal: msg.value as boolean });
          break;
        case 'explorerSetEnforceExpectedOutputKind':
          await this.updateOptions({ enforceExpectedOutputKind: msg.value as boolean });
          break;
        case 'explorerSetBlockConflictingOps':
          await this.updateOptions({ blockConflictingOps: msg.value as boolean });
          break;
        case 'explorerSetAllowAutoRequeue':
          await this.updateOptions({ allowAutoRequeue: msg.value as boolean });
          break;
        case 'explorerSetAllowAgentGitCommits':
          await this.updateOptions({ allowAgentGitCommits: msg.value as boolean });
          break;
        case 'explorerSetAllowAgentGitPush':
          await this.updateOptions({ allowAgentGitPush: msg.value as boolean });
          break;
        case 'explorerSetMaterializerConcurrency':
          await this.updateOptions({ materializerConcurrency: msg.value as number });
          break;
        case 'explorerSetClarificationTimeoutSeconds':
          await this.updateOptions({ defaultClarificationTimeoutSeconds: msg.value as number });
          break;
        case 'explorerSetClarificationTimeoutBehavior':
          await this.updateOptions({ defaultClarificationTimeoutBehavior: msg.value as string });
          break;
        case 'explorerBrowseRepositoryPath':
          await this.handleBrowseRepositoryPath();
          break;
        case 'explorerClearRepositoryPath':
          await vscode.workspace.getConfiguration('nodalmerge')
            .update('repositoryPath', '', vscode.ConfigurationTarget.Workspace);
          void this.panel.webview.postMessage({ type: 'explorerSettings', ...this.getRepositoryPathSettings() });
          break;
        case 'explorerSteeringAction':
          if ((msg.action as string) === 'steerDeadLetterRetrySend') {
            await this.handleSteeredRetrySend(msg);
          } else {
            await this.handleSteeringAction(
              msg.action as string,
              msg.workUnitId as string,
              (msg.agentId as string) ?? '',
            );
          }
          break;
        case 'explorerGoalPause':
          await this.handleGoalPause(msg.goalId as string);
          break;
        case 'explorerGoalResume':
          await this.handleGoalResume(msg.goalId as string);
          break;
        case 'explorerCounterfactualAction':
          await this.handleCounterfactualAction(
            msg.workUnitId as string,
          );
          break;
        case 'explorerSelectContextTab':
          await this.loadDecisionContext(msg.workUnitId as string);
          break;
        case 'explorerSelectConversationTab':
          await this.loadConversationLog(msg.workUnitId as string);
          break;
        case 'explorerLoadCounterfactualComparison':
          await this.loadCounterfactualComparison(msg.workUnitId as string);
          break;
        default:
          return;
      }
    } catch (err) {
      void vscode.window.showErrorMessage('NodalMerge: ' + String(err));
    }
  }

  private async handleGoalPause(goalId: string): Promise<void> {
    try {
      await this.post(`/studio/goals/${goalId}/pause`, { pausedBy: 'user' });
      void vscode.window.showInformationMessage('NodalMerge: Goal paused.');
      await this.refreshSessions();
    } catch (err) {
      void vscode.window.showErrorMessage('NodalMerge: Pause failed — ' + String(err));
    }
  }

  private async handleGoalResume(goalId: string): Promise<void> {
    const steering = await vscode.window.showInputBox({
      prompt: 'Optional: add steering message for the agent (leave blank to resume as-is)',
      placeHolder: 'e.g. Focus on the auth module first',
      ignoreFocusOut: true,
    });
    if (steering === undefined) { return; } // user cancelled
    try {
      await this.post(`/studio/goals/${goalId}/resume`, {
        steering: steering.trim() || undefined,
        resumedBy: 'user',
      });
      void vscode.window.showInformationMessage('NodalMerge: Goal resumed.');
      await this.refreshSessions();
    } catch (err) {
      void vscode.window.showErrorMessage('NodalMerge: Resume failed — ' + String(err));
    }
  }

  private async handleSteeringAction(
    action: string, workUnitId: string, agentId: string,
  ): Promise<void> {
    if (action === 'steerPause') {
      // Open input box for constraint injection
      const injected = await vscode.window.showInputBox({
        prompt: 'Inject constraint or redirect...',
        placeHolder: 'e.g. use Redis instead of SQLite',
        ignoreFocusOut: true,
      });
      if (!injected || !injected.trim()) { return; }

      try {
        await this.post('/studio/steering/redirect', {
          workUnitId,
          agentId,
          injectedConstraint: injected.trim(),
          sessionId: this.selectedSessionId ?? undefined,
        });
        void vscode.window.showInformationMessage('NodalMerge: Forked with constraint — ' + injected.trim());
        if (this.selectedSessionId) { await this.refreshDecisionTree(this.selectedSessionId); }
      } catch (err) {
        void vscode.window.showErrorMessage('NodalMerge: Steering failed — ' + String(err));
      }
      return;
    }

    if (action === 'steerDeadLetterRetry') {
      let entry: { entryId: string; reason: string; attemptCount: number } | undefined;
      try {
        entry = await this.get<{ entryId: string; reason: string; attemptCount: number }>(
          '/studio/dead-letter/by-work-unit/' + workUnitId,
        );
      } catch (err) {
        void vscode.window.showErrorMessage('NodalMerge: Could not load dead-letter entry — ' + String(err));
        return;
      }

      const steeringContext = await vscode.window.showInputBox({
        prompt: 'Failed because: ' + entry.reason + ' — describe the correction',
        placeHolder: 'e.g. the file lives at repo root, not under src/ — start the search there',
        ignoreFocusOut: true,
      });
      if (!steeringContext || !steeringContext.trim()) { return; }

      try {
        await this.post('/studio/dead-letter/' + entry.entryId + '/retry-with-context', {
          steeringContext: steeringContext.trim(),
        });
        void vscode.window.showInformationMessage('NodalMerge: Retrying with steering context.');
        if (this.selectedSessionId) { await this.refreshDecisionTree(this.selectedSessionId); }
      } catch (err) {
        void vscode.window.showErrorMessage('NodalMerge: Steered retry failed — ' + String(err));
      }
      return;
    }

    if (action === 'steerForkFromNode') {
      const goal = await vscode.window.showInputBox({
        prompt: 'Goal for the fork from this node',
        placeHolder: 'e.g. Retry with a different approach',
        ignoreFocusOut: true,
      });
      if (!goal || !goal.trim()) { return; }

      const profile = await this.configService?.pickProfile('Select profile to run the forked work unit');
      if (!profile) { return; }

      const constraintText = await vscode.window.showInputBox({
        prompt: 'Constraint (optional, press Enter to skip)',
        placeHolder: 'e.g. use gRPC instead of REST',
        ignoreFocusOut: true,
      });

      try {
        await this.post('/studio/steering/fork-from-node', {
          workUnitId,
          newGoal: goal.trim(),
          constraintText: constraintText?.trim() || undefined,
          profileId: profile.id,
          sessionId: this.selectedSessionId ?? undefined,
        });
        void vscode.window.showInformationMessage('NodalMerge: Forked from node — ' + goal.trim());
        if (this.selectedSessionId) { await this.refreshDecisionTree(this.selectedSessionId); }
      } catch (err) {
        void vscode.window.showErrorMessage('NodalMerge: Fork from node failed — ' + String(err));
      }
    }

  }

  // Phase Y — Steer & Retry with credential override from the Decision Lens inline UI.
  private async handleSteeredRetrySend(msg: Record<string, unknown>): Promise<void> {
    const workUnitId = msg.workUnitId as string;
    const steeringContext = (msg.steeringContext as string) || '';
    const overrideModel = (msg.overrideModel as string) || undefined;
    const overrideBaseUrl = (msg.overrideBaseUrl as string) || undefined;
    const overrideApiKey = (msg.overrideApiKey as string) || undefined;
    const overrideProvider = (msg.overrideProvider as string) || undefined;
    const overrideProfileId = (msg.overrideProfileId as string) || undefined;

    // Load the dead-letter entry to get the entryId
    let entry: { entryId: string; reason: string; attemptCount: number } | undefined;
    try {
      entry = await this.get<{ entryId: string; reason: string; attemptCount: number }>(
        '/studio/dead-letter/by-work-unit/' + workUnitId,
      );
    } catch (err) {
      void vscode.window.showErrorMessage('NodalMerge: Could not load dead-letter entry — ' + String(err));
      return;
    }

    try {
      await this.post('/studio/dead-letter/' + entry.entryId + '/retry-with-context', {
        steeringContext: steeringContext || 'Retry with steering direction.',
        overrideModel,
        overrideBaseUrl,
        overrideApiKey,
        overrideProvider,
        overrideProfileId,
      });
      void vscode.window.showInformationMessage(
        'NodalMerge: Retrying with steering' + (overrideProfileId ? ' using profile ' + overrideProfileId : '') + '.');
      if (this.selectedSessionId) { await this.refreshDecisionTree(this.selectedSessionId); }
    } catch (err) {
      void vscode.window.showErrorMessage('NodalMerge: Steered retry failed — ' + String(err));
    }
  }

  // ── Slice 25c — Counterfactual: "Run with different model" ───────────────

  private async handleCounterfactualAction(workUnitId: string): Promise<void> {
    try {
      // 1. Fetch the latest proposal for this work unit
      const artifacts = await this.get<ArtifactRef[]>('/studio/workunits/' + workUnitId + '/artifacts');
      const proposals = artifacts.filter(a => a.type === 'MergeProposal');
      if (proposals.length === 0) {
        void vscode.window.showWarningMessage('NodalMerge: no proposal available to counterfactual from — the work unit must have at least one MergeProposal artifact.');
        return;
      }
      const latestProposal = proposals[proposals.length - 1];

      // 2. Pick a different profile/model for the counterfactual
      const profile = await this.configService?.pickProfile('Select a different profile/model for counterfactual');
      if (!profile) { return; }

      // 3. Optionally override the goal
      const goalOverride = await vscode.window.showInputBox({
        prompt: 'Goal override (optional, press Enter to use default)',
        placeHolder: '[counterfactual] Re-run with ' + (profile.label || profile.id),
        ignoreFocusOut: true,
      });

      // 4. Create the counterfactual
      const result = await this.post<{
        counterfactualId: string;
        originalWorkUnitId: string;
        newWorkUnitId: string;
        originalProposalId: string;
      }>('/studio/counterfactuals', {
        proposalId: latestProposal.artifactId,
        newProfileId: profile.id,
        newGoalOverride: goalOverride?.trim() || undefined,
        sessionId: this.selectedSessionId ?? undefined,
      });

      void vscode.window.showInformationMessage(
        'NodalMerge: Counterfactual created — ' + result.newWorkUnitId
        + ' (original: ' + result.originalWorkUnitId + ')');

      // 5. Refresh the decision tree to show the new counterfactual node
      if (this.selectedSessionId) { await this.refreshDecisionTree(this.selectedSessionId); }
    } catch (err) {
      void vscode.window.showErrorMessage('NodalMerge: Counterfactual failed — ' + String(err));
    }
  }

  private async handleRun(
    strategy: string, goal: string, reviewPolicy?: string, bypassPromotionBranch?: boolean,
    forkConfig?: Array<{ profileId: string; constraintHint?: string }>,
    referenceFiles?: Array<{ repositoryId: string; path: string }>,
  ): Promise<void> {
    const referenceFilesPatch = referenceFiles && referenceFiles.length ? { referenceFiles } : {};
    if (!goal || !goal.trim()) {
      void vscode.window.showWarningMessage('NodalMerge: enter a goal before running.');
      return;
    }

    // Slice 22c fix — Architecture/Library/Product Fork experiments. These were previously
    // falling through to the topology-template lookup below (which always failed with
    // "Strategy not found", since these strategy names are not template names). Route them
    // to the real ExperimentService instead, using the fork-config panel's profile/constraint
    // entries. Each fork is auto-enqueued by the backend when a profileId is present — no
    // separate spawn call needed here (see ExperimentService.CreateAsync).
    const EXPERIMENT_FORK_TYPES: Record<string, string> = {
      'Architecture Fork': 'Architecture',
      'Library Comparison': 'Library',
      'Product Strategy Fork': 'Product',
    };
    if (strategy in EXPERIMENT_FORK_TYPES) {
      const forks = (forkConfig ?? []).filter(f => f.profileId || f.constraintHint);
      if (forks.length < 2) {
        void vscode.window.showErrorMessage(
          `NodalMerge: ${strategy} requires at least 2 fork entries with a constraint — configure them in the fork panel above.`,
        );
        void this.panel.webview.postMessage({ type: 'runResult', success: false, message: 'At least 2 forks required.' });
        return;
      }
      try {
        const result = await this.post<{ experimentId: string; parentWorkUnitId: string; forkWorkUnitIds: string[] }>(
          '/studio/experiments',
          {
            goal,
            owner: 'user',
            forkType: EXPERIMENT_FORK_TYPES[strategy],
            forks: forks.map(f => ({ profileId: f.profileId || undefined, constraintText: f.constraintHint || undefined })),
            ...(reviewPolicy ? { reviewPolicy } : {}),
          },
        );
        const session = await this.post<ExecutionSession>('/studio/sessions', {
          rootWorkUnitId: result.parentWorkUnitId,
          profileIds: forks.map(f => f.profileId).filter(Boolean),
        });
        this.selectedSessionId = session.sessionId;
        this.onSessionChanged?.(this.selectedSessionId);
        void this.panel.webview.postMessage({ type: 'runResult', success: true, sessionId: session.sessionId });
        await this.refreshSessions();
        await this.refreshDecisionTree(session.sessionId);
        void vscode.window.showInformationMessage(`${strategy} experiment started: ${forks.length} forks.`);
      } catch (err) {
        void this.panel.webview.postMessage({ type: 'runResult', success: false, message: String(err) });
        void vscode.window.showErrorMessage(`NodalMerge: ${strategy} failed — ` + String(err));
      }
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

        const repositoryPath = resolveRepositoryPath();
        const reviewAndTarget = {
          ...(reviewPolicy ? { reviewPolicy } : {}),
          bypassPromotionBranch: !!bypassPromotionBranch,
        };
        // Create a parent work unit to hold both model runs
        const parentWu = await this.post<{ workUnitId: string }>('/studio/workunits', {
          goal,
          owner: 'user',
          ...reviewAndTarget,
          ...(repositoryPath ? { repositoryPath } : {}),
          ...referenceFilesPatch,
        });

        // Create two child work units — one per model
        const [childA, childB] = await Promise.all([
          this.post<{ workUnitId: string }>('/studio/workunits', {
            goal: `[${modelAProfile.model ?? modelAProfile.id}] ${goal}`,
            owner: modelAProfile.id,
            parentWorkUnitId: parentWu.workUnitId,
            ...reviewAndTarget,
            ...(repositoryPath ? { repositoryPath } : {}),
          }),
          this.post<{ workUnitId: string }>('/studio/workunits', {
            goal: `[${modelBProfile.model ?? modelBProfile.id}] ${goal}`,
            owner: modelBProfile.id,
            parentWorkUnitId: parentWu.workUnitId,
            ...reviewAndTarget,
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
        this.onSessionChanged?.(this.selectedSessionId);
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

      // Agent Topology — resolve credentials for any stage that has its own profile configured;
      // unset stages fall back to the Orchestrator's credentials on the backend.
      const stageCredentials: Record<string, SpawnLlmConfig> = {};
      const stagePlans: Array<[string, string | undefined]> = [
        ['Plan', template.planner],
        ['Execute', template.worker],
        ['Review', template.reviewer],
      ];
      for (const [stage, profileId] of stagePlans) {
        if (!profileId) { continue; }
        const cfg = await this.configService.resolveSpawnLlmConfig(profileId, this.secrets, this.lmProxyBaseUrl);
        if (!cfg) {
          throw new Error(`Profile "${profileId}" is missing LLM credentials — set it up in Model & Agent Studio.`);
        }
        stageCredentials[stage] = cfg;
      }

      const repositoryPath = resolveRepositoryPath();
      const rootWu = await this.post<{ workUnitId: string }>('/studio/workunits', {
        goal,
        owner: template.orchestrator,
        ...(reviewPolicy ? { reviewPolicy } : {}),
        bypassPromotionBranch: !!bypassPromotionBranch,
        ...(repositoryPath ? { repositoryPath } : {}),
        ...referenceFilesPatch,
      });

      const session = await this.post<ExecutionSession>('/studio/sessions', {
        rootWorkUnitId: rootWu.workUnitId,
        profileIds: [template.orchestrator],
      });

      await this.post('/studio/agents/spawn', {
        agentType: 'orchestrator',
        workUnitId: rootWu.workUnitId,
        ...orchCfg,
        ...(Object.keys(stageCredentials).length > 0 ? { stageCredentials } : {}),
      });

      this.selectedSessionId = session.sessionId;
      this.onSessionChanged?.(this.selectedSessionId);
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
      const goalB = await vscode.window.showInputBox({
        prompt: 'Goal for the second hypothesis fork', ignoreFocusOut: true,
      });
      if (!goalB) { return; }

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
      return;
    }

    if (action === 'forkKnownGood') {
      const wu = await this.get<WorkUnit>('/studio/workunits/' + workUnitId);
      await this.forkFromKnownGood(wu.branchId);
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

  private async loadHypothesisComparison(parentId: string): Promise<void> {
    try {
      const result = await this.get<{ dataJson: string }>(
        '/studio/projections/HypothesisComparison?workUnitId=' + encodeURIComponent(parentId));
      const payload = JSON.parse(result.dataJson);
      void this.panel.webview.postMessage({ type: 'comparisonData', parentId, payload });
    } catch {
      // Evidence/score display is a bonus on top of the raw side-by-side compare view — if the
      // projection fails for any reason, the compare view still works without it.
    }
  }

  private async handlePickWinner(parentId: string, winnerId: string): Promise<void> {
    try {
      // Comparison engine — single call converges the experiment server-side: approves the
      // winner's latest proposal, rejects every other sibling's dangling proposal, and writes
      // DecisionNode/HypothesisNode convergence state for all of them. Replaces the old
      // client-driven approve-one/reject-each-loser REST dance.
      const result = await this.post<{ rejectedWorkUnitIds: string[] }>(
        '/studio/experiments/' + parentId + '/converge',
        { winnerId });

      void vscode.window.showInformationMessage(
        'NodalMerge: Winner accepted, ' + result.rejectedWorkUnitIds.length + ' rejected.'
      );
      if (this.selectedSessionId) { await this.refreshDecisionTree(this.selectedSessionId); }
    } catch (err) {
      void vscode.window.showErrorMessage('NodalMerge: Pick winner failed — ' + String(err));
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

  private async forkFromKnownGood(branchId: string): Promise<void> {
    const states = await this.get<KnownGoodState[]>('/studio/state/knownGood/' + encodeURIComponent(branchId));
    if (states.length === 0) {
      void vscode.window.showWarningMessage('NodalMerge: no known-good checkpoints marked for this branch yet.');
      return;
    }
    const picked = states.length === 1 ? states[0] : await vscode.window.showQuickPick(
      states.map(s => ({ label: s.description, description: new Date(s.createdAt).toLocaleString(), state: s })),
      { placeHolder: 'Select a known-good checkpoint', ignoreFocusOut: true },
    ).then(p => p?.state);
    if (!picked) { return; }

    const goal = await vscode.window.showInputBox({
      prompt: 'Goal for the new fork', placeHolder: 'e.g. Retry from the last known-good checkpoint', ignoreFocusOut: true,
    });
    if (!goal) { return; }
    const profile = await this.configService?.pickProfile('Select profile to run the new fork');
    if (!profile) { return; }
    const result = await this.post<{ workUnitId: string }>(
      '/studio/state/' + picked.stateId + '/fork',
      { goal, profileId: profile.id, ...(this.selectedSessionId ? { sessionId: this.selectedSessionId } : {}) });
    void vscode.window.showInformationMessage('NodalMerge: Forked new work unit ' + result.workUnitId + ' from known-good checkpoint.');
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
  .gw-repo-path-row { cursor: default; }
  .gw-repo-path-row #gw-repo-path-display { flex: 1; min-width: 0; font-size: 0.85em; padding: 2px 6px; }
  /* Slice 21c — inline Review/Target controls */
  .gw-options-row {
    flex-shrink: 0; padding: 6px 14px; border-bottom: 1px solid var(--nm-border);
    display: flex; gap: 18px; flex-wrap: wrap; align-items: center; font-size: 0.82em;
  }
  .gw-radio-group { display: flex; gap: 12px; align-items: center; }
  .gw-radio-group-label { opacity: 0.6; text-transform: uppercase; font-size: 0.72em; letter-spacing: 0.05em; margin-right: 4px; }
  .gw-radio-option { display: flex; align-items: center; gap: 4px; cursor: pointer; }
  .gw-target-row { display: none; }
  .gw-target-row.visible { display: flex; }
  .gw-body { flex: 1; display: flex; overflow: hidden; min-height: 0; }
  .gw-col { overflow-y: auto; padding: 10px 12px; }
  .gw-decision-tree { width: 280px; flex-shrink: 0; }
  .gw-timeline { flex: 1; min-width: 0; }
  .gw-inspector { width: 320px; flex-shrink: 0; }
  .gw-resizer { width: 5px; flex-shrink: 0; cursor: col-resize; background: var(--nm-border); }
  .gw-resizer:hover, .gw-resizer.gw-resizing { background: var(--nm-info); }
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

  /* Slice 22c — Inline fork config panel */
  .gw-fork-config {
    flex-shrink: 0; padding: 8px 14px; border-bottom: 1px solid var(--nm-border);
    background: color-mix(in srgb, var(--nm-info) 8%, var(--nm-section-bg));
    display: none; gap: 10px; flex-wrap: wrap; align-items: flex-end;
  }
  .gw-fork-config.visible { display: flex; }
  .gw-fork-config .gw-field { min-width: 130px; }
  .gw-fork-entry {
    border: 1px solid var(--nm-border); border-radius: 4px;
    padding: 6px 10px; background: var(--nm-section-bg);
    display: flex; flex-direction: column; gap: 4px; min-width: 180px;
  }

  /* Cross-repo file reference chips, below the goal options row */
  .gw-reference-row {
    display: flex; gap: 8px; align-items: center; flex-wrap: wrap;
    padding: 4px 14px 8px; border-bottom: 1px solid var(--nm-border);
  }
  .gw-reference-chips { display: flex; gap: 6px; flex-wrap: wrap; }
  .gw-reference-chip {
    display: inline-flex; align-items: center; gap: 5px;
    border: 1px solid var(--nm-border); border-radius: 12px;
    padding: 2px 8px; font-size: 0.76em; background: var(--nm-section-bg);
  }
  .gw-reference-chip .gw-reference-chip-remove { cursor: pointer; opacity: 0.6; }
  .gw-reference-chip .gw-reference-chip-remove:hover { opacity: 1; }
  .gw-fork-entry-title { font-size: 0.78em; font-weight: 600; }
  .gw-fork-entry select { font-size: 0.82em; }

  /* Slice 22c — Experiment parent node badges */
  .dn-exp-badges { display: flex; gap: 4px; margin-top: 2px; }
  .dn-exp-badges .badge.forks { background: #b180d7; color: #fff; }
  .dn-exp-badges .badge.cf { background: #2da198; color: #fff; }
  .compare-link {
    font-size: 0.74em; cursor: pointer; color: var(--nm-info); text-decoration: underline;
    white-space: nowrap;
  }
  .compare-link:hover { opacity: 0.8; }

  /* Slice 22c — Compare Results side-by-side view in Decision Lens */
  .cmp-results { margin-top: 12px; }
  .cmp-header { display: flex; justify-content: space-between; align-items: center; margin-bottom: 6px; }
  .cmp-fork-cards { display: flex; gap: 8px; }
  .cmp-fork-card {
    flex: 1; min-width: 0; border: 1px solid var(--nm-border); border-radius: 6px;
    padding: 8px 10px; background: var(--nm-section-bg);
  }
  .cmp-fork-card.selected { border-color: var(--nm-success); border-width: 2px; }
  .cmp-fork-card.rejected { opacity: 0.45; }
  .cmp-fk-model { font-size: 0.82em; font-weight: 600; }
  .cmp-fk-goal { font-size: 0.78em; opacity: 0.7; margin: 4px 0; }
  .cmp-fk-meta { display: flex; gap: 6px; flex-wrap: wrap; font-size: 0.72em; margin-top: 4px; }
  .cmp-pick-bar { margin-top: 10px; display: flex; gap: 6px; align-items: center; }
  .cmp-pick-bar button.pick-winner { background: var(--nm-success); color: #fff; }
  .cmp-pick-bar button.pick-winner:disabled { opacity: 0.4; cursor: not-allowed; }

  /* Slice 24b — Decision Lens tab bar */
  .gw-tab-bar { display: flex; gap: 0; margin-bottom: 8px; border-bottom: 1px solid var(--nm-border); }
  .gw-tab-btn {
    background: transparent; color: var(--nm-fg); border: none; border-bottom: 2px solid transparent;
    padding: 3px 10px; font-size: 0.78em; cursor: pointer; font-family: var(--nm-font);
    opacity: 0.55; font-weight: 600; text-transform: uppercase; letter-spacing: 0.05em;
  }
  .gw-tab-btn.active { opacity: 1; border-bottom-color: var(--nm-info); color: var(--nm-info); }
  .gw-tab-btn:hover { opacity: 0.8; }
  .gw-tab-panel { display: none; }
  .gw-tab-panel.active { display: block; }

  /* Phase 11 — Conversation tab */
  .conv-entry {
    border: 1px solid var(--nm-border); border-radius: 6px; padding: 8px 10px;
    margin-bottom: 8px; background: var(--nm-section-bg);
  }
  .conv-entry-head { display: flex; align-items: center; gap: 8px; margin-bottom: 4px; }
  .conv-text { white-space: pre-wrap; font-size: 0.85em; margin: 4px 0; }
  .conv-tool { margin-top: 4px; font-size: 0.82em; }
  .conv-tool summary { cursor: pointer; opacity: 0.85; }
  .conv-tool-label { opacity: 0.55; font-size: 0.85em; margin-top: 4px; }
  .conv-pre {
    white-space: pre-wrap; background: rgba(127,127,127,0.12); border-radius: 3px;
    padding: 6px; margin: 2px 0; font-family: var(--nm-mono); max-height: 240px; overflow-y: auto;
  }

  /* Slice 24b — Context tab structured sections */
  .ctx-section { margin-bottom: 12px; }
  .ctx-section h3 {
    font-size: 0.74em; font-weight: 700; text-transform: uppercase; letter-spacing: 0.06em;
    opacity: 0.5; margin: 0 0 4px;
  }
  .ctx-item { font-size: 0.84em; padding: 2px 0; }
  .ctx-item.mono { font-family: var(--nm-mono); font-size: 0.8em; }
  .ctx-evidence { font-size: 0.85em; padding: 2px 0; }
  .ctx-evidence.success { color: var(--nm-success); }
  .ctx-evidence.fail { color: var(--nm-error); }
  .ctx-plan-entry { border: 1px solid var(--nm-border); border-radius: 4px; padding: 6px 10px; margin-bottom: 4px; background: var(--nm-section-bg); }
  .ctx-plan-slice { font-size: 0.8em; font-weight: 600; }
  .ctx-plan-goal { font-size: 0.82em; opacity: 0.75; margin-top: 2px; }
  .ctx-plan-steps { font-size: 0.76em; margin-top: 4px; padding-left: 12px; }
  .ctx-plan-steps li { opacity: 0.65; }
  .ctx-copy-btn { font-size: 0.74em; margin-top: 8px; opacity: 0.6; }
  .ctx-copy-btn:hover { opacity: 1; }
`;

const GW_HTML = `
  <div class="gw-topbar">
    <div class="gw-field">
      <label>Active Exploration</label>
      <div style="display:flex;gap:6px;align-items:center">
        <select id="gw-session"><option value="">(no exploration)</option></select>
        <button id="gw-session-pause" class="ghost" title="Pause this exploration" style="display:none;color:var(--nm-warn);border-color:var(--nm-warn);padding:3px 8px;font-size:0.78em">&#x23F8; Pause</button>
        <button id="gw-session-resume" class="ghost" title="Resume this exploration" style="display:none;padding:3px 8px;font-size:0.78em">&#x25B6; Resume</button>
      </div>
    </div>
    <div class="gw-field">
      <label>Investigation Strategy</label>
      <select id="gw-strategy"></select>
    </div>
    <div class="gw-field">
      <label>Goal</label>
      <textarea id="gw-goal" placeholder="Describe a goal — e.g. Add dark mode support across the settings UI"></textarea>
    </div>
    <button id="gw-run">&#x25B6; Run</button>
    <button id="gw-settings-btn" class="ghost" title="Exploration Settings">&#9881;</button>
  </div>
  <div class="gw-options-row">
    <div class="gw-radio-group">
      <span class="gw-radio-group-label">Review</span>
      <label class="gw-radio-option"><input type="radio" name="gw-review-policy" value="HumanRequired" checked/> Human Required</label>
      <label class="gw-radio-option"><input type="radio" name="gw-review-policy" value="AgentApproval"/> Agent Approval</label>
      <label class="gw-radio-option"><input type="radio" name="gw-review-policy" value="Hybrid"/> Hybrid (5 min)</label>
    </div>
    <div class="gw-radio-group gw-target-row" id="gw-target-row">
      <span class="gw-radio-group-label">Target</span>
      <label class="gw-radio-option"><input type="radio" name="gw-target" value="candidate" checked/> Candidate Branch</label>
      <label class="gw-radio-option"><input type="radio" name="gw-target" value="direct"/> Direct</label>
    </div>
  </div>
  <div class="gw-reference-row">
    <span class="gw-radio-group-label">References</span>
    <div id="gw-reference-chips" class="gw-reference-chips"></div>
    <button id="gw-add-reference-btn" class="ghost" style="padding:3px 10px;font-size:0.78em">+ Add reference</button>
  </div>
  <div id="gw-fork-config" class="gw-fork-config">
    <div id="gw-fork-entries" style="display:flex;gap:10px;flex-wrap:wrap;align-items:flex-end"></div>
  </div>
  <div id="gw-settings-panel" class="gw-settings-panel" style="display:none">
    <label class="gw-settings-row" style="padding-bottom:8px;border-bottom:1px solid var(--nm-border)">
      Workspace Folder
    </label>
    <div class="gw-settings-row gw-repo-path-row">
      <input type="text" id="gw-repo-path-display" readonly title="The repository Studio operates against"/>
      <button id="gw-repo-path-browse" class="ghost">Browse&hellip;</button>
      <button id="gw-repo-path-clear" class="ghost" title="Use the open VS Code folder instead">Use Open Folder</button>
    </div>
    <label class="gw-settings-row" style="margin-top:8px;border-top:1px solid var(--nm-border);padding-top:8px">
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
    <label class="gw-settings-row" style="margin-top:8px;border-top:1px solid var(--nm-border);padding-top:8px">
      Pipeline Gates
    </label>
    <label class="gw-settings-row">
      <input type="checkbox" id="gw-require-build-checkbox"/>
      Require build before proposal
    </label>
    <label class="gw-settings-row">
      <input type="checkbox" id="gw-require-test-checkbox"/>
      Require tests before proposal
    </label>
    <label class="gw-settings-row">
      <input type="checkbox" id="gw-enforce-output-kind-checkbox"/>
      Reject worker proposals with no file changes
    </label>
    <label class="gw-settings-row" style="margin-top:8px;border-top:1px solid var(--nm-border);padding-top:8px">
      Merge &amp; Conflict
    </label>
    <label class="gw-settings-row">
      <input type="checkbox" id="gw-block-conflicting-ops-checkbox"/>
      Block conflicting ops (reject second op on conflict)
    </label>
    <label class="gw-settings-row">
      <input type="checkbox" id="gw-allow-auto-requeue-checkbox"/>
      Auto-requeue losing work unit when all merge strategies fail
    </label>
    <label class="gw-settings-row" style="margin-top:8px;border-top:1px solid var(--nm-border);padding-top:8px">
      Git Integration <span style="font-size:0.8em;color:var(--nm-text-muted)">(opt-in, use with care)</span>
    </label>
    <label class="gw-settings-row">
      <input type="checkbox" id="gw-allow-agent-git-commits-checkbox"/>
      Allow agents to create git commits on export
    </label>
    <label class="gw-settings-row">
      <input type="checkbox" id="gw-allow-agent-git-push-checkbox"/>
      Allow agents to push git branches on export
    </label>
    <label class="gw-settings-row" style="margin-top:8px;border-top:1px solid var(--nm-border);padding-top:8px">
      Repository
    </label>
    <label class="gw-settings-row">
      Materializer concurrency
      <input type="number" id="gw-materializer-concurrency" min="1" max="16" step="1" style="width:60px"/>
    </label>
    <label class="gw-settings-row" style="margin-top:8px;border-top:1px solid var(--nm-border);padding-top:8px">
      Clarification Timeout <span style="font-size:0.8em;opacity:0.6">— if an agent asks and you don't reply in time</span>
    </label>
    <label class="gw-settings-row">
      Auto-respond after
      <input type="number" id="gw-clarification-timeout-seconds" min="0" step="10" style="width:70px" title="Seconds before auto-responding (0 = wait indefinitely)"/>
      seconds&ensp;(0 = wait forever)
    </label>
    <label class="gw-settings-row">
      Timeout behavior
      <select id="gw-clarification-timeout-behavior" style="font-size:0.85em">
        <option value="auto_continue">auto_continue — let the agent decide</option>
        <option value="auto_abandon">auto_abandon — stop the work unit</option>
      </select>
    </label>
  </div>
  <div class="gw-body">
    <div class="gw-col gw-decision-tree" id="gw-col-tree">
      <h2>Decision Tree</h2>
      <div id="gw-tree"><p class="empty">Create a goal to start exploring decisions.</p></div>
    </div>
    <div class="gw-resizer" id="gw-resizer-tree"></div>
    <div class="gw-col gw-timeline" id="gw-col-timeline">
      <h2>Reasoning & Execution Timeline</h2>
      <div id="gw-timeline"><p class="empty">Select a decision node to see its reasoning and execution timeline.</p></div>
    </div>
    <div class="gw-resizer" id="gw-resizer-inspector"></div>
    <div class="gw-col gw-inspector" id="gw-col-inspector">
      <h2>Decision Lens</h2>
      <div id="gw-inspector"><p class="empty">Select a decision node or timeline item to inspect.</p></div>
    </div>
  </div>
`;

const GW_JS = `
  var vscode = acquireVsCodeApi();
  var state = {
    decisionNodes: [], selectedNodeId: null, timelineArtifacts: [], timelineEvents: [], selectedSessionId: '',
    selectedNodeConversation: null, conversationPollTimer: null,
    referenceFiles: [],
  };

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
    state.selectedSessionId = ev.target.value;
    vscode.postMessage({ type: 'explorerSelectSession', sessionId: ev.target.value });
    document.getElementById('gw-tree').innerHTML = '<p class="empty">Loading…</p>';
    updateSessionControls(ev.target.value, state.__sessions || []);
  });

  function updateSessionControls(sessionId, sessions) {
    var pauseBtn = document.getElementById('gw-session-pause');
    var resumeBtn = document.getElementById('gw-session-resume');
    if (!sessionId) {
      pauseBtn.style.display = 'none';
      resumeBtn.style.display = 'none';
      return;
    }
    var session = (sessions || []).find(function(s) { return s.sessionId === sessionId; });
    var isPaused = session && session.status === 'Paused';
    pauseBtn.style.display = (!isPaused && session) ? '' : 'none';
    resumeBtn.style.display = isPaused ? '' : 'none';
  }

  document.getElementById('gw-session-pause').addEventListener('click', function() {
    var session = (state.__sessions || []).find(function(s) { return s.sessionId === state.selectedSessionId; });
    if (!session) { return; }
    vscode.postMessage({ type: 'explorerGoalPause', goalId: session.rootWorkUnitId });
  });

  document.getElementById('gw-session-resume').addEventListener('click', function() {
    var session = (state.__sessions || []).find(function(s) { return s.sessionId === state.selectedSessionId; });
    if (!session) { return; }
    vscode.postMessage({ type: 'explorerGoalResume', goalId: session.rootWorkUnitId });
  });

  document.getElementById('gw-run').addEventListener('click', function() {
    var goal = document.getElementById('gw-goal').value.trim();
    var strategy = document.getElementById('gw-strategy').value;
    if (!goal) { return; }
    var forkConfig = collectForkConfig();
    var reviewPolicyEl = document.querySelector('input[name=gw-review-policy]:checked');
    var targetEl = document.querySelector('input[name=gw-target]:checked');
    var btn = document.getElementById('gw-run');
    btn.disabled = true;
    btn.textContent = 'Running…';
    vscode.postMessage({
      type: 'explorerRun', strategy: strategy, goal: goal, forkConfig: forkConfig,
      reviewPolicy: reviewPolicyEl ? reviewPolicyEl.value : 'HumanRequired',
      bypassPromotionBranch: targetEl ? targetEl.value === 'direct' : false,
      referenceFiles: (state.referenceFiles || []).map(function(r) { return { repositoryId: r.repositoryId, path: r.path }; }),
    });
  });

  // ── Cross-repo file reference chips ─────────────────────────────────────
  function renderReferenceChips() {
    var el = document.getElementById('gw-reference-chips');
    if (!el) { return; }
    el.innerHTML = (state.referenceFiles || []).map(function(r, i) {
      return '<span class="gw-reference-chip" title="' + esc(r.repositoryLabel || r.repositoryId) + '">' +
        esc((r.repositoryLabel || r.repositoryId) + ' / ' + r.path) +
        '<span class="gw-reference-chip-remove" data-index="' + i + '">&times;</span></span>';
    }).join('');
    el.querySelectorAll('.gw-reference-chip-remove').forEach(function(btn) {
      btn.addEventListener('click', function() {
        state.referenceFiles.splice(parseInt(this.getAttribute('data-index'), 10), 1);
        renderReferenceChips();
      });
    });
  }

  document.getElementById('gw-add-reference-btn').addEventListener('click', function() {
    vscode.postMessage({ type: 'explorerAddReference' });
  });

  // ── Slice 22c — Strategy dropdown change reveals fork config panel ─────
  document.getElementById('gw-strategy').addEventListener('change', function() {
    var strategy = this.value;
    var panel = document.getElementById('gw-fork-config');
    if (strategy === 'Multi-Model Comparison' || strategy === 'Architecture Fork' || strategy === 'Library Comparison' || strategy === 'Product Strategy Fork') {
      panel.classList.add('visible');
      if (!state.forkConfig || !state.forkConfig.length) {
        state.forkConfig = buildDefaultForkConfig(strategy);
      }
      renderForkConfigPanel(state.forkConfig);
    } else {
      panel.classList.remove('visible');
    }
  });

  // ── Slice 22c — Inline fork config panel helpers ──────────────────────
  function buildDefaultForkConfig(strategy) {
    var orchProfiles = (state.agentProfiles || []).filter(function(p) { return p.domain === 'orchestration' && p.model; });
    if (orchProfiles.length < 2) {
      orchProfiles = (state.agentProfiles || []).filter(function(p) { return p.domain === 'orchestration'; });
    }
    var allProfiles = state.agentProfiles || [];
    var numForks = strategy === 'Multi-Model Comparison' ? 2 : 2;
    var entries = [];
    for (var i = 0; i < numForks; i++) {
      entries.push({ profileId: orchProfiles[i] ? orchProfiles[i].id : (allProfiles[i] ? allProfiles[i].id : ''), constraintHint: '' });
    }
    return entries;
  }

  function collectForkConfig() {
    var entries = [];
    var panel = document.getElementById('gw-fork-config');
    if (!panel || !panel.classList.contains('visible')) { return entries; }
    panel.querySelectorAll('.gw-fork-entry').forEach(function(entry) {
      var sel = entry.querySelector('select');
      var txt = entry.querySelector('input[type=text]');
      entries.push({ profileId: sel ? sel.value : '', constraintHint: txt ? txt.value : '' });
    });
    return entries;
  }

  function renderForkConfigPanel(entries) {
    state.forkConfig = entries || [];
    var el = document.getElementById('gw-fork-entries');
    if (!el) { return; }
    var profiles = state.agentProfiles || [];
    var html = '';
    (entries || []).forEach(function(entry, i) {
      html += '<div class="gw-fork-entry">';
      html += '<div class="gw-fork-entry-title">Fork ' + (i + 1) + '</div>';
      html += '<div class="gw-field"><label>Profile</label><select>' + profiles.map(function(p) {
        return '<option value="' + esc(p.id) + '"' + (p.id === entry.profileId ? ' selected' : '') + '>' + esc(p.label) + (p.model ? ' (' + esc(p.model) + ')' : '') + '</option>';
      }).join('') + '</select></div>';
      html += '<div class="gw-field"><label>Constraint (optional)</label><input type="text" value="' + esc(entry.constraintHint || '') + '" placeholder="e.g. use gRPC instead of REST"/></div>';
      html += '</div>';
    });
    var addBtn = '<div class="gw-field" style="align-self:flex-end"><button id="gw-add-fork-btn" class="ghost" style="padding:3px 10px;font-size:0.78em">+ Add Fork</button></div>';
    el.innerHTML = html + addBtn;
    var addBtnEl = document.getElementById('gw-add-fork-btn');
    if (addBtnEl) {
      addBtnEl.addEventListener('click', function() {
        if (!state.forkConfig) { state.forkConfig = []; }
        state.forkConfig.push({ profileId: (profiles[0] || {}).id || '', constraintHint: '' });
        renderForkConfigPanel(state.forkConfig);
      });
    }
  }

  // ── Exploration Settings ─────────────────────────────────────────────────

  document.getElementById('gw-settings-btn').addEventListener('click', function() {
    var panel = document.getElementById('gw-settings-panel');
    panel.style.display = panel.style.display === 'none' ? 'block' : 'none';
  });

  document.getElementById('gw-repo-path-browse').addEventListener('click', function() {
    vscode.postMessage({ type: 'explorerBrowseRepositoryPath' });
  });

  document.getElementById('gw-repo-path-clear').addEventListener('click', function() {
    vscode.postMessage({ type: 'explorerClearRepositoryPath' });
  });

  document.getElementById('gw-llm-profile-checkbox').addEventListener('change', function(ev) {
    vscode.postMessage({ type: 'explorerSetUseLlmProfileSelection', value: ev.target.checked });
  });

  document.getElementById('gw-require-build-checkbox').addEventListener('change', function(ev) {
    vscode.postMessage({ type: 'explorerSetRequireBuildBeforeProposal', value: ev.target.checked });
  });

  document.getElementById('gw-require-test-checkbox').addEventListener('change', function(ev) {
    vscode.postMessage({ type: 'explorerSetRequireTestBeforeProposal', value: ev.target.checked });
  });

  document.getElementById('gw-enforce-output-kind-checkbox').addEventListener('change', function(ev) {
    vscode.postMessage({ type: 'explorerSetEnforceExpectedOutputKind', value: ev.target.checked });
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

  document.getElementById('gw-block-conflicting-ops-checkbox').addEventListener('change', function(ev) {
    vscode.postMessage({ type: 'explorerSetBlockConflictingOps', value: ev.target.checked });
  });

  document.getElementById('gw-allow-auto-requeue-checkbox').addEventListener('change', function(ev) {
    vscode.postMessage({ type: 'explorerSetAllowAutoRequeue', value: ev.target.checked });
  });

  document.getElementById('gw-allow-agent-git-commits-checkbox').addEventListener('change', function(ev) {
    vscode.postMessage({ type: 'explorerSetAllowAgentGitCommits', value: ev.target.checked });
  });

  document.getElementById('gw-allow-agent-git-push-checkbox').addEventListener('change', function(ev) {
    vscode.postMessage({ type: 'explorerSetAllowAgentGitPush', value: ev.target.checked });
  });

  document.getElementById('gw-materializer-concurrency').addEventListener('change', function(ev) {
    var value = parseInt(ev.target.value, 10);
    if (!value || value < 1) { return; }
    vscode.postMessage({ type: 'explorerSetMaterializerConcurrency', value: value });
  });

  document.getElementById('gw-clarification-timeout-seconds').addEventListener('change', function(ev) {
    var value = parseInt(ev.target.value, 10);
    vscode.postMessage({ type: 'explorerSetClarificationTimeoutSeconds', value: isNaN(value) || value < 0 ? 0 : value });
  });

  document.getElementById('gw-clarification-timeout-behavior').addEventListener('change', function(ev) {
    vscode.postMessage({ type: 'explorerSetClarificationTimeoutBehavior', value: ev.target.value });
  });

  // Phase Y — Steer & Retry profile toggle
  function toggleSteerRetryProfile(workUnitId) {
    var checkbox = document.getElementById('dl-use-new-profile-' + workUnitId);
    var select = document.getElementById('dl-profile-select-' + workUnitId);
    if (checkbox && select) {
      select.style.display = checkbox.checked ? 'block' : 'none';
    }
  }

  // ── Resizable columns ─────────────────────────────────────────────────────

  (function setupColumnResizers() {
    var MIN_COL_WIDTH = 50;
    var MIN_TIMELINE_WIDTH = 50;
    var treeEl = document.getElementById('gw-col-tree');
    var inspectorEl = document.getElementById('gw-col-inspector');
    var bodyEl = document.querySelector('.gw-body');

    var saved = null;
    try { saved = JSON.parse(localStorage.getItem('nm-gw-column-widths') || 'null'); } catch (e) { saved = null; }
    if (saved && saved.tree) { treeEl.style.width = saved.tree + 'px'; }
    if (saved && saved.inspector) { inspectorEl.style.width = saved.inspector + 'px'; }

    function persistWidths() {
      try {
        localStorage.setItem('nm-gw-column-widths', JSON.stringify({
          tree: treeEl.getBoundingClientRect().width,
          inspector: inspectorEl.getBoundingClientRect().width,
        }));
      } catch (e) { /* localStorage unavailable — resizing still works, just won't persist */ }
    }

    function bindResizer(resizerEl, targetEl, otherEl, direction) {
      resizerEl.addEventListener('mousedown', function(downEv) {
        downEv.preventDefault();
        var startX = downEv.clientX;
        var startWidth = targetEl.getBoundingClientRect().width;
        // Recomputed per-drag (not just once) since the other fixed column may have been
        // resized since this resizer was bound, and the timeline needs to keep its own floor.
        var maxWidth = bodyEl.getBoundingClientRect().width - otherEl.getBoundingClientRect().width - MIN_TIMELINE_WIDTH - 10;
        resizerEl.classList.add('gw-resizing');
        document.body.style.cursor = 'col-resize';

        function onMove(moveEv) {
          var next = startWidth + (moveEv.clientX - startX) * direction;
          next = Math.max(MIN_COL_WIDTH, Math.min(next, maxWidth));
          targetEl.style.width = next + 'px';
        }
        function onUp() {
          document.removeEventListener('mousemove', onMove);
          document.removeEventListener('mouseup', onUp);
          resizerEl.classList.remove('gw-resizing');
          document.body.style.cursor = '';
          persistWidths();
        }
        document.addEventListener('mousemove', onMove);
        document.addEventListener('mouseup', onUp);
      });
    }

    // Dragging the tree|timeline resizer right grows the tree column; dragging the
    // timeline|inspector resizer right shrinks the inspector column (it's anchored to the right).
    bindResizer(document.getElementById('gw-resizer-tree'), treeEl, inspectorEl, 1);
    bindResizer(document.getElementById('gw-resizer-inspector'), inspectorEl, treeEl, -1);
  })();

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
      bindDecisionInspectorTabs();
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
      html += '</div>';
      // Slice 22c — Experiment parent badges
      var children = (byParent[wu.workUnitId] || []);
      if (children.length >= 2) {
        var childForkTypes = children.map(function(c) { return c.forkType || ''; }).filter(function(t) { return t && t.toLowerCase() !== 'unknown'; });
        // Show "Compare Results" only for experiment forks (children with named fork types),
        // not for normal Decompose fan-outs where children are dividing work, not competing.
        if (childForkTypes.length >= 2) {
          html += '<div class="dn-exp-badges">';
          html += '<span class="badge forks">' + childForkTypes.length + ' forks</span>';
          html += '<span class="compare-link" data-exp-parent="' + esc(wu.workUnitId) + '">Compare Results</span>';
          html += '</div>';
        }
      }
      // Slice 25c — Counterfactual badge + comparison link
      var cfOriginalId = wu.metadata && wu.metadata.counterfactualFromWorkUnitId;
      if (cfOriginalId) {
        html += '<div class="dn-exp-badges">';
        html += '<span class="badge cf">Counterfactual</span>';
        html += '<span class="compare-link cf-compare-link" data-cf-original="' + esc(cfOriginalId) + '">Compare with Original</span>';
        html += '</div>';
      }
      html += '</div>';
      (byParent[wu.workUnitId] || []).forEach(function(child) { renderNode(child, depth + 1); });
    }
    roots.forEach(function(r) { renderNode(r, 0); });
    el.innerHTML = html;
    el.querySelectorAll('.dn-node').forEach(function(node) {
      node.addEventListener('click', function() {
        var id = node.getAttribute('data-wu');
        stopConversationPoll();
        state.selectedNodeId = id;
        state.selectedNodeConversation = null;
        renderDecisionTree(state.decisionNodes);
        document.getElementById('gw-timeline').innerHTML = '<p class="empty">Loading…</p>';
        document.getElementById('gw-inspector').innerHTML = renderDecisionInspector(state.decisionNodes.find(function(w) { return w.workUnitId === id; }));
        bindDecisionInspectorTabs();
        vscode.postMessage({ type: 'explorerSelectWorkUnit', workUnitId: id });
      });
    });
    // Slice 22c — Compare Results link handler
    el.querySelectorAll('.compare-link').forEach(function(link) {
      link.addEventListener('click', function(ev) {
        ev.stopPropagation();
        var parentId = link.getAttribute('data-exp-parent');
        if (!parentId) { return; }
        var children = byParent[parentId] || [];
        var proposalIds = [];
        children.forEach(function(c) {
          if (c.latestProposalId) { proposalIds.push(c.latestProposalId); }
        });
        // Fetch each child's timeline to find proposals
        state.__compareChildren = children;
        state.__compareParentId = parentId;
        document.getElementById('gw-inspector').innerHTML = renderCompareResults(children, parentId);
        bindCompareResultsButtons();
      });
    });
    // Slice 25c — Compare with Original link handler
    el.querySelectorAll('.cf-compare-link').forEach(function(link) {
      link.addEventListener('click', function(ev) {
        ev.stopPropagation();
        var originalId = link.getAttribute('data-cf-original');
        if (!originalId) { return; }
        document.getElementById('gw-inspector').innerHTML = '<p class="empty">Loading comparison…</p>';
        vscode.postMessage({ type: 'explorerLoadCounterfactualComparison', workUnitId: originalId });
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
    html += '<button class="ghost" data-wu-action="forkKnownGood" data-wu="' + esc(workUnitId) + '">Fork from Known Good</button>';
    html += '</div>';
    el.innerHTML = html;
    bindDecisionInspectorTabs();
  }

  function bindWorkUnitActionButtons() {
    document.querySelectorAll('[data-wu-action]').forEach(function(btn) {
      btn.addEventListener('click', function() {
        var action = btn.getAttribute('data-wu-action');
        if (action === 'steerPause' || action === 'steerForkFromNode' || action === 'steerDeadLetterRetry') {
          vscode.postMessage({
            type: 'explorerSteeringAction',
            action: action,
            workUnitId: btn.getAttribute('data-wu'),
            agentId: btn.getAttribute('data-agent') || '',
          });
        } else if (action === 'steerDeadLetterRetrySend') {
          var wuId = btn.getAttribute('data-wu');
          var contextEl = document.getElementById('dl-steer-context-' + wuId);
          var steeringContext = contextEl ? contextEl.value : '';
          var useNewProfile = document.getElementById('dl-use-new-profile-' + wuId);
          var profileSelect = document.getElementById('dl-profile-select-' + wuId);
          var overrideModel = '';
          var overrideBaseUrl = '';
          var overrideApiKey = '';
          var overrideProvider = '';
          var overrideProfileId = '';
          if (useNewProfile && useNewProfile.checked && profileSelect && profileSelect.value) {
            // Look up the selected profile's model/detail from agentProfiles
            var selId = profileSelect.value;
            var prof = (state.agentProfiles || []).find(function(p) { return p.id === selId; });
            if (prof) {
              overrideModel = prof.model || '';
              overrideBaseUrl = prof.baseUrl || '';
              overrideProvider = prof.provider || '';
              overrideProfileId = prof.id || '';
            }
          }
          vscode.postMessage({
            type: 'explorerSteeringAction',
            action: 'steerDeadLetterRetrySend',
            workUnitId: wuId,
            steeringContext: steeringContext.trim() || '',
            overrideModel: overrideModel || undefined,
            overrideBaseUrl: overrideBaseUrl || undefined,
            overrideApiKey: overrideApiKey || undefined,
            overrideProvider: overrideProvider || undefined,
            overrideProfileId: overrideProfileId || undefined,
          });
        } else {
          vscode.postMessage({
            type: 'explorerWorkUnitAction',
            action: action,
            workUnitId: btn.getAttribute('data-wu'),
          });
        }
      });
    });
    // Inline onchange attributes are blocked by the webview's CSP (script-src is nonce-only,
    // no unsafe-inline), so the profile checkbox is wired here instead of via onchange="".
    document.querySelectorAll('[id^="dl-use-new-profile-"]').forEach(function(checkbox) {
      checkbox.addEventListener('change', function() {
        var workUnitId = checkbox.id.slice('dl-use-new-profile-'.length);
        toggleSteerRetryProfile(workUnitId);
      });
    });
  }

  function renderDecisionInspector(wu) {
    if (!wu) { return '<p class="empty">Select a decision node or timeline item to inspect.</p>'; }

    // ── Slice 24b — Tab bar ────────────────────────────────────────────────
    var html = '<div class="gw-tab-bar">';
    html += '<button class="gw-tab-btn active" data-gw-tab="metadata">Metadata</button>';
    html += '<button class="gw-tab-btn" data-gw-tab="context">Context</button>';
    html += '<button class="gw-tab-btn" data-gw-tab="conversation">Conversation</button>';
    html += '</div>';

    // ── Metadata panel ────────────────────────────────────────────────────
    html += '<div class="gw-tab-panel active" id="gw-panel-metadata">';
    html += '<div class="meta-grid">';
    html += '<span class="meta-label">Decision Status</span>' + badge(wu.status);
    html += '<span class="meta-label">Phase</span><span>' + stageBadge(wu.currentStage) + '</span>';
    html += '<span class="meta-label">Initiator</span><span class="mono">' + esc(wu.owner) + '</span>';
    html += '<span class="meta-label">Executor</span><span class="mono">' + esc(wu.assignedAgent || '—') + '</span>';
    html += '<span class="meta-label">Hypothesis Fork</span><span class="mono">' + esc(wu.branchId) + '</span>';
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
    if (state.reasoningGraph) {
      html += renderReasoningChain(state.reasoningGraph);
    }
    var statusLower = (wu.status || '').toLowerCase();
    var isRunning = statusLower === 'running' || statusLower === 'executing' || statusLower === 'active' || statusLower === 'queued' || statusLower === 'retrying';
    html += '<div class="inspector-actions">';
    html += '<button class="ghost" data-wu-action="forkHypothesis" data-wu="' + esc(wu.workUnitId) + '">Fork Hypothesis</button>';
    html += '<button class="ghost" data-wu-action="reexplore" data-wu="' + esc(wu.workUnitId) + '">Re-explore</button>';
    html += '<button class="ghost" data-wu-action="forkLatest" data-wu="' + esc(wu.workUnitId) + '">Fork from latest candidate</button>';
    html += '<button class="ghost" data-wu-action="forkKnownGood" data-wu="' + esc(wu.workUnitId) + '">Fork from Known Good</button>';
    // Slice 25c — Counterfactual: "Run with different model" for completed work units
    var isCompleted = statusLower === 'completed' || statusLower === 'merged';
    if (isCompleted) {
      html += '<button class="ghost" data-wu-action="counterfactual" data-wu="' + esc(wu.workUnitId) + '">↺ Run with different model</button>';
    }
    if (isRunning) {
      html += '<button class="ghost" data-wu-action="steerPause" data-wu="' + esc(wu.workUnitId) + '" data-agent="' + esc(wu.assignedAgent || '') + '" style="color:var(--nm-warn);border-color:var(--nm-warn)">⏸ Pause & Redirect</button>';
      html += '<button class="ghost" data-wu-action="steerForkFromNode" data-wu="' + esc(wu.workUnitId) + '">↳ Fork from here</button>';
    }
    if (statusLower === 'deadlettered') {
      html += '<div class="steer-retry-section" style="margin-top:8px;padding:8px;border:1px solid var(--nm-error);border-radius:4px">';
      html += '<div style="font-size:0.85em;opacity:0.8;margin-bottom:6px">🛠 Steer & Retry — correct the agent and optionally swap the model:</div>';
      html += '<textarea id="dl-steer-context-' + esc(wu.workUnitId) + '" rows="2" placeholder="e.g. the file lives at repo root, not under src/ — start the search there" style="width:100%;margin-bottom:6px"></textarea>';
      html += '<label style="display:flex;align-items:center;gap:4px;font-size:0.82em;margin-bottom:6px">';
      html += '<input type="checkbox" id="dl-use-new-profile-' + esc(wu.workUnitId) + '"/> Use new agent profile';
      html += '</label>';
      html += '<select id="dl-profile-select-' + esc(wu.workUnitId) + '" style="display:none;width:100%;margin-bottom:6px">';
      (state.agentProfiles || []).forEach(function(p) {
        html += '<option value="' + esc(p.id) + '">' + esc(p.label) + (p.model ? ' (' + esc(p.model) + ')' : '') + '</option>';
      });
      html += '</select>';
      html += '<button class="ghost" data-wu-action="steerDeadLetterRetrySend" data-wu="' + esc(wu.workUnitId) + '" style="color:var(--nm-error);border-color:var(--nm-error)">🛠 Retry</button>';
      html += '</div>';
    }
    html += '</div>';
    html += '</div>'; // end Metadata panel

    // ── Context panel ─────────────────────────────────────────────────────
    html += '<div class="gw-tab-panel" id="gw-panel-context">';
    if (state.selectedNodeContext) {
      html += renderContextTab(state.selectedNodeContext);
    } else {
      html += '<p class="empty">Context loading…</p>';
    }
    html += '</div>'; // end Context panel

    // ── Conversation panel (Phase 11) ──────────────────────────────────────
    html += '<div class="gw-tab-panel" id="gw-panel-conversation">';
    if (state.selectedNodeConversation) {
      html += renderConversationTab(state.selectedNodeConversation);
    } else {
      html += '<p class="empty">Conversation loading…</p>';
    }
    html += '</div>'; // end Conversation panel

    return html;
  }

  // Phase 11 — same running-status check used for the steering buttons above; reused so the
  // Conversation tab's poll knows when to stop without recomputing the status string twice.
  function isWuRunning(wu) {
    var statusLower = (wu.status || '').toLowerCase();
    return statusLower === 'running' || statusLower === 'executing' || statusLower === 'active' ||
      statusLower === 'queued' || statusLower === 'retrying';
  }

  // Phase 11 — one row per logged cycle, newest first within each agent so the most recent
  // reasoning is visible without scrolling; tool calls/results render as collapsible blocks since
  // they can be large (workspace.read of a big file, etc.).
  function renderConversationTab(entries) {
    if (!entries || !entries.length) {
      return '<p class="empty">No conversation recorded yet for this decision node.</p>';
    }
    var totalIn = 0, totalOut = 0, haveTokens = false, anyEstimated = false;
    var modelsByRole = {};
    entries.forEach(function(e) {
      if (e.inputTokens != null) { totalIn += e.inputTokens; haveTokens = true; }
      if (e.outputTokens != null) { totalOut += e.outputTokens; haveTokens = true; }
      if (e.tokensEstimated && (e.inputTokens != null || e.outputTokens != null)) { anyEstimated = true; }
      if (e.model) {
        var key = e.agentRole + '|' + e.model + '|' + (e.provider || '');
        modelsByRole[key] = { role: e.agentRole, model: e.model, provider: e.provider };
      }
    });
    var html = '';
    if (haveTokens) {
      var totalTilde = anyEstimated ? '~' : '';
      var totalTitle = anyEstimated
        ? ' title="Includes one or more estimated counts (vscode-lm models don’t report real token usage; estimated via VS Code’s tokenizer, not the provider’s exact count)"'
        : '';
      html += '<div class="conv-token-total"' + totalTitle + ' style="font-size:0.8em;opacity:0.7;margin-bottom:4px">'
        + 'Tokens this run — ' + totalTilde + '↑' + totalIn.toLocaleString() + ' in / ' + totalTilde + '↓' + totalOut.toLocaleString() + ' out</div>';
    }
    var modelKeys = Object.keys(modelsByRole);
    if (modelKeys.length > 0) {
      html += '<div class="conv-model-summary" style="font-size:0.8em;opacity:0.7;margin-bottom:8px">Models this run — '
        + modelKeys.map(function(k) {
            var m = modelsByRole[k];
            return esc(m.role) + ': ' + esc(m.model) + (m.provider ? ' (' + esc(m.provider) + ')' : '');
          }).join(', ')
        + '</div>';
    }
    html += '<div id="conv-list">';
    entries.slice().reverse().forEach(function(e) {
      html += '<div class="conv-entry">';
      html += '<div class="conv-entry-head">';
      html += '<span class="badge">' + esc(e.agentRole) + '</span>';
      html += '<span class="mono" style="font-size:0.78em;opacity:0.6">' + esc(e.agentId) + '</span>';
      if (e.model) {
        html += '<span class="mono" style="font-size:0.78em;opacity:0.7">' + esc(e.model)
          + (e.provider ? ' (' + esc(e.provider) + ')' : '') + '</span>';
      }
      html += '<span style="font-size:0.78em;opacity:0.55">cycle ' + e.cycleNumber + '</span>';
      html += '<span style="font-size:0.72em;opacity:0.45">' + fmtTime(e.occurredAt) + '</span>';
      if (e.inputTokens != null || e.outputTokens != null) {
        var tilde = e.tokensEstimated ? '~' : '';
        var tokenTitle = e.tokensEstimated
          ? ' title="Estimated via VS Code’s tokenizer — vscode-lm models don’t report real token usage, so this is not the provider’s exact count"'
          : '';
        html += '<span style="font-size:0.72em;opacity:0.5"' + tokenTitle + '>' + tilde + '↑' + (e.inputTokens != null ? e.inputTokens.toLocaleString() : '—')
          + ' ' + tilde + '↓' + (e.outputTokens != null ? e.outputTokens.toLocaleString() : '—') + '</span>';
      }
      html += '</div>';
      if (e.assistantText) {
        html += '<div class="conv-text">' + esc(e.assistantText) + '</div>';
      }
      (e.toolCalls || []).forEach(function(call) {
        var result = (e.toolResults || []).find(function(r) { return r.toolUseId === call.toolUseId; });
        html += '<details class="conv-tool" data-tool-use-id="' + esc(call.toolUseId || '') + '">';
        html += '<summary>🔧 ' + esc(call.name) + '</summary>';
        html += '<div class="conv-tool-label">Input</div>';
        html += '<pre class="conv-pre">' + esc(call.inputJson) + '</pre>';
        if (result) {
          html += '<div class="conv-tool-label">Result' + (result.truncated ? ' (truncated)' : '') + '</div>';
          html += '<pre class="conv-pre">' + esc(result.result) + '</pre>';
        }
        html += '</details>';
      });
      html += '</div>';
    });
    html += '</div>';
    return html;
  }

  // ── Slice 24b — Context tab ────────────────────────────────────────────

  function stopConversationPoll() {
    if (state.conversationPollTimer) { clearInterval(state.conversationPollTimer); state.conversationPollTimer = null; }
  }

  // Phase 11 — only polls while the Conversation tab is the one visible and its work unit is
  // still running; reuses the 2s cadence already used everywhere else in this panel (tree/session
  // polling) rather than introducing a faster or push-based mechanism.
  function startConversationPoll(workUnitId) {
    stopConversationPoll();
    state.conversationPollTimer = setInterval(function() {
      var wu = state.decisionNodes.find(function(w) { return w.workUnitId === workUnitId; });
      if (!wu || !isWuRunning(wu) || state.selectedNodeId !== workUnitId) { stopConversationPoll(); return; }
      vscode.postMessage({ type: 'explorerSelectConversationTab', workUnitId: workUnitId });
    }, 2000);
  }

  function bindTabBarClick() {
    document.querySelectorAll('.gw-tab-btn').forEach(function(btn) {
      btn.addEventListener('click', function() {
        var tab = btn.getAttribute('data-gw-tab');
        document.querySelectorAll('.gw-tab-btn').forEach(function(b) { b.classList.remove('active'); });
        document.querySelectorAll('.gw-tab-panel').forEach(function(p) { p.classList.remove('active'); });
        btn.classList.add('active');
        var panel = document.getElementById('gw-panel-' + tab);
        if (panel) { panel.classList.add('active'); }

        // If Context tab is selected and we have no data yet, request it
        if (tab === 'context' && !state.selectedNodeContext && state.selectedNodeId) {
          document.getElementById('gw-panel-context').innerHTML = '<p class="empty">Loading…</p>';
          vscode.postMessage({ type: 'explorerSelectContextTab', workUnitId: state.selectedNodeId });
        }

        // Phase 11 — Conversation tab: fetch on first view, then poll while the work unit runs.
        if (tab === 'conversation' && state.selectedNodeId) {
          if (!state.selectedNodeConversation) {
            document.getElementById('gw-panel-conversation').innerHTML = '<p class="empty">Loading…</p>';
          }
          vscode.postMessage({ type: 'explorerSelectConversationTab', workUnitId: state.selectedNodeId });
          var wu = state.decisionNodes.find(function(w) { return w.workUnitId === state.selectedNodeId; });
          if (wu && isWuRunning(wu)) { startConversationPoll(state.selectedNodeId); }
        } else {
          stopConversationPoll();
        }
      });
    });
  }

  function renderContextTab(context) {
    if (!context) { return '<p class="empty">No context data available for this decision node.</p>'; }
    var html = '';

    // Goal
    html += '<div class="ctx-section">';
    html += '<h3>Goal</h3>';
    html += '<div class="ctx-item">' + esc(context.goal) + '</div>';
    html += '</div>';

    // Plan
    if (context.plan && context.plan.length) {
      html += '<div class="ctx-section">';
      html += '<h3>Plan</h3>';
      context.plan.forEach(function(slice) {
        html += '<div class="ctx-plan-entry">';
        html += '<div class="ctx-plan-slice mono">' + esc(slice.sliceId) + '</div>';
        html += '<div class="ctx-plan-goal">' + esc(slice.goal) + '</div>';
        if (slice.fileScope && slice.fileScope.length) {
          html += '<div class="ctx-item" style="opacity:0.5;font-size:0.76em">📁 ' + esc(slice.fileScope.join(', ')) + '</div>';
        }
        if (slice.steps && slice.steps.length) {
          html += '<ol class="ctx-plan-steps">';
          slice.steps.forEach(function(step) { html += '<li>' + esc(step) + '</li>'; });
          html += '</ol>';
        }
        html += '</div>';
      });
      html += '</div>';
    }

    // Assumptions
    if (context.assumptions && context.assumptions.length) {
      html += '<div class="ctx-section">';
      html += '<h3>Assumptions</h3>';
      context.assumptions.forEach(function(a) {
        html += '<div class="ctx-item">• ' + esc(a) + '</div>';
      });
      html += '</div>';
    }

    // Constraints
    if (context.constraints && context.constraints.length) {
      html += '<div class="ctx-section">';
      html += '<h3>Constraints</h3>';
      context.constraints.forEach(function(c) {
        html += '<div class="ctx-item">🔒 ' + esc(c) + '</div>';
      });
      html += '</div>';
    }

    // Evidence
    if (context.evidence && context.evidence.length) {
      html += '<div class="ctx-section">';
      html += '<h3>Evidence</h3>';
      context.evidence.forEach(function(ev) {
        var icon = ev.success ? '✅' : '❌';
        var cls = ev.success ? 'success' : 'fail';
        html += '<div class="ctx-evidence ' + cls + '">' + icon + ' ' + esc(ev.summary) + '</div>';
      });
      html += '</div>';
    }

    // Execution results
    if (context.execution) {
      html += '<div class="ctx-section">';
      html += '<h3>Execution Results</h3>';
      html += '<div class="ctx-item">' + (context.execution.allSucceeded ? '✅ All passed' : '❌ Some failed') + '</div>';
      if (context.execution.buildSystems && context.execution.buildSystems.length) {
        html += '<div class="ctx-item mono">Build systems: ' + esc(context.execution.buildSystems.join(', ')) + '</div>';
      }
      if (context.execution.testSummary) {
        html += '<div class="ctx-item mono">' + esc(context.execution.testSummary) + '</div>';
      }
      if (context.execution.executedAt) {
        html += '<div class="ctx-item" style="font-size:0.72em;opacity:0.5">' + esc(fmtTime(context.execution.executedAt)) + '</div>';
      }
      html += '</div>';
    }

    // Allowed Tools
    if (context.allowedTools && context.allowedTools.length) {
      html += '<div class="ctx-section">';
      html += '<h3>Allowed Tools</h3>';
      html += '<div class="ctx-item mono">' + esc(context.allowedTools.join(', ')) + '</div>';
      html += '</div>';
    }

    // Model info
    if (context.agentModel) {
      html += '<div class="ctx-section">';
      html += '<h3>Model</h3>';
      html += '<div class="ctx-item mono">' + esc(context.agentModel) + (context.agentProvider ? ' @ ' + esc(context.agentProvider) : '') + '</div>';
      html += '</div>';
    }

    // Steered-from indicator
    if (context.steeredFromDecisionId) {
      html += '<div class="ctx-section">';
      html += '<h3>Steering</h3>';
      html += '<div class="ctx-item mono">↳ Steered from decision ' + esc(context.steeredFromDecisionId) + '</div>';
      html += '</div>';
    }

    // Copy as Markdown button
    html += '<div class="inspector-actions">';
    html += '<button class="ghost ctx-copy-btn" id="ctx-copy-markdown">📋 Copy as Markdown</button>';
    html += '</div>';

    return html;
  }

  function bindContextCopyButton() {
    var btn = document.getElementById('ctx-copy-markdown');
    if (!btn) { return; }
    btn.addEventListener('click', function() {
      var ctx = state.selectedNodeContext;
      if (!ctx) { return; }
      var md = '## Decision Context\\n\\n';
      md += '**Goal:** ' + ctx.goal + '\\n\\n';
      if (ctx.plan && ctx.plan.length) {
        md += '### Plan\\n';
        ctx.plan.forEach(function(s) {
          md += '- **' + s.sliceId + ':** ' + s.goal + '\\n';
          if (s.steps && s.steps.length) { s.steps.forEach(function(st) { md += '  1. ' + st + '\\n'; }); }
        });
        md += '\\n';
      }
      if (ctx.assumptions && ctx.assumptions.length) {
        md += '### Assumptions\\n';
        ctx.assumptions.forEach(function(a) { md += '- ' + a + '\\n'; });
        md += '\\n';
      }
      if (ctx.constraints && ctx.constraints.length) {
        md += '### Constraints\\n';
        ctx.constraints.forEach(function(c) { md += '- ' + c + '\\n'; });
        md += '\\n';
      }
      if (ctx.evidence && ctx.evidence.length) {
        md += '### Evidence\\n';
        ctx.evidence.forEach(function(ev) { md += '- ' + (ev.success ? '✅' : '❌') + ' ' + ev.summary + '\\n'; });
        md += '\\n';
      }
      if (ctx.allowedTools && ctx.allowedTools.length) {
        md += '### Allowed Tools\\n';
        md += ctx.allowedTools.join(', ') + '\\n\\n';
      }
      if (ctx.agentModel) {
        md += '**Model:** ' + ctx.agentModel + (ctx.agentProvider ? ' @ ' + ctx.agentProvider : '') + '\\n\\n';
      }
      if (ctx.steeredFromDecisionId) {
        md += '**Steered from:** ' + ctx.steeredFromDecisionId + '\\n\\n';
      }
      navigator.clipboard.writeText(md).then(function() {
        var btn = document.getElementById('ctx-copy-markdown');
        if (btn) { btn.textContent = '✓ Copied!'; setTimeout(function() { if (btn) { btn.textContent = '📋 Copy as Markdown'; } }, 1500); }
      }).catch(function() {
        var btn = document.getElementById('ctx-copy-markdown');
        if (btn) { btn.textContent = '⚠ Copy failed'; }
      });
    });
  }

  function bindDecisionInspectorTabs() {
    bindTabBarClick();
    bindWorkUnitActionButtons();
    bindContextCopyButton();
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
    html += '<button data-p-action="openReview">Open in Review &rarr;</button>';
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

  // ── Slice 22c — Compare Results side-by-side view ──────────────────────

  function renderCompareResults(children, parentId) {
    var profiles = state.agentProfiles || [];
    // Comparison engine — deterministic evidence/score per sibling, fetched async via the
    // HypothesisComparison projection. May not have arrived yet on first render; that's fine,
    // the raw side-by-side view above still works without it.
    var comparison = (state.__comparisonParentId === parentId) ? state.__comparisonData : null;
    var siblingsByWu = {};
    if (comparison && comparison.siblings) {
      comparison.siblings.forEach(function(s) { siblingsByWu[s.workUnitId] = s; });
    } else {
      vscode.postMessage({ type: 'explorerLoadComparison', parentId: parentId });
    }
    var html = '<div class="cmp-results"><div class="cmp-header">';
    html += '<h2>Fork Comparison</h2>';
    html += '<span class="mono" style="font-size:0.72em">' + children.length + ' forks</span>';
    html += '</div>';
    if (comparison && comparison.recommendedWorkUnitId) {
      html += '<p style="font-size:0.78em;opacity:0.75">Evidence-based recommendation: <strong>' +
        esc(comparison.recommendedWorkUnitId) + '</strong> (deterministic score, not a decision — pick the winner yourself below)</p>';
    }
    html += '<div class="cmp-fork-cards">';
    children.forEach(function(child, i) {
      var profile = profiles.find(function(p) { return p.id === child.owner; }) || {};
      var modelLabel = profile.model || profile.label || child.owner || 'Fork ' + (i + 1);
      var won = state.__compareWinner === child.workUnitId;
      var lost = state.__compareLosers && state.__compareLosers.indexOf(child.workUnitId) >= 0;
      var cls = won ? ' selected' : (lost ? ' rejected' : '');
      var sibling = siblingsByWu[child.workUnitId];
      html += '<div class="cmp-fork-card' + cls + '" data-cmp-wu="' + esc(child.workUnitId) + '">';
      html += '<div class="cmp-fk-model">🔀 ' + esc(modelLabel) + '</div>';
      html += '<div class="cmp-fk-goal">' + esc((child.goal || '').substring(0, 120)) + '</div>';
      html += '<div class="cmp-fk-meta">';
      html += badge(child.status);
      if (child.forkType && child.forkType.toLowerCase() !== 'unknown') {
        html += '<span class="badge fork-type">' + esc(child.forkType) + '</span>';
      }
      html += '<span class="mono">' + (child.proposalCount || 0) + ' proposals</span>';
      if (sibling) {
        html += '<span class="mono" title="' + esc((sibling.evidenceSummaries || []).join(' | ')) + '">score: ' +
          sibling.score.toFixed(1) + ' (' + sibling.evidenceCount + ' evidence)</span>';
      }
      if (won) { html += '<span class="badge completed">★ Winner</span>'; }
      html += '</div></div>';
    });
    html += '</div>';
    if (!state.__compareWinner) {
      html += '<div class="cmp-pick-bar">';
      html += '<span style="font-size:0.78em;opacity:0.6">Select a fork then click Pick Winner:</span>';
      html += '<button class="pick-winner" id="gw-pick-winner" disabled>Pick Winner</button>';
      html += '</div>';
    } else {
      html += '<div class="cmp-pick-bar">';
      html += '<span style="font-size:0.78em;color:var(--nm-success)">✔ Winner selected: ' + esc(state.__compareWinnerLabel || state.__compareWinner) + '</span>';
      html += '<button class="ghost" id="gw-reset-compare" style="font-size:0.74em">Reset</button>';
      html += '</div>';
    }
    html += '<div class="inspector-actions" style="margin-top:8px">';
    html += '<button class="ghost" id="gw-compare-open-latest" style="font-size:0.8em">📋 View proposals</button>';
    html += '</div>';
    html += '</div>';
    return html;
  }

  // ── Slice 25c — Counterfactual: original vs. counterfactual comparison ─
  function renderCounterfactualComparison(comparison) {
    if (!comparison) { return '<p class="empty">No comparison data available for this counterfactual.</p>'; }

    function renderSide(label, model, provider, proposals) {
      var html = '<div class="cmp-fork-card">';
      html += '<div class="cmp-fk-model">🔀 ' + esc(label) + ': ' + esc(model || provider || 'unknown') + '</div>';
      (proposals || []).forEach(function(p) {
        html += '<div class="cmp-fk-goal">' + esc((p.goal || '').substring(0, 120)) + '</div>';
        html += '<div class="cmp-fk-meta">';
        html += badge(p.status);
        if (typeof p.confidence === 'number') {
          html += '<span class="mono">confidence: ' + Math.round(p.confidence * 100) + '%</span>';
        }
        html += '<span class="mono">' + (p.filesTouched || []).length + ' files</span>';
        html += '</div>';
        if (p.diffSummary) {
          html += '<div class="cmp-fk-goal" style="opacity:0.6">' + esc(p.diffSummary.substring(0, 200)) + '</div>';
        }
      });
      html += '</div>';
      return html;
    }

    var htmlOut = '<div class="cmp-results"><div class="cmp-header">';
    htmlOut += '<h2>Counterfactual Comparison</h2>';
    htmlOut += '</div>';
    htmlOut += '<div class="cmp-fork-cards">';
    htmlOut += renderSide('Original', comparison.originalModel, comparison.originalProvider, comparison.originals);
    htmlOut += renderSide('Counterfactual', comparison.counterfactualModel, comparison.counterfactualProvider, comparison.counterfactuals);
    htmlOut += '</div>';
    if (comparison.whichWasBetter) {
      htmlOut += '<div class="cmp-pick-bar"><span style="font-size:0.78em;color:var(--nm-success)">Which was better: ' + esc(comparison.whichWasBetter) + '</span></div>';
    }
    htmlOut += '</div>';
    return htmlOut;
  }

  function bindCompareResultsButtons() {
    var children = state.__compareChildren || [];
    var pickBtn = document.getElementById('gw-pick-winner');
    var openBtn = document.getElementById('gw-compare-open-latest');
    var resetBtn = document.getElementById('gw-reset-compare');

    // Card click to select
    document.querySelectorAll('.cmp-fork-card').forEach(function(card) {
      card.addEventListener('click', function() {
        var wuId = card.getAttribute('data-cmp-wu');
        if (!wuId) { return; }
        state.__comparePendingPick = wuId;
        document.querySelectorAll('.cmp-fork-card').forEach(function(c) { c.classList.remove('selected'); });
        card.classList.add('selected');
        if (pickBtn) {
          pickBtn.disabled = false;
          pickBtn.textContent = 'Pick Winner: ' + esc(state.__comparePendingPickLabel || wuId);
        }
      });
    });

    if (pickBtn) {
      pickBtn.addEventListener('click', function() {
        var winnerId = state.__comparePendingPick;
        if (!winnerId) { return; }
        var winnerWU = children.find(function(c) { return c.workUnitId === winnerId; });
        state.__compareWinner = winnerId;
        state.__compareWinnerLabel = winnerWU ? (winnerWU.owner || winnerWU.goal || winnerId) : winnerId;
        state.__compareLosers = children.filter(function(c) { return c.workUnitId !== winnerId; }).map(function(c) { return c.workUnitId; });
        // Send pick winner action to extension host
        vscode.postMessage({
          type: 'explorerPickWinner',
          winnerId: winnerId,
          parentId: state.__compareParentId || '',
        });
        // Re-render
        document.getElementById('gw-inspector').innerHTML = renderCompareResults(children, state.__compareParentId);
        bindCompareResultsButtons();
      });
    }

    if (resetBtn) {
      resetBtn.addEventListener('click', function() {
        state.__compareWinner = null;
        state.__compareWinnerLabel = null;
        state.__compareLosers = null;
        state.__comparePendingPick = null;
        document.getElementById('gw-inspector').innerHTML = renderCompareResults(children, state.__compareParentId);
        bindCompareResultsButtons();
      });
    }

    if (openBtn) {
      openBtn.addEventListener('click', function() {
        var firstChild = children[0];
        if (firstChild) {
          state.selectedNodeId = firstChild.workUnitId;
          renderDecisionTree(state.decisionNodes);
          vscode.postMessage({ type: 'explorerSelectWorkUnit', workUnitId: firstChild.workUnitId });
        }
      });
    }
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
    if (msg.type === 'explorerReferenceAdded') {
      state.referenceFiles = state.referenceFiles || [];
      state.referenceFiles.push({ repositoryId: msg.repositoryId, repositoryLabel: msg.repositoryLabel, path: msg.path });
      renderReferenceChips();
      return;
    }
    if (msg.type === 'strategies') {
      // Slice 22c — store profiles for fork config
      if (msg.profiles) { state.agentProfiles = msg.profiles || []; }
      var sel = document.getElementById('gw-strategy');
      sel.innerHTML = (msg.strategies || []).map(function(t) {
        var disabled = t.disabled ? ' disabled' : '';
        var title = t.tooltip ? ' title="' + esc(t.tooltip) + '"' : '';
        return '<option value="' + esc(t.name) + '"' + disabled + title + '>' + esc(t.name) + '</option>';
      }).join('');
      // Trigger fork config panel visibility if current selection is experiment
      var currentVal = sel.value;
      var panel = document.getElementById('gw-fork-config');
      if (currentVal === 'Multi-Model Comparison' || currentVal === 'Architecture Fork' || currentVal === 'Library Comparison' || currentVal === 'Product Strategy Fork') {
        panel.classList.add('visible');
        if ((!state.forkConfig || !state.forkConfig.length) && state.agentProfiles) {
          state.forkConfig = buildDefaultForkConfig(currentVal);
        }
        renderForkConfigPanel(state.forkConfig || buildDefaultForkConfig(currentVal));
      } else {
        panel.classList.remove('visible');
      }
      return;
    }
    if (msg.type === 'sessions') {
      var sel2 = document.getElementById('gw-session');
      state.__sessions = msg.sessions || [];
      var options = '<option value="">(no exploration)</option>' + state.__sessions.map(function(s) {
        var paused = s.status === 'Paused' ? ' ⏸' : '';
        return '<option value="' + esc(s.sessionId) + '">' + esc(s.sessionId) + ' — ' + esc(s.status) + paused + '</option>';
      }).join('');
      sel2.innerHTML = options;
      sel2.value = msg.selectedSessionId || '';
      state.selectedSessionId = msg.selectedSessionId || '';
      updateSessionControls(state.selectedSessionId, state.__sessions);
      return;
    }
    if (msg.type === 'comparisonData') {
      state.__comparisonData = msg.payload;
      state.__comparisonParentId = msg.parentId;
      if (state.__compareParentId === msg.parentId) {
        document.getElementById('gw-inspector').innerHTML = renderCompareResults(state.__compareChildren, msg.parentId);
        bindCompareResultsButtons();
      }
      return;
    }
    if (msg.type === 'tree') {
      // A poll for the previous session can still be in flight when the user starts a fresh
      // one; without this check whichever response lands last wins, regardless of which
      // session is actually selected now, so a stale poll can clobber the new tree with the
      // old (possibly still-in-progress) session's work units.
      if ((msg.sessionId || '') !== state.selectedSessionId) { return; }
      renderDecisionTree(msg.workUnits);
      return;
    }
    if (msg.type === 'timeline') {
      // Same stale-response race as the tree message: clicking a second node before the
      // first one's fetch resolves means an older, slower response can land after the
      // newer one and overwrite it — or land for a node that's no longer selected at all,
      // which used to render anyway since this had no workUnitId check.
      if (msg.workUnitId !== state.selectedNodeId) { return; }
      renderTimeline(msg.artifacts, msg.events);
      // Slice 18c — store evidence and re-render inspector if node is still selected
      if (msg.evidence) {
        state.selectedNodeEvidence = msg.evidence || [];
      }
      // Slice 18f — store reasoning graph and re-render inspector
      if (msg.reasoningGraph) {
        state.reasoningGraph = msg.reasoningGraph;
      }
      var wu = state.decisionNodes.find(function(w) { return w.workUnitId === state.selectedNodeId; });
      if (wu) {
        document.getElementById('gw-inspector').innerHTML = renderDecisionInspector(wu);
        bindDecisionInspectorTabs();
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
      if (msg.effectiveRepositoryPath !== undefined) {
        var repoDisplay = document.getElementById('gw-repo-path-display');
        repoDisplay.value = msg.effectiveRepositoryPath || '(no folder open)';
        repoDisplay.title = msg.repositoryPathOverride
          ? 'Override: ' + msg.effectiveRepositoryPath
          : 'Auto-detected from the open VS Code folder: ' + msg.effectiveRepositoryPath;
      }
      document.getElementById('gw-llm-profile-checkbox').checked = !!msg.useLlmProfileSelection;
      document.getElementById('gw-max-concurrent-workers').value = msg.maxConcurrentWorkers;
      document.getElementById('gw-scheduler-poll-interval').value = msg.schedulerPollIntervalMs;
      document.getElementById('gw-require-build-checkbox').checked = !!msg.requireBuildBeforeProposal;
      document.getElementById('gw-require-test-checkbox').checked = !!msg.requireTestBeforeProposal;
      document.getElementById('gw-enforce-output-kind-checkbox').checked = !!msg.enforceExpectedOutputKind;
      document.getElementById('gw-block-conflicting-ops-checkbox').checked = !!msg.blockConflictingOps;
      document.getElementById('gw-allow-auto-requeue-checkbox').checked = !!msg.allowAutoRequeue;
      document.getElementById('gw-allow-agent-git-commits-checkbox').checked = !!msg.allowAgentGitCommits;
      document.getElementById('gw-allow-agent-git-push-checkbox').checked = !!msg.allowAgentGitPush;
      if (msg.materializerConcurrency !== undefined) {
        document.getElementById('gw-materializer-concurrency').value = msg.materializerConcurrency;
      }
      var timeoutSecondsEl = document.getElementById('gw-clarification-timeout-seconds');
      var timeoutBehaviorEl = document.getElementById('gw-clarification-timeout-behavior');
      if (timeoutSecondsEl) {
        timeoutSecondsEl.value = msg.defaultClarificationTimeoutSeconds > 0 ? msg.defaultClarificationTimeoutSeconds : 0;
      }
      if (timeoutBehaviorEl) {
        timeoutBehaviorEl.value = msg.defaultClarificationTimeoutBehavior || 'auto_continue';
      }
      // Slice 21c — Target (Direct/Candidate) only makes sense when promotion branch is on.
      document.getElementById('gw-target-row').classList.toggle('visible', !!msg.usePromotionBranch);
      return;
    }
    if (msg.type === 'decisionContext') {
      if (msg.workUnitId === state.selectedNodeId) {
        state.selectedNodeContext = msg.context || null;
        if (state.selectedNodeId) {
          var wuCtx = state.decisionNodes.find(function(w) { return w.workUnitId === state.selectedNodeId; });
          if (wuCtx) {
            document.getElementById('gw-inspector').innerHTML = renderDecisionInspector(wuCtx);
            bindDecisionInspectorTabs();
          }
        }
      }
      return;
    }
    if (msg.type === 'gwOpenConversationStandalone') {
      var wu = msg.workUnit;
      if (!wu) { return; }
      stopConversationPoll();
      state.selectedNodeId = wu.workUnitId;
      state.selectedNodeConversation = null;
      state.selectedNodeContext = null;
      // Not necessarily part of the currently selected session's tree — cache it anyway so the
      // existing decisionNodes-driven helpers (poll lookup, tab re-render) keep working. A later
      // 'tree' poll for the active session will overwrite this array and may drop it again; that
      // only affects the Metadata tab's re-render, not the Conversation tab already on screen.
      state.decisionNodes = (state.decisionNodes || []).filter(function(w) { return w.workUnitId !== wu.workUnitId; });
      state.decisionNodes.push(wu);
      document.getElementById('gw-inspector').innerHTML = renderDecisionInspector(wu);
      bindDecisionInspectorTabs();
      document.querySelectorAll('.gw-tab-btn').forEach(function(b) {
        b.classList.toggle('active', b.getAttribute('data-gw-tab') === 'conversation');
      });
      document.querySelectorAll('.gw-tab-panel').forEach(function(p) {
        p.classList.toggle('active', p.id === 'gw-panel-conversation');
      });
      if (isWuRunning(wu)) { startConversationPoll(wu.workUnitId); }
      return;
    }
    if (msg.type === 'conversationLog') {
      if (msg.workUnitId === state.selectedNodeId) {
        state.selectedNodeConversation = msg.entries || [];
        var convPanel = document.getElementById('gw-panel-conversation');
        if (convPanel) {
          // Polling re-renders this tab by full innerHTML replacement (entries can change shape
          // mid-run), which would otherwise re-collapse every <details> the user had opened and
          // reset their scroll position on every 2s tick. Snapshot by toolUseId (stable across
          // polls) and the scrollable inspector column, then restore after the swap.
          var openIds = Array.prototype.map.call(
            convPanel.querySelectorAll('.conv-tool[open]'),
            function(d) { return d.getAttribute('data-tool-use-id'); },
          );
          var scrollEl = document.getElementById('gw-col-inspector');
          var scrollTop = scrollEl ? scrollEl.scrollTop : 0;
          convPanel.innerHTML = renderConversationTab(state.selectedNodeConversation);
          openIds.forEach(function(id) {
            if (!id) { return; }
            var d = convPanel.querySelector('.conv-tool[data-tool-use-id="' + CSS.escape(id) + '"]');
            if (d) { d.setAttribute('open', ''); }
          });
          if (scrollEl) { scrollEl.scrollTop = scrollTop; }
        }
      }
      return;
    }
    if (msg.type === 'counterfactualComparison') {
      document.getElementById('gw-inspector').innerHTML = renderCounterfactualComparison(msg.comparison);
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