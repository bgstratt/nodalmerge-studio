import * as vscode from 'vscode';
import { buildNonce, SHELL_CSS_VARS } from './sharedWebviewChrome';
import { MergeReviewPanel } from './MergeReviewPanel';
import { AgentConfigPanel } from './AgentConfigPanel';
import { WorkspaceDashboardPanel } from './WorkspaceDashboardPanel';
import { DagReplayPanel } from './DagReplayPanel';
import { ArtifactExplorerPanel } from './ArtifactExplorerPanel';
import type { NotificationManager } from '../NotificationManager';
import type { AgentConfigService } from '../AgentConfigService';

interface TabDef { id: string; label: string }

/**
 * Slice 0 — the consolidated "NodalMerge Studio" window. Previously each feature
 * (Workspace Dashboard, Merge Review, DAG Replay, Agent Config) was its own
 * vscode.WebviewPanel, opened by its own command, appearing as its own editor tab. This is
 * now the only WebviewPanel the extension creates; the 4 panel classes no longer create their
 * own panel/html/message-listener — they're constructed with this shell's shared panel and
 * become "views" whose HTML fragment gets embedded here and whose handleMessage gets called
 * here. See sharedWebviewChrome.ts for how their CSS/JS avoid colliding once combined.
 */
export class StudioShellPanel implements vscode.Disposable {
  static current: StudioShellPanel | undefined;
  private static readonly viewType = 'nodalmerge.studio';

  private readonly panel: vscode.WebviewPanel;
  private readonly disposables: vscode.Disposable[] = [];

  readonly workspace: WorkspaceDashboardPanel;
  readonly mergeReview: MergeReviewPanel;
  readonly agentConfig: AgentConfigPanel;
  readonly dagReplay: DagReplayPanel;
  readonly home: ArtifactExplorerPanel;

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

    this.workspace   = new WorkspaceDashboardPanel(panel, baseUrl, notifications, configService, secrets, lmProxyBaseUrl);
    this.mergeReview = new MergeReviewPanel(panel, baseUrl, configService);
    this.agentConfig = new AgentConfigPanel(panel, baseUrl, configService, secrets, lmProxyBaseUrl);
    this.dagReplay   = new DagReplayPanel(panel, baseUrl);
    this.home        = new ArtifactExplorerPanel(panel, baseUrl, configService, secrets, lmProxyBaseUrl);

    this.panel.webview.html = this.buildHtml(extensionUri);
    this.panel.onDidDispose(() => this.dispose(), null, this.disposables);
    this.panel.webview.onDidReceiveMessage(
      (msg: Record<string, unknown>) => { void this.handleMessage(msg); },
      null,
      this.disposables,
    );

    // Slice 0 simplification: all views start polling/loading as soon as the shell opens,
    // regardless of which tab is visible — there's one always-open webview now, not 4
    // independently hidden/shown panels, so there's no per-tab "became visible" signal to
    // gate this on. Tab-aware pause/resume of polling is a reasonable later refinement, not
    // required for this slice. Merge Review stays idle on purpose — there's no proposal to
    // show until loadProposal()/loadConflict() is called.
    this.workspace.activate();
    this.agentConfig.activate();
    this.dagReplay.activate();
    this.home.activate();
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
      'NodalMerge Studio',
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
   * action needs to bring a specific view to the front (e.g. Merge Review for a proposal). */
  showTab(tabId: string): void {
    void this.panel.webview.postMessage({ type: 'studio.showTab', tab: tabId });
  }

  private async handleMessage(msg: Record<string, unknown>): Promise<void> {
    // Broadcast — verified while planning that the 4 views' message-type vocabularies don't
    // overlap, so each view's own handleMessage safely ignores types it doesn't recognize.
    await Promise.all([
      this.workspace.handleMessage(msg),
      this.mergeReview.handleMessage(msg),
      this.agentConfig.handleMessage(msg),
      this.dagReplay.handleMessage(msg),
      this.home.handleMessage(msg),
    ]);
  }

  private buildHtml(extensionUri: vscode.Uri): string {
    const nonce  = buildNonce();
    const webview = this.panel.webview;

    const homeFragment        = ArtifactExplorerPanel.getFragment();
    const workspaceFragment   = WorkspaceDashboardPanel.getFragment();
    const mergeReviewFragment = MergeReviewPanel.getFragment();
    const agentConfigFragment = AgentConfigPanel.getFragment();
    const dagFragment         = DagReplayPanel.getFragment(webview, extensionUri, nonce);

    const tabs: TabDef[] = [
      { id: ArtifactExplorerPanel.containerId, label: 'Home' },
      { id: AgentConfigPanel.containerId, label: 'Agent Config' },
      { id: WorkspaceDashboardPanel.containerId, label: 'Workspace' },
      { id: MergeReviewPanel.containerId, label: 'Merge Review' },
      { id: DagReplayPanel.containerId, label: 'DAG Replay' },
    ];
    const tabButtonsHtml = tabs
      .map(t => `<button class="nm-shell-tab${t.id === ArtifactExplorerPanel.containerId ? ' active' : ''}" data-tab="${t.id}">${t.label}</button>`)
      .join('\n');

    return `<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="UTF-8">
  <meta http-equiv="Content-Security-Policy"
        content="default-src 'none'; style-src 'nonce-${nonce}'; script-src 'nonce-${nonce}'; connect-src ws://127.0.0.1:*;">
  <meta name="viewport" content="width=device-width, initial-scale=1.0">
  <title>NodalMerge Studio</title>
  <style nonce="${nonce}">
${SHELL_CSS_VARS}
${homeFragment.css}
${workspaceFragment.css}
${mergeReviewFragment.css}
${agentConfigFragment.css}
${dagFragment.css}
  </style>
</head>
<body>
  <div id="nm-shell-tabbar">${tabButtonsHtml}</div>
  <div id="nm-shell-content">
${homeFragment.html}
${agentConfigFragment.html}
${workspaceFragment.html}
${mergeReviewFragment.html}
${dagFragment.html}
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
${agentConfigFragment.script}
  </script>
  <script nonce="${nonce}">
${workspaceFragment.script}
  </script>
  <script nonce="${nonce}">
${mergeReviewFragment.script}
  </script>
  <script nonce="${nonce}">
${homeFragment.script}
  </script>
${dagFragment.scriptTag}
</body>
</html>`;
  }

  dispose(): void {
    StudioShellPanel.current = undefined;
    this.workspace.dispose();
    this.home.dispose();
    this.dagReplay.dispose();
    this.panel.dispose();
    for (const d of this.disposables) { d.dispose(); }
    this.disposables.length = 0;
  }
}
