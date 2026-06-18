import * as vscode from 'vscode';
import { buildNonce, SHELL_CSS_VARS } from './sharedWebviewChrome';
import { DecisionConvergencePanel } from './MergeReviewPanel';
import { ModelAgentStudioPanel } from './AgentConfigPanel';
import { ExecutionTimelinePanel } from './WorkspaceDashboardPanel';
import { TrajectoryReplayPanel } from './DagReplayPanel';
import { GoalWorkspacePanel } from './ArtifactExplorerPanel';
import type { NotificationManager } from '../NotificationManager';
import type { AgentConfigService } from '../AgentConfigService';

interface TabDef { id: string; label: string }

/**
 * NodalMerge Studio — Decision Workspace shell.
 * Consolidates Goal Workspace, Model & Agent Studio, Execution Timeline,
 * Decision Convergence, and Trajectory Replay into a single webview panel.
 */
export class StudioShellPanel implements vscode.Disposable {
  static current: StudioShellPanel | undefined;
  private static readonly viewType = 'nodalmerge.studio';

  private readonly panel: vscode.WebviewPanel;
  private readonly disposables: vscode.Disposable[] = [];

  readonly executionTimeline: ExecutionTimelinePanel;
  readonly decisionConvergence: DecisionConvergencePanel;
  readonly modelAgentStudio: ModelAgentStudioPanel;
  readonly trajectoryReplay: TrajectoryReplayPanel;
  readonly goalWorkspace: GoalWorkspacePanel;

  private constructor(
    panel: vscode.WebviewPanel,
    baseUrl: string,
    extensionUri: vscode.Uri,
    configService: AgentConfigService,
    secrets: vscode.SecretStorage,
    lmProxyBaseUrl: string,
    notifications?: NotificationManager,
  ) {
    this.panel = panel;

    this.executionTimeline   = new ExecutionTimelinePanel(panel, baseUrl, notifications, configService, secrets, lmProxyBaseUrl);
    this.decisionConvergence = new DecisionConvergencePanel(panel, baseUrl, configService);
    this.modelAgentStudio    = new ModelAgentStudioPanel(panel, baseUrl, configService, secrets, lmProxyBaseUrl);
    this.trajectoryReplay    = new TrajectoryReplayPanel(panel, baseUrl);
    this.goalWorkspace       = new GoalWorkspacePanel(panel, baseUrl, configService, secrets, lmProxyBaseUrl);

    this.panel.webview.html = this.buildHtml(extensionUri);
    this.panel.onDidDispose(() => this.dispose(), null, this.disposables);
    this.panel.webview.onDidReceiveMessage(
      (msg: Record<string, unknown>) => { void this.handleMessage(msg); },
      null,
      this.disposables,
    );

    this.executionTimeline.activate();
    this.modelAgentStudio.activate();
    this.trajectoryReplay.activate();
    this.goalWorkspace.activate();
  }

  static createOrShow(
    baseUrl: string,
    extensionUri: vscode.Uri,
    configService: AgentConfigService,
    secrets: vscode.SecretStorage,
    lmProxyBaseUrl: string,
    notifications?: NotificationManager,
  ): StudioShellPanel {
    if (StudioShellPanel.current) {
      StudioShellPanel.current.panel.reveal(vscode.ViewColumn.Two);
      return StudioShellPanel.current;
    }
    const panel = vscode.window.createWebviewPanel(
      StudioShellPanel.viewType,
      'NodalMerge Studio — Decision Workspace',
      vscode.ViewColumn.Two,
      {
        enableScripts: true,
        retainContextWhenHidden: true,
        localResourceRoots: [vscode.Uri.joinPath(extensionUri, 'out')],
      },
    );
    StudioShellPanel.current = new StudioShellPanel(
      panel, baseUrl, extensionUri, configService, secrets, lmProxyBaseUrl, notifications,
    );
    return StudioShellPanel.current;
  }

  /** Switches the already-open shell to a given tab — used when a notification or dead-letter
   * action needs to bring a specific view to the front (e.g. Decision Convergence for a proposal). */
  showTab(tabId: string): void {
    void this.panel.webview.postMessage({ type: 'studio.showTab', tab: tabId });
  }

  private async handleMessage(msg: Record<string, unknown>): Promise<void> {
    await Promise.all([
      this.executionTimeline.handleMessage(msg),
      this.decisionConvergence.handleMessage(msg),
      this.modelAgentStudio.handleMessage(msg),
      this.trajectoryReplay.handleMessage(msg),
      this.goalWorkspace.handleMessage(msg),
    ]);
  }

  private buildHtml(extensionUri: vscode.Uri): string {
    const nonce  = buildNonce();
    const webview = this.panel.webview;

    const goalWorkspaceFragment        = GoalWorkspacePanel.getFragment();
    const modelAgentStudioFragment     = ModelAgentStudioPanel.getFragment();
    const executionTimelineFragment    = ExecutionTimelinePanel.getFragment();
    const decisionConvergenceFragment  = DecisionConvergencePanel.getFragment();
    const trajectoryFragment           = TrajectoryReplayPanel.getFragment(webview, extensionUri, nonce);

    const tabs: TabDef[] = [
      { id: GoalWorkspacePanel.containerId, label: 'Goal Workspace' },
      { id: ModelAgentStudioPanel.containerId, label: 'Model & Agent Studio' },
      { id: ExecutionTimelinePanel.containerId, label: 'Execution Timeline' },
      { id: DecisionConvergencePanel.containerId, label: 'Decision Convergence' },
      { id: TrajectoryReplayPanel.containerId, label: 'Trajectory Replay' },
    ];
    const tabButtonsHtml = tabs
      .map(t => `<button class="nm-shell-tab${t.id === GoalWorkspacePanel.containerId ? ' active' : ''}" data-tab="${t.id}">${t.label}</button>`)
      .join('\n');

    return `<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="UTF-8">
  <meta http-equiv="Content-Security-Policy"
        content="default-src 'none'; style-src 'nonce-${nonce}'; script-src 'nonce-${nonce}'; connect-src ws://127.0.0.1:*;">
  <meta name="viewport" content="width=device-width, initial-scale=1.0">
  <title>NodalMerge Studio — Decision Workspace</title>
  <style nonce="${nonce}">
${SHELL_CSS_VARS}
${goalWorkspaceFragment.css}
${modelAgentStudioFragment.css}
${executionTimelineFragment.css}
${decisionConvergenceFragment.css}
${trajectoryFragment.css}
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
  </div>
  <script nonce="${nonce}">
    (function() {
      window.__nmVscode = acquireVsCodeApi();
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
${trajectoryFragment.scriptTag}
</body>
</html>`;
  }

  dispose(): void {
    StudioShellPanel.current = undefined;
    this.executionTimeline.dispose();
    this.goalWorkspace.dispose();
    this.trajectoryReplay.dispose();
    this.panel.dispose();
    for (const d of this.disposables) { d.dispose(); }
    this.disposables.length = 0;
  }
}