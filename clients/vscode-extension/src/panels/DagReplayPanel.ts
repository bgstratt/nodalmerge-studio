import * as vscode from 'vscode';
import { scopeViewCss } from './sharedWebviewChrome';

const POLL_INTERVAL_MS = 2_000;

interface WorkUnit {
  workUnitId: string;
  branchId: string;
  goal: string;
  owner: string;
  status: string;
  currentStage?: string | null;
}

export class DagReplayPanel {
  static readonly containerId = 'shell-pane-dag-replay';

  private readonly panel: vscode.WebviewPanel;
  private readonly baseUrl: string;
  private pollTimer?: ReturnType<typeof setInterval>;

  constructor(panel: vscode.WebviewPanel, baseUrl: string) {
    this.panel = panel;
    this.baseUrl = baseUrl;
  }

  /** Called once by the shell right after construction — was the tail of createOrShow(). */
  activate(): void {
    void this.init();
    this.pollTimer = setInterval(() => { void this.refreshWorkUnits(); }, POLL_INTERVAL_MS);
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
    return {
      css: scopeViewCss(DAG_REPLAY_CSS, DagReplayPanel.containerId),
      html: `<div id="${DagReplayPanel.containerId}" class="nm-shell-pane">${DAG_REPLAY_HTML}</div>`,
      scriptTag: `<script nonce="${nonce}" src="${scriptUri}"></script>`,
    };
  }

  private async init(): Promise<void> {
    try {
      const workUnits = await this.get<WorkUnit[]>('/studio/workunits');
      const port = this.extractPort();
      void this.panel.webview.postMessage({
        type:      'init',
        port,
        roomId:    'studio-main',
        workUnits: workUnits.map(wu => ({
          workUnitId:   wu.workUnitId,
          branchId:     wu.branchId,
          goal:         wu.goal,
          currentStage: wu.currentStage ?? null,
        })),
      });
    } catch {
      // host not running or WU list empty — WebView shows idle state
    }
  }

  /** Polled every POLL_INTERVAL_MS so work units fanned out after the panel was opened (no
   * goal/stage entry in the webview's workUnitIdToBranchId map otherwise) become visible without
   * requiring the panel to be closed and reopened. Sends 'workUnits', not 'init' — main.ts merges
   * that into goals/stages/workUnitIdToBranchId without resetting replay state or reconnecting
   * the websocket. */
  private async refreshWorkUnits(): Promise<void> {
    try {
      const workUnits = await this.get<WorkUnit[]>('/studio/workunits');
      void this.panel.webview.postMessage({
        type: 'workUnits',
        workUnits: workUnits.map(wu => ({
          workUnitId:   wu.workUnitId,
          branchId:     wu.branchId,
          goal:         wu.goal,
          currentStage: wu.currentStage ?? null,
        })),
      });
    } catch {
      // host not running — same suppress-and-poll-later convention as init()
    }
  }

  async handleMessage(msg: Record<string, unknown>): Promise<void> {
    if (msg.type === 'branchFromCursor') {
      const goal = await vscode.window.showInputBox({
        prompt:         'Goal for branch from cursor',
        placeHolder:    'e.g. Experiment from this checkpoint',
        ignoreFocusOut: true,
      });
      if (!goal) { return; }
      try {
        const repositoryPath = vscode.workspace.workspaceFolders?.[0]?.uri?.fsPath;
        const wu = await this.post<WorkUnit>('/studio/workunits', { goal, owner: 'user', ...(repositoryPath ? { repositoryPath } : {}) });
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
    }
  }

  private extractPort(): number {
    const match = this.baseUrl.match(/:(\d+)/);
    return match ? parseInt(match[1], 10) : 5080;
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
`;

const DAG_REPLAY_HTML = `
  <div id="toolbar">
    <span class="toolbar-title">DAG Replay</span>
    <span id="status-dot" class="status-dot"></span>
    <span id="status-text">idle</span>
    <span id="node-count"></span>
  </div>
  <div id="dag-scroll">
    <svg id="dag-svg"></svg>
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
  </div>
`;
