import * as vscode from 'vscode';
import type { OutputChannel } from 'vscode';
import { toWebSocketUrl } from '../constants';
import { buildNonce, SHELL_CSS_VARS } from './sharedWebviewChrome';
import { DecisionConvergencePanel } from './MergeReviewPanel';
import { ModelAgentStudioPanel } from './AgentConfigPanel';
import { ExecutionTimelinePanel } from './WorkspaceDashboardPanel';
import { TrajectoryReplayPanel } from './DagReplayPanel';
import { GoalWorkspacePanel } from './ArtifactExplorerPanel';
import { InsightsPanel } from './InsightsPanel';
import { ProjectionComparisonPanel } from './ProjectionComparisonPanel';
import type { NotificationManager } from '../NotificationManager';
import type { AgentConfigService } from '../AgentConfigService';

interface TabDef { id: string; label: string }

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
    this.modelAgentStudio    = new ModelAgentStudioPanel(panel, baseUrl, configService, secrets, lmProxyBaseUrl);
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
      StudioShellPanel.current.panel.reveal(vscode.ViewColumn.Two);
      return StudioShellPanel.current;
    }
    const panel = vscode.window.createWebviewPanel(
      StudioShellPanel.viewType,
      'NodalMerge Studio',
      vscode.ViewColumn.Two,
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

  // style-src below intentionally omits a nonce: VS Code's webview host injects the current
  // theme's CSS custom properties via inline style attributes on load, and pairing 'unsafe-inline'
  // with a nonce/hash on the same directive makes browsers disregard 'unsafe-inline' entirely
  // (CSP3 backwards-compat rule) — that blocked the injection and a host-side fallback path threw
  // a document.write() SyntaxError that aborted parsing the rest of the page, silently preventing
  // every script tag after the failure point (including Decision Convergence's) from ever running.
  private buildHtml(extensionUri: vscode.Uri): string {
    const nonce   = buildNonce();
    const webview = this.panel.webview;
    const wsOrigin = toWebSocketUrl(this.baseUrl);

    const goalWorkspaceFragment        = GoalWorkspacePanel.getFragment();
    const modelAgentStudioFragment     = ModelAgentStudioPanel.getFragment();
    const executionTimelineFragment    = ExecutionTimelinePanel.getFragment();
    const decisionConvergenceFragment  = DecisionConvergencePanel.getFragment();
    const trajectoryFragment           = TrajectoryReplayPanel.getFragment(webview, extensionUri, nonce);
    const insightsFragment             = InsightsPanel.getFragment();
    const projectionComparisonFragment = ProjectionComparisonPanel.getFragment();

    const tabs: TabDef[] = [
      { id: GoalWorkspacePanel.containerId, label: 'Goal Workspace' },
      { id: ModelAgentStudioPanel.containerId, label: 'Model & Agent Studio' },
      { id: ExecutionTimelinePanel.containerId, label: 'Activity Center' },
      { id: DecisionConvergencePanel.containerId, label: 'Review' },
      { id: TrajectoryReplayPanel.containerId, label: 'Pathways' },
      { id: InsightsPanel.containerId, label: 'Insights' },
      { id: ProjectionComparisonPanel.containerId, label: 'Projection Snapshots' },
    ];
    const tabButtonsHtml = tabs
      .map(t => `<button class="nm-shell-tab${t.id === GoalWorkspacePanel.containerId ? ' active' : ''}" data-tab="${t.id}">${t.label}</button>`)
      .join('\n');

    return `<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="UTF-8">
  <meta http-equiv="Content-Security-Policy"
        content="default-src 'none'; style-src 'unsafe-inline'; script-src 'nonce-${nonce}'; connect-src ${wsOrigin} ${wsOrigin}/*;">
  <meta name="viewport" content="width=device-width, initial-scale=1.0">
   <title>NodalMerge Studio</title>
  <style nonce="${nonce}">
${SHELL_CSS_VARS}
${goalWorkspaceFragment.css}
${modelAgentStudioFragment.css}
${executionTimelineFragment.css}
${decisionConvergenceFragment.css}
${trajectoryFragment.css}
${insightsFragment.css}
${projectionComparisonFragment.css}
  </style>
</head>
<body>
  <div id="nm-shell-tabbar">${tabButtonsHtml}</div>
  <div id="nm-shell-content">
${goalWorkspaceFragment.html}
${modelAgentStudioFragment.html}
${executionTimelineFragment.html}
${decisionConvergenceFragment.html}
${trajectoryFragment.html}
${insightsFragment.html}
${projectionComparisonFragment.html}
  </div>
  <script nonce="${nonce}">
    (function() {
      window.__nmVscode = acquireVsCodeApi();
      window.onerror = function(msg, src, line, col, err) {
        var stack = (err && err.stack) || (src + ':' + line + ':' + col);
        window.__nmVscode.postMessage({ type: 'nm-webview-error', message: String(msg), stack: stack });
        return false;
      };
      window.onunhandledrejection = function(event) {
        var r = event.reason;
        window.__nmVscode.postMessage({ type: 'nm-webview-error', message: String(r), stack: (r && r.stack) || '' });
      };
      var tabButtons = document.querySelectorAll('.nm-shell-tab');
      var panes = document.querySelectorAll('#nm-shell-content > .nm-shell-pane');
      function showTab(tabId) {
        tabButtons.forEach(function(b) { b.classList.toggle('active', b.getAttribute('data-tab') === tabId); });
        panes.forEach(function(p) { p.classList.toggle('active', p.id === tabId); });
        window.__nmVscode.postMessage({ type: 'studio.tabActivated', tab: tabId });
      }
      tabButtons.forEach(function(b) {
        b.addEventListener('click', function() { showTab(b.getAttribute('data-tab')); });
      });
      window.addEventListener('message', function(event) {
        var msg = event.data;
        if (msg && msg.type === 'studio.showTab') { showTab(msg.tab); }
      });
    })();
  </script>
  <script nonce="${nonce}">
${modelAgentStudioFragment.script}
  </script>
  <script nonce="${nonce}">
${executionTimelineFragment.script}
  </script>
  <script nonce="${nonce}">
${decisionConvergenceFragment.script}
  </script>
  <script nonce="${nonce}">
${goalWorkspaceFragment.script}
  </script>
  <script nonce="${nonce}">
${insightsFragment.script}
  </script>
  <script nonce="${nonce}">
${projectionComparisonFragment.script}
  </script>
${trajectoryFragment.scriptTag}
</body>
</html>`;
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