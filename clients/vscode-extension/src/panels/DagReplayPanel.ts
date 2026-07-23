import * as vscode from 'vscode';
import * as os from 'os';
import * as path from 'path';
import * as fs from 'fs';
import { toWebSocketUrl } from '../constants';
import { resolveRepositoryPath } from '../repositoryPath';
import { scopeViewCss, openReadOnlyDiff } from './sharedWebviewChrome';

const POLL_INTERVAL_MS = 2_000;

// Root directory for "Materialize to scratch workspace" output — set once from extension.ts's
// activate() (the only place with access to ExtensionContext.globalStorageUri) rather than
// threading an extra constructor parameter through StudioShellPanel's four createOrShow call
// sites. Falls back to the OS temp dir when unset (e.g. tests constructing the panel directly).
let pathwaysScratchRoot: string | undefined;
export function setPathwaysScratchRoot(fsPath: string): void {
  pathwaysScratchRoot = fsPath;
}

interface WorkUnit {
  workUnitId: string;
  branchId: string;
  goal: string;
  owner: string;
  status: string;
  currentStage?: string | null;
}

// plans/pathways-workspace-history.md slice 1 — nodes/edges from the WorkspacePathways
// projection (GET /studio/projections/WorkspacePathways). Replaces the old per-work-unit
// artifact + orchestration-decision-log dump (/studio/replay/timeline) that made Pathways
// read as an agent's internal task list instead of workspace history.
interface PathwaysNode {
  nodeId: string;
  kind: string;
  workUnitId?: string | null;
  branchId?: string | null;
  actorKind: string;
  actorId?: string | null;
  actorModel?: string | null;
  actorProvider?: string | null;
  summary: string;
  occurredAt: string;
  proposalId?: string | null;
  artifactId?: string | null;
  filesTouched?: string[] | null;
  snapshotId?: string | null;
  externalSyncStateIdBefore?: string | null;
  externalSyncStateIdAfter?: string | null;
  reviewedBy?: string | null;
}

interface PathwaysEdge {
  fromNodeId: string;
  toNodeId: string;
  kind: string;
}

interface PathwaysPayload {
  nodes: PathwaysNode[];
  edges: PathwaysEdge[];
  generatedAt: string;
}

interface KnownGoodState {
  stateId: string;
  branchId: string;
  description: string;
  createdAt: string;
}

export class TrajectoryReplayPanel {
  static readonly containerId = 'shell-pane-trajectory-replay';

  private readonly panel: vscode.WebviewPanel;
  private readonly baseUrl: string;
  private readonly getSelectedSessionId?: () => string | undefined;
  private localSessionOverride?: string;
  private pollTimer?: ReturnType<typeof setInterval>;

  constructor(panel: vscode.WebviewPanel, baseUrl: string, getSelectedSessionId?: () => string | undefined) {
    this.panel = panel;
    this.baseUrl = baseUrl;
    this.getSelectedSessionId = getSelectedSessionId;
  }

  /** Called once by the shell right after construction — was the tail of createOrShow(). */
  activate(): void {
    void this.init();
    void this.sendSessionPicker();
    this.pollTimer = setInterval(() => { void this.refreshWorkUnits(); }, POLL_INTERVAL_MS);
  }

  /** Immediately re-polls — used by the shell when the selected session changes. */
  async triggerPoll(): Promise<void> {
    await this.init();
    void this.sendSessionPicker();
  }

  setSessionOverride(sessionId: string | undefined): void {
    this.localSessionOverride = sessionId;
    void this.sendSessionPicker();
    void this.init();
  }

  private getEffectiveSessionId(): string | undefined {
    return this.localSessionOverride ?? this.getSelectedSessionId?.();
  }

  private async sendSessionPicker(): Promise<void> {
    try {
      const sessions = await this.get<Array<{ sessionId: string; status: string }>>('/studio/sessions');
      void this.panel.webview.postMessage({
        type: 'updateSessionPicker',
        panelId: TrajectoryReplayPanel.containerId,
        sessions,
        shellSessionId: this.getSelectedSessionId?.(),
        overrideSessionId: this.localSessionOverride,
      });
    } catch { /* host not ready */ }
  }

  dispose(): void {
    if (this.pollTimer) { clearInterval(this.pollTimer); this.pollTimer = undefined; }
  }

  /** Slice 0 — unlike the 3 inline-HTML views, this one's JS is an external esbuild bundle
   * (out/dag-replay.js, kept as-is — see main.ts for the one acquireVsCodeApi() change that
   * was needed) loaded via its own nonced <script src>, not an inline <script> block. Its DOM
   * ids (dag-svg, scrubber, ...) don't collide with any other view's, so unlike the other 3
   * views this fragment's script is not root-scoped. */
  static getFragment(webview: vscode.Webview, extensionUri: vscode.Uri, nonce: string):
    { css: string; html: string; scriptTag: string } {
    const scriptUri = webview.asWebviewUri(vscode.Uri.joinPath(extensionUri, 'out', 'dag-replay.js'));
    return TrajectoryReplayPanel.getFragmentForScriptSrc(String(scriptUri), nonce);
  }

  /** Pure variant of getFragment — no vscode API needed; used by getFragment above and by the
   * webview smoke-test harness, which serves out/dag-replay.js from a plain file path. */
  static getFragmentForScriptSrc(scriptSrc: string, nonce: string):
    { css: string; html: string; scriptTag: string } {
    return {
      css: scopeViewCss(DAG_REPLAY_CSS, TrajectoryReplayPanel.containerId),
      html: `<div id="${TrajectoryReplayPanel.containerId}" class="nm-shell-pane">${DAG_REPLAY_HTML}</div>`,
      scriptTag: `<script nonce="${nonce}" src="${scriptSrc}"></script>`,
    };
  }

  // WorkspacePathways is deliberately workspace-wide, not session-scoped (it's the "branchable
  // git tree" of the whole workspace's history, not one agent's task list) — so unlike the
  // workunits fetch, no sessionId param goes on this request regardless of the session picker.
  private async fetchPathways(): Promise<PathwaysPayload> {
    const res = await this.get<{ data: PathwaysPayload }>('/studio/projections/WorkspacePathways?level=Normal');
    return res.data;
  }

  private async init(): Promise<void> {
    try {
      const sessionId = this.getEffectiveSessionId();
      const params = sessionId ? '?sessionId=' + encodeURIComponent(sessionId) : '';
      // Pathways is best-effort: a host predating ProjectionType.WorkspacePathways 400s the
      // fetch, and that must degrade to "lanes without history nodes," not kill the whole init
      // (work units, stages, and the WS connection all ride on this call).
      const [workUnits, pathwaysResult] = await Promise.all([
        this.get<WorkUnit[]>('/studio/workunits' + params),
        this.fetchPathways().catch(() => undefined),
      ]);
      const pathways = pathwaysResult;
      void this.panel.webview.postMessage({
        type:      'init',
        wsUrl:     this.buildWsUrl(),
        roomId:    'studio-main',
        // Tells the webview the workUnits list is session-scoped, so it dims out-of-session
        // lanes (pathways data itself is always workspace-wide).
        sessionActive: !!sessionId,
        workUnits: workUnits.map(wu => ({
          workUnitId:   wu.workUnitId,
          branchId:     wu.branchId,
          goal:         wu.goal,
          currentStage: wu.currentStage ?? null,
        })),
        // Slice 1 (pathways-workspace-history.md) — include pathways data in the init payload so
        // the DAG renders goal/integration/rejection/external nodes immediately, not just stages
        // that change after the panel opens.
        pathways,
      });
    } catch {
      // host not running or no data yet — WebView shows idle state
    }
  }

  /** Polled every POLL_INTERVAL_MS so work units fanned out after the panel was opened (no
   * goal/stage entry in the webview's workUnitIdToBranchId map otherwise) become visible without
   * requiring the panel to be closed and reopened. Also refreshes pathways data so new
   * integrations/rejections/external updates appear without a full re-init. */
  private async refreshWorkUnits(): Promise<void> {
    try {
      const sessionId = this.getEffectiveSessionId();
      const params = sessionId ? '?sessionId=' + encodeURIComponent(sessionId) : '';
      const [workUnits, pathways] = await Promise.all([
        this.get<WorkUnit[]>('/studio/workunits' + params),
        this.fetchPathways().catch(() => undefined), // best-effort — same reason as init()
      ]);
      void this.panel.webview.postMessage({
        type: 'workUnits',
        sessionActive: !!sessionId, // same session-highlight flag as init()
        workUnits: workUnits.map(wu => ({
          workUnitId:   wu.workUnitId,
          branchId:     wu.branchId,
          goal:         wu.goal,
          currentStage: wu.currentStage ?? null,
        })),
        pathways,
      });
    } catch {
      // host not running — same suppress-and-poll-later convention as init()
    }
  }

  async handleMessage(msg: Record<string, unknown>): Promise<void> {
    // Re-fetch when this tab is activated so the user always sees session-scoped data immediately.
    if (msg.type === 'studio.tabActivated' && msg.tab === TrajectoryReplayPanel.containerId) {
      await this.refreshWorkUnits();
      return;
    }

    // Slice 2 fast-follow (pathways-workspace-history.md) — "View Diff" on a node-detail file
    // change. Shares sharedWebviewChrome's read-only content provider with MergeReviewPanel.ts
    // rather than registering a second one under the same scheme (VS Code only allows one
    // registration per scheme per extension). The message type is namespaced ("pathways." prefix)
    // because StudioShellPanel broadcasts every webview message to every panel's handleMessage —
    // a bare "openDiff" here was also handled by DecisionConvergencePanel, opening duplicate tabs.
    if (msg.type === 'pathways.openDiff') {
      const filePath = String(msg.path ?? 'file');
      const before = (msg.beforeContent as string | null | undefined) ?? '';
      const after = (msg.afterContent as string | null | undefined) ?? '';
      await openReadOnlyDiff(filePath, before, after);
      return;
    }

    // Slice 18d — trajectory replay mode switch: fetch data from the appropriate
    // /studio/trajectory/replay?mode= endpoint and send to the webview.
    if (msg.type === 'replayModeChanged') {
      const mode = (msg.mode as string) || 'linear';
      const workUnitId = (msg.workUnitId as string) || undefined;
      const sessionId = this.getEffectiveSessionId();
      try {
        let url = '/studio/trajectory/replay?mode=' + encodeURIComponent(mode);
        if (workUnitId) { url += '&workUnitId=' + encodeURIComponent(workUnitId); }
        if (sessionId) { url += '&sessionId=' + encodeURIComponent(sessionId); }
        const data = await this.get<unknown>(url);
        void this.panel.webview.postMessage({ type: 'replayModeData', mode, data });
      } catch (err) {
        void vscode.window.showErrorMessage('NodalMerge: failed to load replay mode — ' + String(err));
      }
      return;
    }

    // Pathways → Plan sub-tab: fetch the read-only plan-decomposition tree for the effective
    // session (server-composed nodes + edges + contracts). Namespaced because the shell broadcasts
    // every webview message to every panel's handleMessage. Errors post null+error to the webview
    // rather than a toast — this can fire on the 2s poll while the Plan tab is open.
    if (msg.type === 'pathways.loadPlanTree') {
      const sessionId = this.getEffectiveSessionId();
      if (!sessionId) {
        void this.panel.webview.postMessage({ type: 'pathways.planTree', data: null });
        return;
      }
      try {
        const data = await this.get<unknown>('/studio/sessions/' + encodeURIComponent(sessionId) + '/plan-tree');
        void this.panel.webview.postMessage({ type: 'pathways.planTree', sessionId, data });
      } catch (err) {
        void this.panel.webview.postMessage({ type: 'pathways.planTree', data: null, error: String(err) });
      }
      return;
    }

    if (msg.type === 'branchFromCursor') {
      const goal = await vscode.window.showInputBox({
        prompt:         'Goal for branch from cursor',
        placeHolder:    'e.g. Experiment from this checkpoint',
        ignoreFocusOut: true,
      });
      if (!goal) { return; }
      try {
        const repositoryPath = resolveRepositoryPath();
        const sourceBranchId = msg.sourceBranchId as string | undefined;
        const wu = await this.post<WorkUnit>('/studio/workunits', {
          goal, owner: 'user',
          ...(repositoryPath ? { repositoryPath } : {}),
          ...(sourceBranchId ? { seedFromBranchId: sourceBranchId } : {}),
        });
        // Tell the WebView to register the new branch from the cursor position
        void this.panel.webview.postMessage({
          type:        'branchCreated',
          newBranchId: wu.branchId,
          workUnitId:  wu.workUnitId,
          goal:        wu.goal,
        });
      } catch (err) {
        void vscode.window.showErrorMessage('NodalMerge: ' + String(err));
      }
      return;
    }

    // Slice 3 (pathways-workspace-history.md) — "Branch from here (new steering)" on an
    // Integration/Rejection/Superseded node. Generalizes ICounterfactualService (already real,
    // already reachable at POST /studio/counterfactuals, just never called from any UI) rather
    // than inventing a new branch-from-node mechanism — PathwaysNode.proposalId is exactly the
    // ProposalId that service's base/{proposalId} snapshot seeding already keys on.
    if (msg.type === 'createCounterfactual') {
      const proposalId = msg.proposalId as string;
      try {
        const profiles = await this.get<Array<{ agentProfileId: string; name: string }>>('/studio/agent-profiles');
        if (profiles.length === 0) {
          void vscode.window.showWarningMessage('NodalMerge: no agent profiles configured to branch with.');
          return;
        }
        const picked = await vscode.window.showQuickPick(
          profiles.map(p => ({ label: p.name, description: p.agentProfileId, profile: p })),
          { placeHolder: 'Pick the profile/model to re-run with', ignoreFocusOut: true },
        );
        if (!picked) { return; }

        const newGoalOverride = await vscode.window.showInputBox({
          prompt:         'Optional: override the goal for this counterfactual',
          placeHolder:    'Leave blank to keep the original goal',
          ignoreFocusOut: true,
        });
        const constraintText = await vscode.window.showInputBox({
          prompt:         'Optional: additional steering constraint',
          placeHolder:    'e.g. Must not modify the public API',
          ignoreFocusOut: true,
        });

        const result = await this.post<{ newWorkUnitId: string }>('/studio/counterfactuals', {
          proposalId,
          newProfileId:    picked.profile.agentProfileId,
          newGoalOverride: newGoalOverride || undefined,
          constraintText:  constraintText || undefined,
        });

        const newWu = await this.get<{ branchId: string; goal: string }>(
          '/studio/workunits/' + encodeURIComponent(result.newWorkUnitId));
        void this.panel.webview.postMessage({
          type:        'branchCreated',
          newBranchId: newWu.branchId,
          workUnitId:  result.newWorkUnitId,
          goal:        newWu.goal,
        });
        void vscode.window.showInformationMessage('NodalMerge: Counterfactual branch created — ' + newWu.goal);
      } catch (err) {
        void vscode.window.showErrorMessage('NodalMerge: ' + String(err));
      }
      return;
    }

    // Slice 3 — "Materialize to scratch" on any node with a workUnitId. Reuses
    // IProjectionMaterializer (already real, already REST-reachable at
    // POST /studio/projections/{workUnitId}/materialize) — the only new part is generating a
    // guaranteed-fresh scratch directory client-side and always passing it explicitly as
    // targetPath. Never omit targetPath here: MaterializeAsync defaults to
    // WorkspaceOptions.SeedRepositoryPath when it's absent, which is the REAL working repo, not
    // a scratch copy. Note this reconstructs the work unit's branch's *current* live content
    // (IFileWorkspaceService), not a state pinned to the exact moment this pathway node was
    // recorded — true point-in-time reconstruction needs the RepositorySnapshot+CAS route
    // (plans/pathways-workspace-history.md verification item 1, still open).
    if (msg.type === 'materializeNode') {
      const workUnitId = msg.workUnitId as string | undefined;
      const snapshotId = msg.snapshotId as string | undefined;
      try {
        // Named, discoverable scratch location: {globalStorage}/pathways-scratch/{branch}/{stamp}.
        // Grouped by branch so repeat materializations of the same lane sit side by side, stamped
        // so they never overwrite each other, under extension storage so OS temp cleanup can't
        // silently eat a workspace the user is still reading. Never the live repo (the server
        // defaults targetPath to WorkspaceOptions.SeedRepositoryPath when omitted — the one thing
        // this must never do, so targetPath is always explicit here).
        const branchLabel = ((msg.branchId as string | undefined) ?? workUnitId ?? snapshotId ?? 'node')
          .replace(/[^A-Za-z0-9._-]/g, '_');
        const stamp = new Date().toISOString().replace(/[:.]/g, '-').slice(0, 19);
        const scratchDir = path.join(
          pathwaysScratchRoot ?? path.join(os.tmpdir(), 'nodalmerge'),
          'pathways-scratch', branchLabel, stamp);
        fs.mkdirSync(scratchDir, { recursive: true });
        // Point-in-time route when the node carries a RepositorySnapshot id (Integration nodes
        // whose MergeApplied event recorded one) — reconstructs the repo exactly as that
        // integration left it, from snapshot TreeEntries + CAS. Branch-current fallback otherwise.
        const route = snapshotId
          ? '/studio/repository-snapshots/' + encodeURIComponent(snapshotId) + '/materialize?targetPath='
          : '/studio/projections/' + encodeURIComponent(workUnitId ?? '') + '/materialize?targetPath=';
        const result = await this.post<{ succeeded: boolean; targetPath: string; fileCount: number; error?: string }>(
          route + encodeURIComponent(scratchDir),
          {},
        );
        if (!result.succeeded) {
          void vscode.window.showErrorMessage('NodalMerge: materialize failed — ' + (result.error ?? 'unknown error'));
          return;
        }
        const openNow = await vscode.window.showInformationMessage(
          'NodalMerge: materialized ' + result.fileCount + ' file(s) to ' + result.targetPath,
          'Open in New Window',
        );
        if (openNow === 'Open in New Window') {
          await vscode.commands.executeCommand(
            'vscode.openFolder', vscode.Uri.file(result.targetPath), { forceNewWindow: true });
        }
      } catch (err) {
        void vscode.window.showErrorMessage('NodalMerge: ' + String(err));
      }
      return;
    }

    // Slice 4 — manual "Sync now". Reuses /studio/workspace/switch (its ManualRefresh path is
    // exactly a resync) — but the path it re-submits is the HOST's currently-configured
    // SeedRepositoryPath (from /studio/workspace-summary), NOT resolveRepositoryPath()'s VS Code
    // folder. Switch *sets* SeedRepositoryPath to whatever it's given; submitting the extension's
    // resolved folder when the host is configured against a different repo would silently perform
    // a real RepositorySwitch (resetting sync generation and the external-changeset chain).
    // resolveRepositoryPath() is only the fallback when the host has no path configured yet.
    if (msg.type === 'syncNow') {
      try {
        const summary = await this.get<{ seedRepositoryPath?: string | null }>('/studio/workspace-summary');
        const repositoryPath = summary.seedRepositoryPath || resolveRepositoryPath();
        if (!repositoryPath) {
          void vscode.window.showWarningMessage('NodalMerge: no repository path configured to sync from.');
          return;
        }
        const result = await this.post<{ sync: unknown }>('/studio/workspace/switch', { repositoryPath });
        if (result.sync) {
          void vscode.window.showInformationMessage('NodalMerge: synced external changes from the working repository.');
          await this.refreshWorkUnits();
        } else {
          void vscode.window.showInformationMessage('NodalMerge: no external changes found.');
        }
      } catch (err) {
        void vscode.window.showErrorMessage('NodalMerge: ' + String(err));
      }
      return;
    }

    // Slice 4 (pathways-workspace-history.md) — file-level diff for an ExternalUpdate node.
    // Reuses IProjectionMaterializer.DiffKnownGoodStatesAsync via the existing
    // GET /studio/projections/known-good/{a}/diff/{b} — RepositorySyncService already marks a
    // KnownGoodState immediately before and after applying every external diff (see
    // ExternalSyncStateIdBefore/After on the pathways node), so this is a path-list diff only
    // (no hunks — there's no before-content captured for an external edit), same as the plan
    // always scoped this to.
    if (msg.type === 'viewExternalDiff') {
      const stateIdBefore = msg.stateIdBefore as string;
      const stateIdAfter = msg.stateIdAfter as string;
      const nodeId = msg.nodeId as string;
      try {
        const diff = await this.get<unknown>(
          '/studio/projections/known-good/' + encodeURIComponent(stateIdBefore)
          + '/diff/' + encodeURIComponent(stateIdAfter));
        void this.panel.webview.postMessage({ type: 'externalDiff', nodeId, diff });
      } catch (err) {
        void this.panel.webview.postMessage({ type: 'externalDiff', nodeId, error: String(err) });
      }
      return;
    }

    if (msg.type === 'inspectNode') {
      const branchId = msg.branchId as string;
      const nodeId = msg.nodeId as string;
      // Slice 2 (pathways-workspace-history.md) — main.ts only sends this for a MergeProposal
      // (Integration/Rejection/Superseded pathways nodes; see pathwaysInspectId), so a workUnitId
      // means "also fetch the file diffs and agent conversation that produced this proposal."
      const workUnitId = msg.workUnitId as string | undefined;
      // Opaque echo — the webview matches it against the latest-clicked node to drop stale
      // responses from rapid consecutive clicks.
      const requestNodeId = msg.requestNodeId as string | undefined;
      try {
        const detail = await this.get<{ kind?: string; artifact?: { type?: string } }>(
          '/studio/replay/inspect/' + encodeURIComponent(branchId) + '?nodeId=' + encodeURIComponent(nodeId));

        let fileChanges: unknown;
        let conversationLog: unknown;
        if (workUnitId && detail.kind === 'artifact' && detail.artifact?.type === 'MergeProposal') {
          const [changesResult, logResult] = await Promise.allSettled([
            this.get<{ fileChanges: unknown }>('/studio/merges/' + encodeURIComponent(nodeId) + '/file-changes'),
            this.get<unknown>('/studio/workunits/' + encodeURIComponent(workUnitId) + '/conversation-log'),
          ]);
          if (changesResult.status === 'fulfilled') { fileChanges = changesResult.value.fileChanges; }
          if (logResult.status === 'fulfilled') { conversationLog = logResult.value; }
        }

        void this.panel.webview.postMessage({ type: 'nodeDetail', nodeId, requestNodeId, detail, fileChanges, conversationLog });
      } catch (err) {
        void this.panel.webview.postMessage({ type: 'nodeDetail', nodeId, requestNodeId, error: String(err) });
      }
      return;
    }

    if (msg.type === 'markKnownGood') {
      const label = await vscode.window.showInputBox({
        prompt:         'Label for this checkpoint',
        placeHolder:    'e.g. all tests passing',
        ignoreFocusOut: true,
      });
      if (!label) { return; }
      try {
        await this.post('/studio/state/markKnownGood', {
          branchId:    msg.branchId as string,
          nodeId:      msg.nodeId   as string,
          description: label,
          createdBy:   'user',
        });
        void vscode.window.showInformationMessage(
          'NodalMerge: Known good state saved — "' + label + '"'
        );
      } catch (err) {
        void vscode.window.showErrorMessage('NodalMerge: ' + String(err));
      }
      return;
    }

    if (msg.type === 'restoreKnownGood') {
      const branchId = msg.branchId as string;
      try {
        const states = await this.get<KnownGoodState[]>('/studio/state/knownGood/' + encodeURIComponent(branchId));
        if (states.length === 0) {
          void vscode.window.showWarningMessage('NodalMerge: no known-good checkpoints marked for this branch yet.');
          return;
        }
        let picked: KnownGoodState | undefined = states[0];
        if (states.length > 1) {
          picked = await vscode.window.showQuickPick(
            states.map(s => ({ label: s.description, description: new Date(s.createdAt).toLocaleString(), state: s })),
            { placeHolder: 'Select a known-good checkpoint to restore', ignoreFocusOut: true },
          ).then(p => p?.state);
        } else {
          const confirm = await vscode.window.showQuickPick(['Restore', 'Cancel'], {
            placeHolder: 'Restore branch to "' + states[0].description + '"?', ignoreFocusOut: true,
          });
          if (confirm !== 'Restore') { return; }
        }
        if (!picked) { return; }

        await this.post('/studio/replay/rollback/' + encodeURIComponent(branchId), { knownGoodStateId: picked.stateId });
        void vscode.window.showInformationMessage('NodalMerge: Restored branch to "' + picked.description + '".');
        await this.refreshWorkUnits();
      } catch (err) {
        void vscode.window.showErrorMessage('NodalMerge: ' + String(err));
      }
    }
  }

  private buildWsUrl(): string {
    return toWebSocketUrl(this.baseUrl) + '/ws/runtime';
  }

  private async get<T>(path: string): Promise<T> {
    const res = await fetch(this.baseUrl + path);
    if (!res.ok) { throw new Error('GET ' + path + ' → ' + String(res.status)); }
    return res.json() as Promise<T>;
  }

  private async post<T = unknown>(path: string, body: unknown): Promise<T> {
    const res = await fetch(this.baseUrl + path, {
      method:  'POST',
      headers: { 'Content-Type': 'application/json' },
      body:    JSON.stringify(body),
    });
    if (!res.ok) {
      const text = await res.text();
      throw new Error('POST ' + path + ' → ' + String(res.status) + ': ' + text);
    }
    return res.json() as Promise<T>;
  }

}

const DAG_REPLAY_CSS = `
    :root {
      --nm-bg:     var(--vscode-editor-background);
      --nm-fg:     var(--vscode-editor-foreground);
      --nm-border: var(--vscode-widget-border, #444);
      --nm-font:   var(--vscode-font-family);
      --nm-mono:   var(--vscode-editor-font-family, monospace);
      --nm-size:   var(--vscode-font-size, 13px);
      --nm-btn:    var(--vscode-button-background);
      --nm-btn-fg: var(--vscode-button-foreground);
      --nm-btn-h:  var(--vscode-button-hoverBackground);
      --nm-success: #4dac26;
      --nm-error:   #f14c4c;
      --nm-info:    var(--vscode-textLink-foreground, #3794ff);
    }
    .hidden { display: none; }
    .status-dot[data-status="idle"] { background: #555; }
    .status-dot[data-status="connecting"] { background: #cca700; }
    .status-dot[data-status="connected"] { background: #4dac26; }
    .status-dot[data-status="disconnected"] { background: #888; }
    .status-dot[data-status="error"] { background: #f14c4c; }
    #dag-svg .clickable { cursor: pointer; }
    * { box-sizing: border-box; }
    body {
      background: var(--nm-bg); color: var(--nm-fg);
      font-family: var(--nm-font); font-size: var(--nm-size);
      margin: 0; padding: 0;
      display: flex; flex-direction: column; overflow: hidden;
    }
    #toolbar {
      display: flex; align-items: center; gap: 10px;
      padding: 7px 14px; border-bottom: 1px solid var(--nm-border);
      flex-shrink: 0; font-size: 0.82em;
    }
    .toolbar-title { font-weight: 700; font-size: 1em; }
    .status-dot {
      width: 7px; height: 7px; border-radius: 50%;
      display: inline-block; background: #555; flex-shrink: 0;
    }
    #status-text { opacity: 0.6; }
    #node-count  { opacity: 0.45; margin-left: auto; }
    #dag-scroll  { flex: 1; overflow: auto; padding: 8px; }
    #dag-svg     { display: block; min-width: 400px; min-height: 120px; }
    #scrubber-row {
      display: flex; align-items: center; gap: 8px;
      padding: 6px 14px; border-top: 1px solid var(--nm-border);
      flex-shrink: 0; font-size: 0.8em;
    }
    #scrub-branch { opacity: 0.55; font-family: var(--nm-mono); max-width: 140px;
                    overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    #scrubber     { flex: 1; accent-color: #3794ff; }
    #scrub-pos    { opacity: 0.5; white-space: nowrap; min-width: 50px; text-align: right; }
    #playback-bar {
      display: flex; align-items: center; gap: 8px;
      padding: 6px 14px; border-top: 1px solid var(--nm-border);
      flex-shrink: 0; background: color-mix(in srgb, #3794ff 8%, var(--nm-bg));
    }
    #playback-bar span { font-size: 0.78em; opacity: 0.7; margin-right: 4px; }
    button {
      background: var(--nm-btn); color: var(--nm-btn-fg);
      border: none; border-radius: 3px;
      padding: 3px 10px; font-size: 0.8em;
      cursor: pointer; font-family: var(--nm-font);
    }
    button:hover { background: var(--nm-btn-h); }
    #btn-branch { background: #7c4dff; }
    #btn-branch:hover { filter: brightness(1.15); }
    #btn-kgs    { background: #4dac26; }
    #btn-kgs:hover    { filter: brightness(1.15); }
    #btn-restore-kgs { background: #c2740a; }
    #btn-restore-kgs:hover { filter: brightness(1.15); }
    #node-detail {
      border-top: 1px solid var(--nm-border);
      max-height: 420px; overflow-y: auto;
      flex-shrink: 0; font-size: 0.82em;
      padding: 8px 14px;
    }
    #node-detail-header {
      display: flex; justify-content: space-between; align-items: center;
      font-weight: 600; margin-bottom: 6px;
    }
    #node-detail-close { background: transparent; padding: 0 6px; font-size: 0.9em; }
    .node-detail-row { margin: 3px 0; }
    .node-detail-label { opacity: 0.55; margin-right: 6px; }
    .node-detail-body-text {
      white-space: pre-wrap; background: rgba(127,127,127,0.12);
      border-radius: 3px; padding: 6px; margin-top: 4px;
      font-family: var(--nm-mono);
    }
    /* Slice 2 (pathways-workspace-history.md) — agent context + file diffs, styled to match
       MergeReviewPanel/decisionConvergence.js's diff rendering conventions. */
    .node-detail-section {
      margin-top: 12px; padding-top: 10px;
      border-top: 1px solid var(--nm-border);
    }
    .node-detail-section h3 {
      font-size: 0.78em; font-weight: 700;
      text-transform: uppercase; letter-spacing: 0.07em;
      opacity: 0.5; margin: 0 0 6px;
    }
    .conv-entry {
      margin: 0 0 8px; padding: 6px 8px;
      border-left: 2px solid var(--nm-info);
      background: rgba(127,127,127,0.06);
    }
    .conv-entry-meta { opacity: 0.55; font-size: 0.85em; margin-bottom: 2px; }
    .conv-entry-text { white-space: pre-wrap; }
    .conv-tool-call { opacity: 0.7; font-family: var(--nm-mono); font-size: 0.85em; margin-top: 2px; }
    .file-change { margin: 0 0 8px; border: 1px solid var(--nm-border); border-radius: 4px; padding: 4px 8px; }
    .file-change summary { cursor: pointer; font-family: var(--nm-mono); font-size: 0.9em; }
    .file-change-badge {
      display: inline-block; border-radius: 9px; padding: 0 7px; margin-left: 6px;
      font-size: 0.78em; background: var(--vscode-badge-background); color: var(--vscode-badge-foreground);
    }
    .diff-line { font-family: var(--nm-mono); font-size: 0.85em; white-space: pre; overflow-x: auto; padding: 0 8px; }
    .diff-add  { color: var(--nm-success); background: rgba(35, 134, 54, 0.12); }
    .diff-del  { color: var(--nm-error);   background: rgba(241, 76, 76, 0.12); }
    .diff-meta { color: var(--nm-info); opacity: 0.7; font-family: var(--nm-mono); font-size: 0.85em; padding: 2px 8px; }
    .diff-empty { opacity: 0.6; padding: 4px 8px; font-size: 0.9em; }
    /* Pathways sub-tabs (Trajectory | Plan) + the read-only Plan pane. */
    .pw-tabs { display: flex; gap: 2px; padding: 0 14px; border-bottom: 1px solid var(--nm-border); flex-shrink: 0; }
    .pw-tab {
      background: transparent; color: var(--nm-fg); border: none; border-radius: 0;
      border-bottom: 2px solid transparent; padding: 5px 12px; font-size: 0.82em; opacity: 0.6;
    }
    .pw-tab:hover { background: transparent; opacity: 0.85; }
    .pw-tab.pw-tab-active { opacity: 1; font-weight: 600; border-bottom-color: var(--nm-info); }
    .pw-pane { flex: 1; display: flex; flex-direction: column; overflow: hidden; min-height: 0; }
    .pw-plan-legend {
      display: flex; gap: 14px; align-items: center; flex-wrap: wrap;
      padding: 3px 14px; font-size: 0.72em; opacity: 0.6;
      border-bottom: 1px solid var(--nm-border); flex-shrink: 0;
    }
    /* The SVG fills the pane; pan/zoom is a transform on the inner viewport group, so there's no
       scroll reflow and the view persists across the poll-driven re-render. */
    #plan-scroll { flex: 1; overflow: hidden; position: relative; }
    #plan-svg { display: block; width: 100%; height: 100%; cursor: grab; touch-action: none; }
    #plan-svg.pw-panning, #plan-svg.pw-panning .clickable { cursor: grabbing; }
    #plan-svg text { font-family: var(--nm-font); }
    /* Only the node label is themed to the fg color; the status/kind tags keep their own fill. A
       CSS rule is required because SVG presentation attributes can't resolve var(). */
    #plan-svg text.pw-node-label { fill: var(--nm-fg); }
    #plan-svg .clickable { cursor: pointer; }
    .pw-fit-btn { font-size: 0.85em; padding: 1px 8px; margin-left: auto; flex-shrink: 0; }
    .pw-plan-empty { opacity: 0.6; padding: 24px; text-align: center; }
    #plan-detail {
      border-top: 1px solid var(--nm-border); max-height: 420px; overflow-y: auto;
      flex-shrink: 0; font-size: 0.82em; padding: 8px 14px;
    }
    #plan-detail-header {
      display: flex; justify-content: space-between; align-items: center;
      font-weight: 600; margin-bottom: 6px;
    }
    #plan-detail-close { background: transparent; padding: 0 6px; font-size: 0.9em; }
    .pw-meta-row { margin: 3px 0; }
    .pw-meta-label { opacity: 0.55; margin-right: 6px; }
    .pw-chip {
      display: inline-block; border-radius: 9px; padding: 0 8px; margin: 2px 4px 2px 0;
      font-size: 0.85em; background: rgba(127,127,127,0.15);
    }
    .pw-goal-text {
      white-space: pre-wrap; background: rgba(127,127,127,0.12);
      border-radius: 3px; padding: 6px; margin-top: 4px;
    }
    .pw-plan-section { margin-top: 12px; padding-top: 10px; border-top: 1px solid var(--nm-border); }
    .pw-plan-section h3 {
      font-size: 0.78em; font-weight: 700; text-transform: uppercase;
      letter-spacing: 0.07em; opacity: 0.5; margin: 0 0 6px;
    }
`;

const DAG_REPLAY_HTML = `
  <div id="toolbar">
    <span class="toolbar-title">Pathways</span>
    <span id="status-dot" class="status-dot"></span>
    <span id="status-text">idle</span>
    <span id="node-count"></span>
    <select id="dag-session-override" style="font-size:0.75em;padding:1px 4px;border:1px solid var(--nm-border);border-radius:3px;background:var(--vscode-input-background,#3c3c3c);color:var(--vscode-input-foreground,#ccc);max-width:150px;margin-left:auto;"><option value="">Follow Workspace</option></select>
  </div>
  <div class="pw-tabs">
    <button class="pw-tab pw-tab-active" data-pane="trajectory">Trajectory</button>
    <button class="pw-tab" data-pane="plan" title="Read-only view of this session's plan decomposition">Plan</button>
  </div>
  <div id="pw-pane-trajectory" class="pw-pane">
  <div style="padding: 4px 14px; font-size: 0.8em; opacity: 0.55; border-bottom: 1px solid var(--nm-border); flex-shrink: 0; display: flex; justify-content: space-between; align-items: center; gap: 8px;">
    <span>Workspace history — goals started, integrations, rejections, and external file updates. Not per-agent reasoning steps.</span>
    <button id="btn-sync-now" class="ghost" style="font-size:0.85em;padding:2px 8px;flex-shrink:0;" title="Check the working repository for external changes now, instead of waiting for the next goal creation">Sync now</button>
    <select id="replay-mode" style="font-size:0.8em;padding:2px 6px;border:1px solid var(--nm-border);border-radius:3px;background:var(--vscode-input-background,#3c3c3c);color:var(--vscode-input-foreground,#ccc);">
      <option value="linear">Linear</option>
      <option value="branchexplorer">Branch Explorer</option>
      <option value="counterfactual">Counterfactual</option>
    </select>
  </div>
  <div style="display:flex;gap:14px;align-items:center;padding:3px 14px;font-size:0.72em;opacity:0.6;border-bottom:1px solid var(--nm-border);flex-shrink:0;">
    <span><svg width="10" height="10" viewBox="0 0 10 10" style="vertical-align:-1px"><circle cx="5" cy="5" r="4" fill="#3794ff"/></svg> Goal</span>
    <span><svg width="10" height="10" viewBox="0 0 10 10" style="vertical-align:-1px"><path d="M5 0 L10 5 L5 10 L0 5 Z" fill="#4dac26"/></svg> Integration</span>
    <span><svg width="10" height="10" viewBox="0 0 10 10" style="vertical-align:-1px"><circle cx="5" cy="5" r="4" fill="#f14c4c"/><path d="M3 3 L7 7 M3 7 L7 3" stroke="#1e1e1e" stroke-width="1.4"/></svg> Rejection</span>
    <span><svg width="10" height="10" viewBox="0 0 10 10" style="vertical-align:-1px"><path d="M5 0 L10 5 L5 10 L0 5 Z" fill="#8a8a8a"/></svg> Superseded</span>
    <span><svg width="10" height="10" viewBox="0 0 10 10" style="vertical-align:-1px"><circle cx="5" cy="5" r="4" fill="#7a3333"/><path d="M3 3 L7 7 M3 7 L7 3" stroke="#1e1e1e" stroke-width="1.4"/></svg> Dead branch</span>
    <span><svg width="10" height="10" viewBox="0 0 10 10" style="vertical-align:-1px"><rect x="1" y="1" width="8" height="8" fill="#cca700"/></svg> External update</span>
  </div>
  <div id="dag-scroll">
    <svg id="dag-svg"></svg>
    <div id="alternate-view" style="display:none;padding:12px;overflow-y:auto;"></div>
  </div>
  <div id="node-detail" class="hidden">
    <div id="node-detail-header">
      <span id="node-detail-title"></span>
      <button id="node-detail-close">✕</button>
    </div>
    <div id="node-detail-body"></div>
  </div>
  <div id="scrubber-row">
    <span id="scrub-branch"></span>
    <input type="range" id="scrubber" min="0" max="0" value="0" step="1">
    <span id="scrub-pos"></span>
  </div>
  <div id="playback-bar" class="hidden">
    <span>PLAYBACK</span>
    <button id="btn-live">▶ Live</button>
    <button id="btn-branch">⎇ Branch from here</button>
    <button id="btn-kgs">📌 Mark Known Good</button>
    <button id="btn-restore-kgs">↩ Restore Known Good</button>
  </div>
  </div>
  <div id="pw-pane-plan" class="pw-pane" style="display:none">
    <div class="pw-plan-legend">
      <span><svg width="11" height="11" viewBox="0 0 12 12" style="vertical-align:-1px"><rect x="1.5" y="1.5" width="9" height="9" rx="2" fill="#8a8a8a"/></svg> Leaf (worker)</span>
      <span><svg width="11" height="11" viewBox="0 0 12 12" style="vertical-align:-1px"><path d="M6 1 L11 6 L6 11 L1 6 Z" fill="#8a8a8a"/></svg> Compound (sub-planner)</span>
      <span style="opacity:0.85"><svg width="20" height="8" style="vertical-align:-1px"><line x1="0" y1="4" x2="20" y2="4" stroke="#8a8a8a" stroke-width="1.5"/></svg> decomposes</span>
      <span style="opacity:0.85"><svg width="20" height="8" style="vertical-align:-1px"><line x1="0" y1="4" x2="20" y2="4" stroke="#cca700" stroke-width="1.5" stroke-dasharray="3 2"/></svg> depends on</span>
      <span style="opacity:0.85"><svg width="20" height="8" style="vertical-align:-1px"><line x1="0" y1="4" x2="16" y2="4" stroke="#c586c0" stroke-width="1.5"/><path d="M16 1.5 L20 4 L16 6.5 Z" fill="#c586c0"/></svg> contract</span>
      <button id="plan-fit-btn" class="pw-fit-btn ghost" title="Fit the whole plan to view (drag to pan, scroll to zoom)">⤢ Fit</button>
    </div>
    <div id="plan-scroll">
      <svg id="plan-svg"></svg>
      <div id="plan-empty" class="pw-plan-empty hidden"></div>
    </div>
    <div id="plan-detail" class="hidden">
      <div id="plan-detail-header">
        <span id="plan-detail-title"></span>
        <button id="plan-detail-close">✕</button>
      </div>
      <div id="plan-detail-body"></div>
    </div>
  </div>
`;
