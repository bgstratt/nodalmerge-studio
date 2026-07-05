import * as vscode from 'vscode';
import { scopeViewCss } from './sharedWebviewChrome';
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

  static getFragment(): { css: string; html: string } {
    return {
      css: scopeViewCss(GW_CSS, GoalWorkspacePanel.containerId),
      html: `<div id="${GoalWorkspacePanel.containerId}" class="nm-shell-pane active">${GW_HTML}</div>`,
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
          const [reasonA, reasonB] = await Promise.all([
            cfgA ? Promise.resolve(undefined) : this.configService.describeMissingCredentials(modelAProfile.id, this.secrets, this.lmProxyBaseUrl),
            cfgB ? Promise.resolve(undefined) : this.configService.describeMissingCredentials(modelBProfile.id, this.secrets, this.lmProxyBaseUrl),
          ]);
          const parts = [
            reasonA ? `"${modelAProfile.id}": ${reasonA}` : undefined,
            reasonB ? `"${modelBProfile.id}": ${reasonB}` : undefined,
          ].filter(Boolean);
          throw new Error(`Orchestrator profile(s) not ready — ${parts.join('; ')}.`);
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
        const reason = await this.configService.describeMissingCredentials(template.orchestrator, this.secrets, this.lmProxyBaseUrl);
        throw new Error(`Profile "${template.orchestrator}" isn't ready — ${reason}.`);
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
          const reason = await this.configService.describeMissingCredentials(profileId, this.secrets, this.lmProxyBaseUrl);
          throw new Error(`Profile "${profileId}" isn't ready — ${reason}.`);
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

