import * as vscode from 'vscode';
import type { OutputChannel } from 'vscode';
import { toWebSocketUrl } from '../constants';
import { buildNonce } from './sharedWebviewChrome';
import { composeShellHtml } from './shellHtml';
import { DecisionConvergencePanel } from './MergeReviewPanel';
import { ModelAgentStudioPanel } from './AgentConfigPanel';
import { ExecutionTimelinePanel } from './WorkspaceDashboardPanel';
import { TrajectoryReplayPanel } from './DagReplayPanel';
import { GoalWorkspacePanel } from './ArtifactExplorerPanel';
import { InsightsPanel } from './InsightsPanel';
import { ProjectionComparisonPanel } from './ProjectionComparisonPanel';
import type { NotificationManager } from '../NotificationManager';
import type { AgentConfigService } from '../AgentConfigService';

/** [containerId, label] pairs in tab order; first entry is the initially-active tab.
 * Exported for the webview smoke-test harness. */
export const STUDIO_SHELL_TABS: Array<[string, string]> = [
  [GoalWorkspacePanel.containerId, 'Goal Workspace'],
  [ModelAgentStudioPanel.containerId, 'Model & Agent Studio'],
  [ExecutionTimelinePanel.containerId, 'Activity Center'],
  [DecisionConvergencePanel.containerId, 'Review'],
  [TrajectoryReplayPanel.containerId, 'Pathways'],
  [InsightsPanel.containerId, 'Insights'],
  [ProjectionComparisonPanel.containerId, 'Projection Snapshots'],
];

/**
 * NodalMerge Studio shell.
 * Consolidates Goal Workspace, Model & Agent Studio, Activity Center,
 * Review, and Pathways into a single webview panel.
 */
export class StudioShellPanel implements vscode.Disposable {
  static current: StudioShellPanel | undefined;
  private static readonly viewType = 'nodalmerge.studio';

  private readonly panel: vscode.WebviewPanel;
  private readonly baseUrl: string;
  private readonly output: OutputChannel;
  private readonly disposables: vscode.Disposable[] = [];

  readonly activityCenter: ExecutionTimelinePanel;
  readonly reviewPanel: DecisionConvergencePanel;
  readonly modelAgentStudio: ModelAgentStudioPanel;
  readonly pathways: TrajectoryReplayPanel;
  readonly goalWorkspace: GoalWorkspacePanel;
  readonly insights: InsightsPanel;
  readonly projectionComparison: ProjectionComparisonPanel;

  private constructor(
    panel: vscode.WebviewPanel,
    baseUrl: string,
    extensionUri: vscode.Uri,
    configService: AgentConfigService,
    secrets: vscode.SecretStorage,
    lmProxyBaseUrl: string,
    output: OutputChannel,
    notifications?: NotificationManager,
  ) {
    this.panel   = panel;
    this.baseUrl = baseUrl;
    this.output  = output;

    this.activityCenter   = new ExecutionTimelinePanel(panel, baseUrl, notifications, configService, secrets, lmProxyBaseUrl, this.getSelectedSessionId);
    this.reviewPanel = new DecisionConvergencePanel(panel, baseUrl, configService, this.getSelectedSessionId);
    this.modelAgentStudio    = new ModelAgentStudioPanel(panel, baseUrl, configService, secrets, this.onModelAgentConfigChanged);
    this.pathways    = new TrajectoryReplayPanel(panel, baseUrl, this.getSelectedSessionId);
    this.goalWorkspace       = new GoalWorkspacePanel(panel, baseUrl, configService, secrets, lmProxyBaseUrl, this.onSessionChanged);
    this.insights            = new InsightsPanel(panel, baseUrl, configService, secrets, lmProxyBaseUrl);
    this.projectionComparison = new ProjectionComparisonPanel(panel, baseUrl);

    this.panel.webview.html = this.buildHtml(extensionUri);
    this.panel.onDidDispose(() => this.dispose(), null, this.disposables);
    this.panel.webview.onDidReceiveMessage(
      (msg: Record<string, unknown>) => { void this.handleMessage(msg); },
      null,
      this.disposables,
    );

    this.activityCenter.activate();
    this.modelAgentStudio.activate();
    this.pathways.activate();
    this.goalWorkspace.activate();
    this.reviewPanel.activate();
    this.insights.activate();
    this.projectionComparison.activate();
  }

  /** Pushed to Goal Workspace right after a profile/template/session-default save in Model &
   *  Agent Studio, instead of leaving it to Goal Workspace's own ~30s strategies poll. */
  private onModelAgentConfigChanged = (): void => {
    void this.goalWorkspace.sendStrategies();
  };

  /** Returns the currently-selected session ID from the Goal Workspace. */
  private getSelectedSessionId = (): string | undefined => {
    return this.goalWorkspace?.selectedSessionId;
  };

  /** Called by GoalWorkspacePanel when the user selects a session.
   *  Triggers repolling for Activity Center and Pathways so they filter to the session. */
  private onSessionChanged = (sessionId: string | undefined): void => {
    void this.panel.webview.postMessage({ type: 'sessionSelected', sessionId: sessionId ?? '' });
    // Re-poll filtered panels immediately
    void this.activityCenter.triggerPoll();
    void this.pathways.triggerPoll();
    void this.reviewPanel.triggerReload();
  };

  static createOrShow(
    baseUrl: string,
    extensionUri: vscode.Uri,
    configService: AgentConfigService,
    secrets: vscode.SecretStorage,
    lmProxyBaseUrl: string,
    output: OutputChannel,
    notifications?: NotificationManager,
  ): StudioShellPanel {
    if (StudioShellPanel.current) {
      StudioShellPanel.current.panel.reveal(vscode.ViewColumn.Active);
      return StudioShellPanel.current;
    }
    const panel = vscode.window.createWebviewPanel(
      StudioShellPanel.viewType,
      'NodalMerge Studio',
      vscode.ViewColumn.Active,
      {
        enableScripts: true,
        retainContextWhenHidden: true,
        localResourceRoots: [vscode.Uri.joinPath(extensionUri, 'out')],
      },
    );
    StudioShellPanel.current = new StudioShellPanel(
      panel, baseUrl, extensionUri, configService, secrets, lmProxyBaseUrl, output, notifications,
    );
    return StudioShellPanel.current;
  }

  /** Switches the already-open shell to a given tab — used when a notification or dead-letter
   * action needs to bring a specific view to the front (e.g. Decision Convergence for a proposal). */
  showTab(tabId: string): void {
    void this.panel.webview.postMessage({ type: 'studio.showTab', tab: tabId });
  }

  /** Re-runs each sub-panel's initial load. Each sub-panel's activate() only fetches host-backed
   * data once at construction (unlike the polling panels), so a panel opened before the host
   * finished starting — or left open across a host restart — never recovers that data on its
   * own; this gives RESTART_HOST a way to force them to re-sync against the new host instance. */
  refresh(): void {
    this.activityCenter.activate();
    this.modelAgentStudio.activate();
    this.pathways.activate();
    this.goalWorkspace.activate();
    this.reviewPanel.activate();
    this.insights.activate();
    this.projectionComparison.activate();
  }

  private async handleMessage(msg: Record<string, unknown>): Promise<void> {
    if (msg.type === 'nm-webview-error') {
      const where = msg.containerId ? `[${String(msg.containerId)}] ` : '';
      this.output.appendLine(`[NodalMerge] Webview error ${where}${String(msg.message)}`);
      if (msg.stack) { this.output.appendLine(String(msg.stack)); }
      this.output.show(true);
      return;
    }
    if (msg.type === 'sessionOverrideChanged') {
      const panelId = msg.panelId as string;
      const sessionId = (msg.sessionId as string | undefined) || undefined;
      if (panelId === ExecutionTimelinePanel.containerId) {
        this.activityCenter.setSessionOverride(sessionId);
      } else if (panelId === DecisionConvergencePanel.containerId) {
        this.reviewPanel.setSessionOverride(sessionId);
      } else if (panelId === TrajectoryReplayPanel.containerId) {
        this.pathways.setSessionOverride(sessionId);
      }
      return;
    }
    // Phase 11 — Activity Center's "View live transcript" link. Mirrors the showTab + direct
    // panel-method pattern extension.ts already uses for notification/dead-letter deep links
    // (see showTab doc comment above) rather than relying on the broadcast below, since the
    // target work unit may not belong to whatever session Goal Workspace currently has selected.
    if (msg.type === 'activityViewTranscript') {
      this.showTab(GoalWorkspacePanel.containerId);
      await this.goalWorkspace.openConversationStandalone(msg.workUnitId as string);
      return;
    }
    await Promise.all([
      this.activityCenter.handleMessage(msg),
      this.reviewPanel.handleMessage(msg),
      this.modelAgentStudio.handleMessage(msg),
      this.pathways.handleMessage(msg),
      this.goalWorkspace.handleMessage(msg),
      this.insights.handleMessage(msg),
      this.projectionComparison.handleMessage(msg),
    ]);
  }

  // The document itself (CSP notes included) lives in shellHtml.ts so the smoke-test harness
  // can compose the exact production document outside a VS Code extension host.
  private buildHtml(extensionUri: vscode.Uri): string {
    const nonce   = buildNonce();
    const webview = this.panel.webview;
    const wsOrigin = toWebSocketUrl(this.baseUrl);

    const viewsScriptUri = webview.asWebviewUri(
      vscode.Uri.joinPath(extensionUri, 'out', 'studio-views.js'));

    return composeShellHtml({
      nonce,
      wsOrigin,
      resourceCspSource: webview.cspSource,
      viewsScriptTag: `<script nonce="${nonce}" src="${viewsScriptUri}"></script>`,
      goalWorkspace: GoalWorkspacePanel.getFragment(),
      modelAgentStudio: ModelAgentStudioPanel.getFragment(),
      executionTimeline: ExecutionTimelinePanel.getFragment(),
      decisionConvergence: DecisionConvergencePanel.getFragment(),
      trajectory: TrajectoryReplayPanel.getFragment(webview, extensionUri, nonce),
      insights: InsightsPanel.getFragment(),
      projectionComparison: ProjectionComparisonPanel.getFragment(),
      tabs: STUDIO_SHELL_TABS,
    });
  }

  dispose(): void {
    StudioShellPanel.current = undefined;
    this.activityCenter.dispose();
    this.goalWorkspace.dispose();
    this.pathways.dispose();
    this.insights.dispose();
    this.projectionComparison.dispose();
    this.panel.dispose();
    for (const d of this.disposables) { d.dispose(); }
    this.disposables.length = 0;
  }
}