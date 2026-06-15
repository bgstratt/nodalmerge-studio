"use strict";
var __create = Object.create;
var __defProp = Object.defineProperty;
var __getOwnPropDesc = Object.getOwnPropertyDescriptor;
var __getOwnPropNames = Object.getOwnPropertyNames;
var __getProtoOf = Object.getPrototypeOf;
var __hasOwnProp = Object.prototype.hasOwnProperty;
var __export = (target, all) => {
  for (var name in all)
    __defProp(target, name, { get: all[name], enumerable: true });
};
var __copyProps = (to, from, except, desc) => {
  if (from && typeof from === "object" || typeof from === "function") {
    for (let key of __getOwnPropNames(from))
      if (!__hasOwnProp.call(to, key) && key !== except)
        __defProp(to, key, { get: () => from[key], enumerable: !(desc = __getOwnPropDesc(from, key)) || desc.enumerable });
  }
  return to;
};
var __toESM = (mod, isNodeMode, target) => (target = mod != null ? __create(__getProtoOf(mod)) : {}, __copyProps(
  // If the importer is in node compatibility mode or this is not an ESM
  // file that has been converted to a CommonJS file using a Babel-
  // compatible transform (i.e. "__esModule" has not been set), then set
  // "default" to the CommonJS "module.exports" for node compatibility.
  isNodeMode || !mod || !mod.__esModule ? __defProp(target, "default", { value: mod, enumerable: true }) : target,
  mod
));
var __toCommonJS = (mod) => __copyProps(__defProp({}, "__esModule", { value: true }), mod);

// src/extension.ts
var extension_exports = {};
__export(extension_exports, {
  activate: () => activate,
  deactivate: () => deactivate
});
module.exports = __toCommonJS(extension_exports);
var vscode9 = __toESM(require("vscode"));

// src/HostManager.ts
var cp = __toESM(require("child_process"));
var http = __toESM(require("http"));
var path = __toESM(require("path"));
var vscode = __toESM(require("vscode"));

// src/constants.ts
var DEFAULT_HOST_PORT = 5080;
var HOST_STARTUP_TIMEOUT_MS = 15e3;
var HOST_HEALTH_POLL_INTERVAL_MS = 500;
var COMMANDS = {
  RESTART_HOST: "nodalmerge.restartHost",
  SHOW_OUTPUT: "nodalmerge.showOutput",
  OPEN_DASHBOARD: "nodalmerge.openDashboard",
  OPEN_MERGE_REVIEW: "nodalmerge.openMergeReview",
  OPEN_DAG_REPLAY: "nodalmerge.openDagReplay",
  OPEN_AGENT_CONFIG: "nodalmerge.openAgentConfig"
};
var HOST_BINARY_NAME = {
  win32: "NodalMerge.Studio.Host.exe",
  linux: "NodalMerge.Studio.Host",
  darwin: "NodalMerge.Studio.Host"
};
function getRid() {
  const { platform, arch } = process;
  if (platform === "win32" && arch === "x64") {
    return "win-x64";
  }
  if (platform === "win32" && arch === "arm64") {
    return "win-arm64";
  }
  if (platform === "linux" && arch === "x64") {
    return "linux-x64";
  }
  if (platform === "linux" && arch === "arm64") {
    return "linux-arm64";
  }
  if (platform === "darwin" && arch === "x64") {
    return "osx-x64";
  }
  if (platform === "darwin" && arch === "arm64") {
    return "osx-arm64";
  }
  throw new Error(`Unsupported platform: ${platform}/${arch}`);
}

// src/HostManager.ts
function sleep(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}
var HostManager = class {
  constructor(output, context) {
    this._ready = false;
    this.output = output;
    this.context = context;
    this.port = vscode.workspace.getConfiguration("nodalmerge").get("hostPort", DEFAULT_HOST_PORT);
    this.statusBar = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Left, 100);
    this.statusBar.command = COMMANDS.SHOW_OUTPUT;
    this.statusBar.show();
    this.applyStatus("idle");
  }
  get isReady() {
    return this._ready;
  }
  get hostPort() {
    return this.port;
  }
  get hostBaseUrl() {
    return `http://127.0.0.1:${this.port}`;
  }
  async start() {
    if (await this.checkHealth()) {
      this._ready = true;
      this.applyStatus("ready");
      this.output.appendLine(`[NodalMerge] Adopted running host on port ${this.port}.`);
      return;
    }
    this.applyStatus("starting");
    this.spawnProcess();
    await this.waitForHealth();
  }
  async restart() {
    this.killProcess();
    this._ready = false;
    await this.start();
  }
  spawnProcess() {
    const { cmd, args, env } = this.resolveHostCommand();
    this.output.appendLine(`[NodalMerge] Spawning: ${cmd} ${args.join(" ")}`);
    this.process = cp.spawn(cmd, args, {
      env: { ...process.env, ...env },
      stdio: ["ignore", "pipe", "pipe"],
      // On Windows, spawn without a window
      windowsHide: true
    });
    this.process.stdout?.on("data", (chunk) => {
      this.output.append(chunk.toString());
    });
    this.process.stderr?.on("data", (chunk) => {
      this.output.append(chunk.toString());
    });
    this.process.on("error", (err) => {
      this.output.appendLine(`[NodalMerge] Spawn error: ${err.message}`);
      this.applyStatus("error");
    });
    this.process.on("exit", (code, signal) => {
      this.output.appendLine(`[NodalMerge] Host exited \u2014 code=${code ?? "null"} signal=${signal ?? "none"}`);
      this._ready = false;
      this.process = void 0;
      this.applyStatus("stopped");
    });
  }
  resolveHostCommand() {
    const hostEnv = {
      Studio__Urls: `http://127.0.0.1:${this.port}`,
      ASPNETCORE_URLS: `http://127.0.0.1:${this.port}`
    };
    if (this.context.extensionMode === vscode.ExtensionMode.Development) {
      const repoRoot = path.join(this.context.extensionPath, "..", "..");
      const hostProject = path.join(
        repoRoot,
        "src",
        "NodalMerge.Studio.Host",
        "NodalMerge.Studio.Host.csproj"
      );
      this.output.appendLine(`[NodalMerge] Dev mode \u2014 dotnet run --project ${hostProject}`);
      return {
        cmd: "dotnet",
        args: ["run", "--project", hostProject, "--no-launch-profile"],
        env: hostEnv
      };
    }
    const rid = getRid();
    const binaryName = HOST_BINARY_NAME[process.platform] ?? "NodalMerge.Studio.Host";
    const binaryPath = path.join(this.context.extensionPath, "bin", rid, binaryName);
    return { cmd: binaryPath, args: [], env: hostEnv };
  }
  async waitForHealth() {
    const deadline = Date.now() + HOST_STARTUP_TIMEOUT_MS;
    while (Date.now() < deadline) {
      if (await this.checkHealth()) {
        this._ready = true;
        this.applyStatus("ready");
        this.output.appendLine(`[NodalMerge] Host healthy on port ${this.port}.`);
        return;
      }
      await sleep(HOST_HEALTH_POLL_INTERVAL_MS);
    }
    this.applyStatus("error");
    throw new Error(
      `NodalMerge Studio Host did not become healthy within ${HOST_STARTUP_TIMEOUT_MS / 1e3}s. Check the NodalMerge output channel for details.`
    );
  }
  checkHealth() {
    return new Promise((resolve) => {
      const req = http.get(
        `http://127.0.0.1:${this.port}/studio/health`,
        { timeout: 1e3 },
        (res) => resolve(res.statusCode === 200)
      );
      req.on("error", () => resolve(false));
      req.on("timeout", () => {
        req.destroy();
        resolve(false);
      });
    });
  }
  applyStatus(status) {
    switch (status) {
      case "idle":
        this.statusBar.text = "$(circle-outline) NodalMerge";
        this.statusBar.tooltip = "NodalMerge Studio \u2014 idle";
        this.statusBar.color = void 0;
        break;
      case "starting":
        this.statusBar.text = "$(loading~spin) NodalMerge";
        this.statusBar.tooltip = "NodalMerge Studio Host starting\u2026";
        this.statusBar.color = void 0;
        break;
      case "ready":
        this.statusBar.text = `$(check) NodalMerge :${this.port}`;
        this.statusBar.tooltip = `NodalMerge Studio Host running on port ${this.port}`;
        this.statusBar.color = new vscode.ThemeColor("statusBarItem.prominentForeground");
        break;
      case "stopped":
        this.statusBar.text = "$(debug-stop) NodalMerge";
        this.statusBar.tooltip = "NodalMerge Studio Host stopped \u2014 click to see output";
        this.statusBar.color = new vscode.ThemeColor("statusBarItem.warningForeground");
        break;
      case "error":
        this.statusBar.text = "$(error) NodalMerge";
        this.statusBar.tooltip = "NodalMerge Studio Host failed to start \u2014 click to see output";
        this.statusBar.color = new vscode.ThemeColor("statusBarItem.errorForeground");
        break;
    }
  }
  killProcess() {
    if (!this.process) {
      return;
    }
    this.output.appendLine("[NodalMerge] Stopping host\u2026");
    this.process.kill("SIGTERM");
    const proc = this.process;
    setTimeout(() => {
      if (!proc.exitCode && !proc.killed) {
        proc.kill("SIGKILL");
      }
    }, 3e3);
    this.process = void 0;
  }
  dispose() {
    this.killProcess();
    this.statusBar.dispose();
  }
};

// src/panels/WorkspaceDashboardPanel.ts
var vscode2 = __toESM(require("vscode"));
var POLL_INTERVAL_MS = 2e3;
var WorkspaceDashboardPanel = class _WorkspaceDashboardPanel {
  constructor(panel, baseUrl, notifications, configService) {
    this.disposables = [];
    this.panel = panel;
    this.baseUrl = baseUrl;
    this.notifications = notifications;
    this.configService = configService;
    this.panel.webview.html = buildDashboardHtml();
    this.panel.onDidDispose(() => this.dispose(), null, this.disposables);
    this.panel.onDidChangeViewState((e) => {
      if (e.webviewPanel.visible) {
        this.startPolling();
      } else {
        this.stopPolling();
      }
    }, null, this.disposables);
    this.panel.webview.onDidReceiveMessage(
      (msg) => {
        void this.handleMessage(msg);
      },
      null,
      this.disposables
    );
    this.startPolling();
  }
  static {
    this.viewType = "nodalmerge.dashboard";
  }
  static createOrShow(baseUrl, notifications, configService) {
    if (_WorkspaceDashboardPanel.current) {
      _WorkspaceDashboardPanel.current.panel.reveal(vscode2.ViewColumn.Two);
      return;
    }
    const panel = vscode2.window.createWebviewPanel(
      _WorkspaceDashboardPanel.viewType,
      "NodalMerge \u2014 Workspace",
      vscode2.ViewColumn.Two,
      { enableScripts: true, retainContextWhenHidden: true }
    );
    _WorkspaceDashboardPanel.current = new _WorkspaceDashboardPanel(panel, baseUrl, notifications, configService);
  }
  startPolling() {
    if (this.pollTimer) {
      return;
    }
    void this.poll();
    this.pollTimer = setInterval(() => {
      void this.poll();
    }, POLL_INTERVAL_MS);
  }
  stopPolling() {
    if (this.pollTimer) {
      clearInterval(this.pollTimer);
      this.pollTimer = void 0;
    }
  }
  async poll() {
    try {
      const [summary, workUnits, agents, merges] = await Promise.all([
        this.get("/studio/workspace-summary"),
        this.get("/studio/workunits"),
        this.get("/studio/agents"),
        this.get("/studio/merges")
      ]);
      void this.panel.webview.postMessage({ type: "data", summary, workUnits, agents, merges });
      this.notifications?.update(merges);
    } catch {
    }
  }
  async handleMessage(msg) {
    try {
      switch (msg.type) {
        case "createWorkUnit": {
          const goal = await vscode2.window.showInputBox({
            prompt: "Work unit goal",
            placeHolder: "e.g. Build the NodalMerge docs site",
            ignoreFocusOut: true
          });
          if (!goal) {
            return;
          }
          const owner = await vscode2.window.showInputBox({
            prompt: "Owner (agent type or name)",
            placeHolder: "orchestrator",
            ignoreFocusOut: true
          });
          if (!owner) {
            return;
          }
          await this.post("/studio/workunits", { goal, owner });
          void this.poll();
          break;
        }
        case "spawnAgent": {
          const prefilledWuId = msg.workUnitId;
          let agentType;
          if (this.configService) {
            const profile = await this.configService.pickProfile("Select agent profile to spawn");
            agentType = profile?.id;
          } else {
            agentType = await vscode2.window.showInputBox({
              prompt: "Agent type",
              placeHolder: "orchestrator, worker, docs-agent\u2026",
              ignoreFocusOut: true
            });
          }
          if (!agentType) {
            return;
          }
          const workUnitId = prefilledWuId ?? await vscode2.window.showInputBox({ prompt: "Work Unit ID", ignoreFocusOut: true }) ?? "";
          if (!workUnitId) {
            return;
          }
          await this.post("/studio/agents/spawn", { agentType, workUnitId });
          void this.poll();
          break;
        }
        case "pauseAgent":
          await this.post("/studio/agents/" + String(msg.agentId) + "/pause", {});
          void this.poll();
          break;
        case "resumeAgent":
          await this.post("/studio/agents/" + String(msg.agentId) + "/resume", {});
          void this.poll();
          break;
        case "stopAgent":
          await this.post("/studio/agents/" + String(msg.agentId) + "/stop", {});
          void this.poll();
          break;
        case "openMergeReview":
          void vscode2.commands.executeCommand("nodalmerge.openMergeReview", msg.proposalId);
          break;
      }
    } catch (err) {
      void vscode2.window.showErrorMessage("NodalMerge: " + String(err));
    }
  }
  async get(path2) {
    const res = await fetch(this.baseUrl + path2);
    if (!res.ok) {
      throw new Error("GET " + path2 + " \u2192 " + String(res.status));
    }
    return res.json();
  }
  async post(path2, body) {
    const res = await fetch(this.baseUrl + path2, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(body)
    });
    if (!res.ok) {
      const text = await res.text();
      throw new Error("POST " + path2 + " \u2192 " + String(res.status) + ": " + text);
    }
    return res.json();
  }
  dispose() {
    this.stopPolling();
    _WorkspaceDashboardPanel.current = void 0;
    this.panel.dispose();
    for (const d of this.disposables) {
      d.dispose();
    }
    this.disposables.length = 0;
  }
};
function buildNonce() {
  let text = "";
  const chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
  for (let i = 0; i < 32; i++) {
    text += chars[Math.floor(Math.random() * chars.length)];
  }
  return text;
}
function buildDashboardHtml() {
  const n = buildNonce();
  return [
    "<!DOCTYPE html>",
    '<html lang="en">',
    "<head>",
    '  <meta charset="UTF-8">',
    '  <meta http-equiv="Content-Security-Policy"',
    `        content="default-src 'none'; style-src 'nonce-` + n + "'; script-src 'nonce-" + n + `';">`,
    '  <meta name="viewport" content="width=device-width, initial-scale=1.0">',
    "  <title>NodalMerge Studio</title>",
    '  <style nonce="' + n + '">',
    DASHBOARD_CSS,
    "  </style>",
    "</head>",
    "<body>",
    DASHBOARD_HTML,
    '<script nonce="' + n + '">',
    DASHBOARD_JS,
    "</script>",
    "</body>",
    "</html>"
  ].join("\n");
}
var DASHBOARD_CSS = `
  :root {
    --nm-bg:         var(--vscode-editor-background);
    --nm-fg:         var(--vscode-editor-foreground);
    --nm-border:     var(--vscode-widget-border, #444);
    --nm-section-bg: var(--vscode-sideBar-background, var(--vscode-editor-background));
    --nm-btn:        var(--vscode-button-background);
    --nm-btn-fg:     var(--vscode-button-foreground);
    --nm-btn-hover:  var(--vscode-button-hoverBackground);
    --nm-badge:      var(--vscode-badge-background);
    --nm-badge-fg:   var(--vscode-badge-foreground);
    --nm-font:       var(--vscode-font-family);
    --nm-mono:       var(--vscode-editor-font-family, monospace);
    --nm-size:       var(--vscode-font-size, 13px);
    --nm-success:    #4dac26;
    --nm-warn:       #cca700;
    --nm-error:      #f14c4c;
  }
  * { box-sizing: border-box; }
  body {
    background: var(--nm-bg);
    color: var(--nm-fg);
    font-family: var(--nm-font);
    font-size: var(--nm-size);
    margin: 0;
    padding: 0 16px 32px;
  }
  h2 {
    font-size: 0.8em;
    font-weight: 700;
    text-transform: uppercase;
    letter-spacing: 0.08em;
    opacity: 0.55;
    margin: 24px 0 8px;
    border-bottom: 1px solid var(--nm-border);
    padding-bottom: 4px;
  }
  .header {
    display: flex;
    align-items: center;
    justify-content: space-between;
    padding: 14px 0 4px;
    border-bottom: 1px solid var(--nm-border);
    margin-bottom: 4px;
  }
  .header-title { font-size: 1.15em; font-weight: 700; }
  .pulse {
    display: inline-block;
    width: 7px; height: 7px; border-radius: 50%;
    background: var(--nm-success);
    margin-left: 7px;
    vertical-align: middle;
    animation: pulse 2s ease-in-out infinite;
  }
  @keyframes pulse { 0%,100%{opacity:1} 50%{opacity:0.25} }
  #last-updated { font-size: 0.76em; opacity: 0.38; }
  .empty { opacity: 0.42; font-style: italic; padding: 4px 0 2px; font-size: 0.9em; }
  .card {
    background: var(--nm-section-bg);
    border: 1px solid var(--nm-border);
    border-radius: 4px;
    padding: 9px 12px;
    margin-bottom: 5px;
  }
  .row { display: flex; align-items: center; gap: 8px; flex-wrap: wrap; }
  .row + .row { margin-top: 4px; }
  .title {
    font-weight: 600;
    flex: 1;
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }
  .mono { font-family: var(--nm-mono); font-size: 0.85em; opacity: 0.65; }
  .badge {
    border-radius: 10px;
    padding: 1px 8px;
    font-size: 0.76em;
    white-space: nowrap;
    background: var(--nm-badge);
    color: var(--nm-badge-fg);
  }
  .badge.active   { background: var(--nm-success); color: #fff; }
  .badge.paused   { background: var(--nm-warn); color: #000; }
  .badge.stopped,
  .badge.completed { background: #555; color: #ccc; }
  .badge.failed   { background: var(--nm-error); color: #fff; }
  .actions { display: flex; gap: 4px; flex-shrink: 0; }
  button {
    background: var(--nm-btn);
    color: var(--nm-btn-fg);
    border: none;
    border-radius: 3px;
    padding: 2px 9px;
    font-size: 0.8em;
    cursor: pointer;
    font-family: var(--nm-font);
    line-height: 1.6;
  }
  button:hover { background: var(--nm-btn-hover); }
  button.ghost {
    background: transparent;
    color: var(--nm-fg);
    border: 1px solid var(--nm-border);
  }
  button.ghost:hover { background: color-mix(in srgb, var(--nm-border) 60%, transparent); }
  button.danger { background: var(--nm-error); color: #fff; }
  button.danger:hover { filter: brightness(1.15); }
  .add-btn {
    margin-top: 6px;
    width: 100%;
    background: transparent;
    color: var(--nm-fg);
    border: 1px dashed var(--nm-border);
    padding: 5px;
    font-size: 0.82em;
    opacity: 0.6;
    border-radius: 3px;
  }
  .add-btn:hover { opacity: 1; }
`;
var DASHBOARD_HTML = `
  <div class="header">
    <span class="header-title">NodalMerge Studio<span class="pulse"></span></span>
    <span id="last-updated"></span>
  </div>

  <h2>Work Units</h2>
  <div id="work-units"><p class="empty">Loading\u2026</p></div>
  <button class="add-btn" id="btn-new-wu">+ New Work Unit</button>

  <h2>Active Agents</h2>
  <div id="agents"><p class="empty">No active agents.</p></div>
  <button class="add-btn" id="btn-spawn">+ Spawn Agent</button>

  <h2>Pending Merges</h2>
  <div id="merges"><p class="empty">No pending merges.</p></div>

  <h2>Failures</h2>
  <div id="failures"><p class="empty">No failures.</p></div>
`;
var DASHBOARD_JS = `
  const vscode = acquireVsCodeApi();

  document.getElementById('btn-new-wu').addEventListener('click', function() {
    vscode.postMessage({ type: 'createWorkUnit' });
  });
  document.getElementById('btn-spawn').addEventListener('click', function() {
    vscode.postMessage({ type: 'spawnAgent' });
  });

  function esc(str) {
    return String(str || '')
      .replace(/&/g, '&amp;')
      .replace(/</g, '&lt;')
      .replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;')
      .replace(/'/g, '&#39;');
  }

  function badge(status) {
    var s = (status || '').toLowerCase();
    return '<span class="badge ' + s + '">' + esc(status || '\u2014') + '</span>';
  }

  function renderWorkUnits(wus) {
    var el = document.getElementById('work-units');
    if (!wus || !wus.length) {
      el.innerHTML = '<p class="empty">No work units yet.</p>';
      return;
    }
    var html = '';
    for (var i = 0; i < wus.length; i++) {
      var wu = wus[i];
      html += '<div class="card">';
      html += '<div class="row">';
      html += '<span class="title" title="' + esc(wu.goal) + '">' + esc(wu.goal) + '</span>';
      html += badge(wu.status);
      html += '<div class="actions">';
      html += '<button class="ghost" data-action="spawnAgent" data-wu="' + esc(wu.workUnitId) + '">Spawn</button>';
      html += '</div>';
      html += '</div>';
      html += '<div class="row">';
      html += '<span class="mono">' + esc(wu.workUnitId) + '</span>';
      html += '<span class="mono">branch: ' + esc(wu.branchId) + '</span>';
      html += '<span class="mono">owner: ' + esc(wu.owner) + '</span>';
      html += '</div>';
      html += '</div>';
    }
    el.innerHTML = html;
    el.querySelectorAll('[data-action="spawnAgent"]').forEach(function(btn) {
      btn.addEventListener('click', function() {
        vscode.postMessage({ type: 'spawnAgent', workUnitId: btn.getAttribute('data-wu') });
      });
    });
  }

  function renderAgents(agents, wus) {
    var el = document.getElementById('agents');
    if (!agents || !agents.length) {
      el.innerHTML = '<p class="empty">No active agents.</p>';
      return;
    }
    var wuMap = {};
    for (var j = 0; j < (wus || []).length; j++) { wuMap[wus[j].workUnitId] = wus[j]; }
    var html = '';
    for (var i = 0; i < agents.length; i++) {
      var a = agents[i];
      var wu = wuMap[a.workUnitId];
      var isPaused = (a.status || '').toLowerCase() === 'paused';
      html += '<div class="card">';
      html += '<div class="row">';
      html += '<span class="title mono">' + esc(a.agentId) + '</span>';
      html += badge(a.status);
      html += '<div class="actions">';
      if (isPaused) {
        html += '<button class="ghost" data-action="resumeAgent" data-id="' + esc(a.agentId) + '">Resume</button>';
      } else {
        html += '<button class="ghost" data-action="pauseAgent" data-id="' + esc(a.agentId) + '">Pause</button>';
      }
      html += '<button class="danger" data-action="stopAgent" data-id="' + esc(a.agentId) + '">Stop</button>';
      html += '</div>';
      html += '</div>';
      if (wu) {
        html += '<div class="row"><span class="mono">' + esc(wu.goal) + '</span></div>';
      }
      html += '</div>';
    }
    el.innerHTML = html;
    el.querySelectorAll('[data-action]').forEach(function(btn) {
      btn.addEventListener('click', function() {
        vscode.postMessage({ type: btn.getAttribute('data-action'), agentId: btn.getAttribute('data-id') });
      });
    });
  }

  var MERGE_STATUS_COLOR = {
    draft:          '',
    readyforreview: 'active',
    approved:       'active',
    rejected:       'failed',
    merged:         'stopped',
  };

  function renderMerges(merges) {
    var el = document.getElementById('merges');
    if (!merges || !merges.length) {
      el.innerHTML = '<p class="empty">No merge proposals.</p>';
      return;
    }
    var html = '';
    for (var i = 0; i < merges.length; i++) {
      var m = merges[i];
      var statusKey = (m.status || '').toLowerCase().replace(/\\s+/g, '');
      var badgeClass = 'badge ' + (MERGE_STATUS_COLOR[statusKey] || '');
      var canReview = statusKey === 'readyforreview' || statusKey === 'approved' || statusKey === 'draft';
      html += '<div class="card">';
      html += '<div class="row">';
      html += '<span class="title" title="' + esc(m.goal) + '">' + esc(m.goal) + '</span>';
      html += '<span class="' + badgeClass + '">' + esc(m.status) + '</span>';
      if (canReview) {
        html += '<div class="actions">';
        html += '<button class="ghost" data-action="openMergeReview" data-pid="' + esc(m.proposalId) + '">Review \u2192</button>';
        html += '</div>';
      }
      html += '</div>';
      html += '<div class="row">';
      html += '<span class="mono">' + esc(m.sourceBranch) + ' \u2192 ' + esc(m.targetBranch) + '</span>';
      html += '</div>';
      html += '</div>';
    }
    el.innerHTML = html;
    el.querySelectorAll('[data-action="openMergeReview"]').forEach(function(btn) {
      btn.addEventListener('click', function() {
        vscode.postMessage({ type: 'openMergeReview', proposalId: btn.getAttribute('data-pid') });
      });
    });
  }

  function renderFailures(failures) {
    var el = document.getElementById('failures');
    if (!failures || !failures.length) {
      el.innerHTML = '<p class="empty">No failures.</p>';
      return;
    }
    var html = '';
    for (var i = 0; i < failures.length; i++) {
      html += '<div class="card"><div class="row"><span class="mono title">' + esc(failures[i]) + '</span></div></div>';
    }
    el.innerHTML = html;
  }

  window.addEventListener('message', function(event) {
    var msg = event.data;
    if (msg.type !== 'data') { return; }
    renderWorkUnits(msg.workUnits);
    renderAgents(msg.agents, msg.workUnits);
    renderMerges(msg.merges);
    renderFailures(msg.summary.failures);
    var ts = document.getElementById('last-updated');
    if (ts) { ts.textContent = 'updated ' + new Date().toLocaleTimeString(); }
  });
`;

// src/panels/MergeReviewPanel.ts
var vscode3 = __toESM(require("vscode"));
var MergeReviewPanel = class _MergeReviewPanel {
  constructor(panel, baseUrl, proposalId) {
    this.disposables = [];
    this.panel = panel;
    this.baseUrl = baseUrl;
    this.proposalId = proposalId;
    this.panel.webview.html = buildHtml();
    this.panel.onDidDispose(() => this.dispose(), null, this.disposables);
    this.panel.webview.onDidReceiveMessage(
      (msg) => {
        void this.handleMessage(msg);
      },
      null,
      this.disposables
    );
    void this.load();
  }
  static {
    this.viewType = "nodalmerge.mergeReview";
  }
  static createOrShow(baseUrl, proposalId) {
    if (_MergeReviewPanel.current) {
      _MergeReviewPanel.current.proposalId = proposalId;
      _MergeReviewPanel.current.panel.reveal(vscode3.ViewColumn.Two);
      void _MergeReviewPanel.current.load();
      return;
    }
    const panel = vscode3.window.createWebviewPanel(
      _MergeReviewPanel.viewType,
      "Merge Review",
      vscode3.ViewColumn.Two,
      { enableScripts: true, retainContextWhenHidden: true }
    );
    _MergeReviewPanel.current = new _MergeReviewPanel(panel, baseUrl, proposalId);
  }
  async load() {
    try {
      const proposal = await this.get("/studio/merges/" + this.proposalId);
      this.panel.title = "Merge Review: " + proposal.sourceBranch;
      void this.panel.webview.postMessage({ type: "proposal", proposal });
    } catch (err) {
      void vscode3.window.showErrorMessage("NodalMerge: failed to load proposal \u2014 " + String(err));
    }
  }
  async handleMessage(msg) {
    try {
      switch (msg.type) {
        case "validate":
          await this.post("/studio/merges/" + this.proposalId + "/validate", {});
          break;
        case "approve":
          await this.post("/studio/merges/" + this.proposalId + "/review", { decision: "Approved" });
          void vscode3.window.showInformationMessage("Merge proposal approved.");
          break;
        case "reject":
          await this.post("/studio/merges/" + this.proposalId + "/review", { decision: "Rejected" });
          void vscode3.window.showWarningMessage("Merge proposal rejected.");
          break;
        case "apply":
          await this.post("/studio/merges/" + this.proposalId + "/apply", {});
          void vscode3.window.showInformationMessage("Merge applied successfully.");
          break;
      }
      await this.load();
    } catch (err) {
      void vscode3.window.showWarningMessage("NodalMerge: " + String(err));
    }
  }
  async get(path2) {
    const res = await fetch(this.baseUrl + path2);
    if (!res.ok) {
      const text = await res.text();
      throw new Error("GET " + path2 + " \u2192 " + String(res.status) + ": " + text);
    }
    return res.json();
  }
  async post(path2, body) {
    const res = await fetch(this.baseUrl + path2, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(body)
    });
    if (!res.ok) {
      const text = await res.text();
      throw new Error("POST " + path2 + " \u2192 " + String(res.status) + ": " + text);
    }
    return res.json();
  }
  dispose() {
    _MergeReviewPanel.current = void 0;
    this.panel.dispose();
    for (const d of this.disposables) {
      d.dispose();
    }
    this.disposables.length = 0;
  }
};
function buildNonce2() {
  let s = "";
  const c = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
  for (let i = 0; i < 32; i++) {
    s += c[Math.floor(Math.random() * c.length)];
  }
  return s;
}
function buildHtml() {
  const n = buildNonce2();
  return [
    '<!DOCTYPE html><html lang="en"><head>',
    '<meta charset="UTF-8">',
    '<meta http-equiv="Content-Security-Policy"',
    `      content="default-src 'none'; style-src 'nonce-` + n + "'; script-src 'nonce-" + n + `';">`,
    '<meta name="viewport" content="width=device-width, initial-scale=1.0">',
    "<title>Merge Review</title>",
    '<style nonce="' + n + '">' + REVIEW_CSS + "</style>",
    "</head><body>",
    REVIEW_HTML,
    '<script nonce="' + n + '">' + REVIEW_JS + "</script>",
    "</body></html>"
  ].join("\n");
}
var REVIEW_CSS = `
  :root {
    --nm-bg:      var(--vscode-editor-background);
    --nm-fg:      var(--vscode-editor-foreground);
    --nm-border:  var(--vscode-widget-border, #444);
    --nm-font:    var(--vscode-font-family);
    --nm-mono:    var(--vscode-editor-font-family, monospace);
    --nm-size:    var(--vscode-font-size, 13px);
    --nm-btn:     var(--vscode-button-background);
    --nm-btn-fg:  var(--vscode-button-foreground);
    --nm-btn-h:   var(--vscode-button-hoverBackground);
    --nm-success: #4dac26;
    --nm-warn:    #cca700;
    --nm-error:   #f14c4c;
    --nm-info:    var(--vscode-textLink-foreground, #3794ff);
  }
  * { box-sizing: border-box; }
  body {
    background: var(--nm-bg); color: var(--nm-fg);
    font-family: var(--nm-font); font-size: var(--nm-size);
    margin: 0; padding: 0 20px 40px;
  }
  h1 { font-size: 1.1em; font-weight: 700; margin: 18px 0 6px; }
  .meta-grid {
    display: grid;
    grid-template-columns: max-content 1fr;
    gap: 4px 12px;
    margin: 12px 0;
  }
  .meta-label { opacity: 0.55; font-size: 0.85em; }
  .meta-value { font-family: var(--nm-mono); font-size: 0.9em; }
  .badge {
    display: inline-block;
    border-radius: 10px; padding: 1px 9px;
    font-size: 0.78em; white-space: nowrap;
    background: var(--vscode-badge-background);
    color: var(--vscode-badge-foreground);
  }
  .badge.draft           { background: #555; color: #ccc; }
  .badge.readyforreview  { background: var(--nm-info); color: #fff; }
  .badge.approved        { background: var(--nm-success); color: #fff; }
  .badge.rejected        { background: var(--nm-error); color: #fff; }
  .badge.merged          { background: #7c4dff; color: #fff; }
  section {
    border: 1px solid var(--nm-border);
    border-radius: 4px;
    padding: 10px 14px;
    margin: 10px 0;
  }
  section h2 {
    font-size: 0.78em; font-weight: 700;
    text-transform: uppercase; letter-spacing: 0.07em;
    opacity: 0.5; margin: 0 0 6px;
  }
  p { margin: 4px 0; line-height: 1.5; }
  .actions {
    display: flex; gap: 8px; flex-wrap: wrap;
    margin-top: 20px; padding-top: 14px;
    border-top: 1px solid var(--nm-border);
  }
  button {
    background: var(--nm-btn); color: var(--nm-btn-fg);
    border: none; border-radius: 3px;
    padding: 5px 14px; font-size: 0.88em;
    cursor: pointer; font-family: var(--nm-font);
  }
  button:hover:not(:disabled) { background: var(--nm-btn-h); }
  button:disabled { opacity: 0.35; cursor: not-allowed; }
  button.approve { background: var(--nm-success); color: #fff; }
  button.approve:hover:not(:disabled) { filter: brightness(1.15); }
  button.reject  { background: var(--nm-error);   color: #fff; }
  button.reject:hover:not(:disabled)  { filter: brightness(1.15); }
  button.apply   { background: #7c4dff; color: #fff; }
  button.apply:hover:not(:disabled)   { filter: brightness(1.15); }
  #loading { opacity: 0.45; padding: 20px 0; }
`;
var REVIEW_HTML = `
  <div id="loading">Loading proposal\u2026</div>
  <div id="content" style="display:none">
    <h1 id="title">Merge Review</h1>
    <div class="meta-grid">
      <span class="meta-label">Status</span>      <span id="status-badge"></span>
      <span class="meta-label">Source branch</span><span class="meta-value" id="source-branch"></span>
      <span class="meta-label">Target branch</span><span class="meta-value" id="target-branch"></span>
      <span class="meta-label">Confidence</span>  <span class="meta-value" id="confidence"></span>
    </div>
    <section>
      <h2>Goal</h2>
      <p id="goal"></p>
    </section>
    <section>
      <h2>Summary</h2>
      <p id="summary"></p>
    </section>
    <section id="section-change">
      <h2>Change description</h2>
      <p id="change-description"></p>
    </section>
    <section id="section-verification" style="display:none">
      <h2>Verification results</h2>
      <p id="verification-results"></p>
    </section>
    <section id="section-rollback" style="display:none">
      <h2>Rollback plan</h2>
      <p id="rollback-plan"></p>
    </section>
    <div class="actions">
      <button id="btn-validate">Validate</button>
      <button id="btn-approve" class="approve">Approve</button>
      <button id="btn-reject"  class="reject">Reject</button>
      <button id="btn-apply"   class="apply">Apply</button>
    </div>
  </div>
`;
var REVIEW_JS = `
  var vscode = acquireVsCodeApi();

  var STATUS_BUTTONS = {
    draft:          { validate: true,  approve: false, reject: false, apply: false },
    readyforreview: { validate: false, approve: true,  reject: true,  apply: false },
    approved:       { validate: false, approve: false, reject: false, apply: true  },
    merged:         { validate: false, approve: false, reject: false, apply: false },
    rejected:       { validate: false, approve: false, reject: false, apply: false },
  };

  function esc(s) {
    return String(s || '').replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;');
  }

  function setText(id, val) {
    var el = document.getElementById(id);
    if (el) el.textContent = val || '';
  }

  function setHtml(id, html) {
    var el = document.getElementById(id);
    if (el) el.innerHTML = html;
  }

  function showIf(id, cond) {
    var el = document.getElementById(id);
    if (el) el.style.display = cond ? '' : 'none';
  }

  function setDisabled(id, disabled) {
    var el = document.getElementById(id);
    if (el) el.disabled = disabled;
  }

  document.getElementById('btn-validate').addEventListener('click', function() {
    vscode.postMessage({ type: 'validate' });
  });
  document.getElementById('btn-approve').addEventListener('click', function() {
    vscode.postMessage({ type: 'approve' });
  });
  document.getElementById('btn-reject').addEventListener('click', function() {
    vscode.postMessage({ type: 'reject' });
  });
  document.getElementById('btn-apply').addEventListener('click', function() {
    vscode.postMessage({ type: 'apply' });
  });

  window.addEventListener('message', function(event) {
    var msg = event.data;
    if (msg.type !== 'proposal') { return; }
    var p = msg.proposal;
    var status = (p.status || '').toLowerCase().replace(/\\s+/g, '');

    document.getElementById('loading').style.display = 'none';
    document.getElementById('content').style.display = '';

    setText('title', 'Merge Review: ' + (p.sourceBranch || ''));
    var badgeClass = 'badge ' + status;
    setHtml('status-badge', '<span class="' + badgeClass + '">' + esc(p.status) + '</span>');
    setText('source-branch', p.sourceBranch);
    setText('target-branch', p.targetBranch);
    setText('confidence', p.confidence != null ? (Math.round(p.confidence * 100) + '%') : '\u2014');
    setText('goal', p.goal);
    setText('summary', p.summary);
    setText('change-description', p.changeDescription);
    setText('verification-results', p.verificationResults);
    setText('rollback-plan', p.rollbackPlan);

    showIf('section-verification', !!p.verificationResults);
    showIf('section-rollback', !!p.rollbackPlan);

    var btns = STATUS_BUTTONS[status] || { validate: false, approve: false, reject: false, apply: false };
    setDisabled('btn-validate', !btns.validate);
    setDisabled('btn-approve',  !btns.approve);
    setDisabled('btn-reject',   !btns.reject);
    setDisabled('btn-apply',    !btns.apply);
  });
`;

// src/panels/DagReplayPanel.ts
var vscode4 = __toESM(require("vscode"));
var DagReplayPanel = class _DagReplayPanel {
  constructor(panel, baseUrl, extensionUri) {
    this.disposables = [];
    this.panel = panel;
    this.baseUrl = baseUrl;
    this.extensionUri = extensionUri;
    this.panel.webview.html = this.buildHtml();
    this.panel.onDidDispose(() => this.dispose(), null, this.disposables);
    this.panel.webview.onDidReceiveMessage(
      (msg) => {
        void this.handleMessage(msg);
      },
      null,
      this.disposables
    );
    void this.init();
  }
  static {
    this.viewType = "nodalmerge.dagReplay";
  }
  static createOrShow(baseUrl, extensionUri) {
    if (_DagReplayPanel.current) {
      _DagReplayPanel.current.panel.reveal(vscode4.ViewColumn.Two);
      void _DagReplayPanel.current.init();
      return;
    }
    const panel = vscode4.window.createWebviewPanel(
      _DagReplayPanel.viewType,
      "NodalMerge \u2014 DAG",
      vscode4.ViewColumn.Two,
      {
        enableScripts: true,
        retainContextWhenHidden: true,
        localResourceRoots: [vscode4.Uri.joinPath(extensionUri, "out")]
      }
    );
    _DagReplayPanel.current = new _DagReplayPanel(panel, baseUrl, extensionUri);
  }
  async init() {
    try {
      const workUnits = await this.get("/studio/workunits");
      const port = this.extractPort();
      void this.panel.webview.postMessage({
        type: "init",
        port,
        roomId: "studio-main",
        workUnits: workUnits.map((wu) => ({
          workUnitId: wu.workUnitId,
          branchId: wu.branchId,
          goal: wu.goal
        }))
      });
    } catch {
    }
  }
  async handleMessage(msg) {
    if (msg.type === "branchFromCursor") {
      const goal = await vscode4.window.showInputBox({
        prompt: "Goal for branch from cursor",
        placeHolder: "e.g. Experiment from this checkpoint",
        ignoreFocusOut: true
      });
      if (!goal) {
        return;
      }
      try {
        const wu = await this.post("/studio/workunits", { goal, owner: "user" });
        void this.panel.webview.postMessage({
          type: "branchCreated",
          newBranchId: wu.branchId,
          workUnitId: wu.workUnitId,
          goal: wu.goal
        });
      } catch (err) {
        void vscode4.window.showErrorMessage("NodalMerge: " + String(err));
      }
      return;
    }
    if (msg.type === "markKnownGood") {
      const label = await vscode4.window.showInputBox({
        prompt: "Label for this checkpoint",
        placeHolder: "e.g. all tests passing",
        ignoreFocusOut: true
      });
      if (!label) {
        return;
      }
      try {
        await this.post("/studio/state/markKnownGood", {
          branchId: msg.branchId,
          nodeId: msg.nodeId,
          description: label,
          createdBy: "user"
        });
        void vscode4.window.showInformationMessage(
          'NodalMerge: Known good state saved \u2014 "' + label + '"'
        );
      } catch (err) {
        void vscode4.window.showErrorMessage("NodalMerge: " + String(err));
      }
    }
  }
  extractPort() {
    const match = this.baseUrl.match(/:(\d+)/);
    return match ? parseInt(match[1], 10) : 5080;
  }
  async get(path2) {
    const res = await fetch(this.baseUrl + path2);
    if (!res.ok) {
      throw new Error("GET " + path2 + " \u2192 " + String(res.status));
    }
    return res.json();
  }
  async post(path2, body) {
    const res = await fetch(this.baseUrl + path2, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(body)
    });
    if (!res.ok) {
      const text = await res.text();
      throw new Error("POST " + path2 + " \u2192 " + String(res.status) + ": " + text);
    }
    return res.json();
  }
  buildHtml() {
    const webview = this.panel.webview;
    const scriptUri = webview.asWebviewUri(
      vscode4.Uri.joinPath(this.extensionUri, "out", "dag-replay.js")
    );
    const csp = webview.cspSource;
    return `<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="UTF-8">
  <meta http-equiv="Content-Security-Policy"
        content="default-src 'none'; script-src ${csp}; connect-src ws://127.0.0.1:*; style-src 'unsafe-inline';">
  <meta name="viewport" content="width=device-width, initial-scale=1.0">
  <title>NodalMerge \u2014 DAG</title>
  <style>
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
    * { box-sizing: border-box; }
    body {
      background: var(--nm-bg); color: var(--nm-fg);
      font-family: var(--nm-font); font-size: var(--nm-size);
      margin: 0; padding: 0;
      display: flex; flex-direction: column; height: 100vh; overflow: hidden;
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
  </style>
</head>
<body>
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
  <div id="playback-bar" style="display:none">
    <span>PLAYBACK</span>
    <button id="btn-live">\u25B6 Live</button>
    <button id="btn-branch">\u2387 Branch from here</button>
    <button id="btn-kgs">\u{1F4CC} Mark Known Good</button>
  </div>
  <script src="${scriptUri}"></script>
</body>
</html>`;
  }
  dispose() {
    _DagReplayPanel.current = void 0;
    this.panel.dispose();
    for (const d of this.disposables) {
      d.dispose();
    }
    this.disposables.length = 0;
  }
};

// src/panels/AgentConfigPanel.ts
var vscode5 = __toESM(require("vscode"));
var AgentConfigPanel = class _AgentConfigPanel {
  constructor(panel, baseUrl, configService, secrets, lmProxyBaseUrl) {
    this.disposables = [];
    this.panel = panel;
    this.baseUrl = baseUrl;
    this.configService = configService;
    this.secrets = secrets;
    this.lmProxyBaseUrl = lmProxyBaseUrl;
    this.panel.webview.html = buildHtml2();
    this.panel.onDidDispose(() => this.dispose(), null, this.disposables);
    this.panel.webview.onDidReceiveMessage(
      (msg) => {
        void this.handleMessage(msg);
      },
      null,
      this.disposables
    );
    void this.sendConfig();
  }
  static {
    this.viewType = "nodalmerge.agentConfig";
  }
  static createOrShow(baseUrl, configService, secrets, lmProxyBaseUrl) {
    if (_AgentConfigPanel.current) {
      _AgentConfigPanel.current.panel.reveal(vscode5.ViewColumn.Two);
      void _AgentConfigPanel.current.sendConfig();
      return;
    }
    const panel = vscode5.window.createWebviewPanel(
      _AgentConfigPanel.viewType,
      "NodalMerge \u2014 Agent Config",
      vscode5.ViewColumn.Two,
      { enableScripts: true, retainContextWhenHidden: true }
    );
    _AgentConfigPanel.current = new _AgentConfigPanel(panel, baseUrl, configService, secrets, lmProxyBaseUrl);
  }
  async sendConfig() {
    void this.panel.webview.postMessage({
      type: "config",
      profiles: this.configService.getProfiles(),
      templates: this.configService.getTemplates(),
      defaultTopology: this.configService.getDefaultTopology()
    });
  }
  async handleMessage(msg) {
    switch (msg.type) {
      case "saveProfiles":
        await this.configService.saveProfiles(msg.profiles);
        void vscode5.window.showInformationMessage("NodalMerge: Agent profiles saved.");
        break;
      case "saveTemplates":
        await this.configService.saveTemplates(msg.templates);
        void vscode5.window.showInformationMessage("NodalMerge: Topology templates saved.");
        break;
      case "setDefault":
        await this.configService.setDefaultTopology(msg.name);
        break;
      case "setApiKey": {
        const profileId = msg.profileId;
        const key = msg.key;
        const profiles = this.configService.getProfiles();
        const profile = profiles.find((p) => p.id === profileId);
        if (profile && key) {
          await this.configService.storeApiKey(profile, key, this.secrets);
          void vscode5.window.showInformationMessage(`NodalMerge: API key stored for profile "${profileId}".`);
          void this.panel.webview.postMessage({ type: "apiKeySaved", profileId });
        }
        break;
      }
      case "quickSpawn":
        await this.handleQuickSpawn(msg.templateName, msg.goal);
        break;
    }
  }
  async handleQuickSpawn(templateName, goal) {
    const templates = this.configService.getTemplates();
    const template = templates.find((t) => t.name === templateName);
    if (!template) {
      void vscode5.window.showErrorMessage(`NodalMerge: Template "${templateName}" not found.`);
      void this.panel.webview.postMessage({ type: "spawnResult", success: false, message: "Template not found." });
      return;
    }
    const profiles = this.configService.getProfiles();
    const resolveProfile = async (profileId) => {
      const p = profiles.find((pr) => pr.id === profileId);
      const isVscodeLm = p?.provider === "vscode-lm";
      return {
        provider: isVscodeLm ? "openai" : p?.provider || "anthropic",
        model: p?.model,
        baseUrl: isVscodeLm ? this.lmProxyBaseUrl : p?.baseUrl,
        apiKey: isVscodeLm ? "" : p ? await this.configService.resolveApiKey(p, this.secrets) : void 0
      };
    };
    try {
      const orchCfg = await resolveProfile(template.orchestrator);
      const orchWu = await this.post("/studio/workunits", {
        goal,
        owner: template.orchestrator
      });
      await this.post("/studio/agents/spawn", {
        agentType: template.orchestrator,
        workUnitId: orchWu.workUnitId,
        provider: orchCfg.provider || void 0,
        model: orchCfg.model || void 0,
        baseUrl: orchCfg.baseUrl || void 0,
        apiKey: orchCfg.apiKey || void 0
      });
      for (const worker of template.workers ?? []) {
        const workerCfg = await resolveProfile(worker.profile);
        const workerWu = await this.post("/studio/workunits", {
          goal: `[${worker.profile}] ${goal}`,
          owner: worker.profile
        });
        await this.post("/studio/agents/spawn", {
          agentType: worker.profile,
          workUnitId: workerWu.workUnitId,
          provider: workerCfg.provider || void 0,
          model: workerCfg.model || void 0,
          baseUrl: workerCfg.baseUrl || void 0,
          apiKey: workerCfg.apiKey || void 0
        });
      }
      void this.panel.webview.postMessage({ type: "spawnResult", success: true });
      void vscode5.window.showInformationMessage(
        `NodalMerge: Spawned "${templateName}" topology for: ${goal}`
      );
    } catch (err) {
      void this.panel.webview.postMessage({
        type: "spawnResult",
        success: false,
        message: String(err)
      });
      void vscode5.window.showErrorMessage("NodalMerge: Quick spawn failed \u2014 " + String(err));
    }
  }
  async post(path2, body) {
    const res = await fetch(this.baseUrl + path2, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(body)
    });
    if (!res.ok) {
      const text = await res.text();
      throw new Error("POST " + path2 + " \u2192 " + String(res.status) + ": " + text);
    }
    return res.json();
  }
  dispose() {
    _AgentConfigPanel.current = void 0;
    this.panel.dispose();
    for (const d of this.disposables) {
      d.dispose();
    }
    this.disposables.length = 0;
  }
};
function buildNonce3() {
  let text = "";
  const chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
  for (let i = 0; i < 32; i++) {
    text += chars[Math.floor(Math.random() * chars.length)];
  }
  return text;
}
function buildHtml2() {
  const n = buildNonce3();
  return [
    "<!DOCTYPE html>",
    '<html lang="en">',
    "<head>",
    '  <meta charset="UTF-8">',
    '  <meta http-equiv="Content-Security-Policy"',
    `        content="default-src 'none'; style-src 'nonce-` + n + "'; script-src 'nonce-" + n + `';">`,
    '  <meta name="viewport" content="width=device-width, initial-scale=1.0">',
    "  <title>Agent Config</title>",
    '  <style nonce="' + n + '">',
    AGENT_CONFIG_CSS,
    "  </style>",
    "</head>",
    "<body>",
    AGENT_CONFIG_HTML,
    '<script nonce="' + n + '">',
    AGENT_CONFIG_JS,
    "</script>",
    "</body>",
    "</html>"
  ].join("\n");
}
var AGENT_CONFIG_CSS = `
  :root {
    --nm-bg:         var(--vscode-editor-background);
    --nm-fg:         var(--vscode-editor-foreground);
    --nm-border:     var(--vscode-widget-border, #444);
    --nm-section-bg: var(--vscode-sideBar-background, #2a2a2a);
    --nm-btn:        var(--vscode-button-background);
    --nm-btn-fg:     var(--vscode-button-foreground);
    --nm-btn-hover:  var(--vscode-button-hoverBackground);
    --nm-font:       var(--vscode-font-family);
    --nm-mono:       var(--vscode-editor-font-family, monospace);
    --nm-size:       var(--vscode-font-size, 13px);
    --nm-input-bg:   var(--vscode-input-background, #3c3c3c);
    --nm-input-fg:   var(--vscode-input-foreground, #ccc);
    --nm-input-bdr:  var(--vscode-input-border, #555);
  }
  * { box-sizing: border-box; }
  body {
    background: var(--nm-bg); color: var(--nm-fg);
    font-family: var(--nm-font); font-size: var(--nm-size);
    margin: 0; padding: 0; display: flex; flex-direction: column; height: 100vh;
  }
  .header {
    padding: 13px 16px 8px; border-bottom: 1px solid var(--nm-border); flex-shrink: 0;
  }
  .header h1 { font-size: 1.1em; font-weight: 700; margin: 0; }
  .tabs {
    display: flex; border-bottom: 1px solid var(--nm-border); flex-shrink: 0;
  }
  .tab-btn {
    background: transparent; color: var(--nm-fg); border: none;
    border-bottom: 2px solid transparent; padding: 8px 16px;
    font-size: 0.88em; cursor: pointer; font-family: var(--nm-font); opacity: 0.6;
  }
  .tab-btn:hover { opacity: 0.9; }
  .tab-btn.active { opacity: 1; border-bottom-color: var(--nm-btn); }
  .tab-pane { flex: 1; overflow-y: auto; padding: 14px 16px; display: none; }
  .tab-pane.visible { display: block; }
  table { width: 100%; border-collapse: collapse; font-size: 0.9em; margin-bottom: 10px; }
  th {
    text-align: left; font-size: 0.75em; text-transform: uppercase;
    letter-spacing: 0.06em; opacity: 0.5; padding: 4px 8px;
    border-bottom: 1px solid var(--nm-border);
  }
  td {
    padding: 6px 8px;
    border-bottom: 1px solid color-mix(in srgb, var(--nm-border) 35%, transparent);
    vertical-align: middle;
  }
  .mono { font-family: var(--nm-mono); font-size: 0.85em; opacity: 0.75; }
  .act-cell { display: flex; gap: 4px; justify-content: flex-end; }
  button {
    background: var(--nm-btn); color: var(--nm-btn-fg); border: none;
    border-radius: 3px; padding: 3px 10px; font-size: 0.8em;
    cursor: pointer; font-family: var(--nm-font);
  }
  button:hover { background: var(--nm-btn-hover); }
  button.ghost {
    background: transparent; color: var(--nm-fg); border: 1px solid var(--nm-border);
  }
  button.ghost:hover { background: color-mix(in srgb, var(--nm-border) 50%, transparent); }
  button.danger { background: #b83030; color: #fff; }
  button.danger:hover { filter: brightness(1.2); }
  button:disabled { opacity: 0.4; cursor: default; }
  .add-btn {
    width: 100%; background: transparent; color: var(--nm-fg);
    border: 1px dashed var(--nm-border); padding: 6px; font-size: 0.85em;
    opacity: 0.5; border-radius: 3px; cursor: pointer; margin-top: 4px;
  }
  .add-btn:hover { opacity: 1; }
  .form-box {
    background: var(--nm-section-bg); border: 1px solid var(--nm-border);
    border-radius: 4px; padding: 14px; margin-bottom: 12px;
  }
  .form-box h3 { margin: 0 0 12px; font-size: 0.9em; opacity: 0.7; }
  .field { margin-bottom: 10px; }
  .field label { display: block; font-size: 0.8em; opacity: 0.6; margin-bottom: 3px; }
  input[type=text], textarea, select {
    width: 100%;
    background: var(--nm-input-bg); color: var(--nm-input-fg);
    border: 1px solid var(--nm-input-bdr); border-radius: 2px;
    padding: 5px 7px; font-family: var(--nm-font); font-size: 0.9em;
  }
  textarea { resize: vertical; min-height: 60px; }
  .form-actions { display: flex; gap: 6px; margin-top: 12px; }
  .save-bar {
    padding: 9px 16px; border-top: 1px solid var(--nm-border);
    display: flex; align-items: center; gap: 8px; flex-shrink: 0;
  }
  .save-bar .status { font-size: 0.8em; opacity: 0.5; flex: 1; }
  .spawn-form { max-width: 440px; }
  .spawn-form .field { margin-bottom: 12px; }
  .spawn-result {
    margin-top: 14px; font-size: 0.88em; padding: 8px 12px; border-radius: 4px;
  }
  .spawn-result.ok  { background: #1e3a1e; color: #7ec87e; }
  .spawn-result.err { background: #3a1e1e; color: #f07070; }
  .default-badge {
    background: #2d5016; color: #8fc96a;
    border-radius: 10px; font-size: 0.74em; padding: 1px 7px; margin-left: 5px;
  }
`;
var AGENT_CONFIG_HTML = `
  <div class="header"><h1>Agent Config</h1></div>
  <div class="tabs">
    <button class="tab-btn active" data-tab="profiles">Profiles</button>
    <button class="tab-btn" data-tab="templates">Topology Templates</button>
    <button class="tab-btn" data-tab="spawn">Quick Spawn</button>
  </div>

  <div id="pane-profiles" class="tab-pane visible">
    <div id="profile-form-area"></div>
    <table>
      <thead><tr><th>ID</th><th>Label</th><th>Domain</th><th>Provider</th><th>Model</th><th></th></tr></thead>
      <tbody id="profile-tbody"></tbody>
    </table>
    <button class="add-btn" id="btn-add-profile">+ Add Profile</button>
  </div>

  <div id="pane-templates" class="tab-pane">
    <div id="template-form-area"></div>
    <table>
      <thead><tr><th>Name</th><th>Orchestrator</th><th>Workers</th><th></th></tr></thead>
      <tbody id="template-tbody"></tbody>
    </table>
    <button class="add-btn" id="btn-add-template">+ Add Template</button>
  </div>

  <div id="pane-spawn" class="tab-pane">
    <div class="spawn-form">
      <div class="field">
        <label>Topology Template</label>
        <select id="spawn-template"></select>
      </div>
      <div class="field">
        <label>Goal</label>
        <input type="text" id="spawn-goal" placeholder="e.g. Refactor the auth module">
      </div>
      <button id="btn-spawn">&#x25B6; Quick Spawn</button>
      <div id="spawn-result" class="spawn-result" style="display:none"></div>
    </div>
  </div>

  <div class="save-bar">
    <span class="status" id="save-status"></span>
    <button id="btn-save-profiles">Save Profiles</button>
    <button id="btn-save-templates">Save Templates</button>
  </div>
`;
var AGENT_CONFIG_JS = `
  const vscode = acquireVsCodeApi();

  let profiles = [];
  let templates = [];
  let defaultTopology = '';

  // \u2500\u2500 Tab switching \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500
  document.querySelectorAll('.tab-btn').forEach(function(btn) {
    btn.addEventListener('click', function() {
      const tab = this.getAttribute('data-tab');
      document.querySelectorAll('.tab-btn').forEach(function(b) { b.classList.remove('active'); });
      document.querySelectorAll('.tab-pane').forEach(function(p) { p.classList.remove('visible'); });
      this.classList.add('active');
      const pane = document.getElementById('pane-' + tab);
      if (pane) { pane.classList.add('visible'); }
    });
  });

  // \u2500\u2500 Escape helper \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500
  function esc(str) {
    return String(str || '')
      .replace(/&/g, '&amp;').replace(/</g, '&lt;')
      .replace(/>/g, '&gt;').replace(/"/g, '&quot;').replace(/'/g, '&#39;');
  }

  // \u2500\u2500 Status flash \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500
  function setStatus(msg) {
    const el = document.getElementById('save-status');
    if (el) {
      el.textContent = msg;
      setTimeout(function() { el.textContent = ''; }, 3000);
    }
  }

  // \u2500\u2500 Profiles \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500
  function renderProfiles() {
    const tbody = document.getElementById('profile-tbody');
    if (!tbody) { return; }
    tbody.innerHTML = '';
    profiles.forEach(function(p, i) {
      const tr = document.createElement('tr');
      tr.innerHTML =
        '<td class="mono">' + esc(p.id) + '</td>' +
        '<td>' + esc(p.label) + '</td>' +
        '<td class="mono">' + esc(p.domain) + '</td>' +
        '<td class="mono">' + esc(p.provider || 'anthropic') + '</td>' +
        '<td class="mono">' + esc(p.model || '\u2014') + '</td>' +
        '<td><div class="act-cell">' +
          '<button class="ghost" data-action="edit" data-idx="' + i + '">Edit</button>' +
          '<button class="danger" data-action="delete" data-idx="' + i + '">Delete</button>' +
        '</div></td>';
      tbody.appendChild(tr);
    });
    updateSpawnTemplates();
  }

  document.getElementById('profile-tbody').addEventListener('click', function(e) {
    const btn = e.target.closest('button');
    if (!btn) { return; }
    const idx    = parseInt(btn.getAttribute('data-idx'), 10);
    const action = btn.getAttribute('data-action');
    if (action === 'edit')   { showProfileForm(idx); }
    if (action === 'delete') { deleteProfile(idx); }
  });

  function deleteProfile(idx) {
    profiles.splice(idx, 1);
    document.getElementById('profile-form-area').innerHTML = '';
    renderProfiles();
  }

  function showProfileForm(idx) {
    const isNew = idx === -1;
    const p = isNew
      ? { id: '', label: '', domain: '', provider: 'vscode-lm', model: '', baseUrl: '', systemPromptHint: '', apiKeyRef: '' }
      : profiles[idx];
    const curProvider = p.provider || 'anthropic';
    const isVsLm = curProvider === 'vscode-lm';
    const area = document.getElementById('profile-form-area');
    area.innerHTML =
      '<div class="form-box">' +
      '<h3>' + (isNew ? 'Add Profile' : 'Edit Profile') + '</h3>' +
      '<div class="field"><label>ID (agent type key)</label>' +
        '<input type="text" id="pf-id" value="' + esc(p.id) + '"' +
        (isNew ? '' : ' readonly style="opacity:0.6"') +
        ' placeholder="e.g. worker"></div>' +
      '<div class="field"><label>Display Label</label>' +
        '<input type="text" id="pf-label" value="' + esc(p.label) + '" placeholder="e.g. Worker Agent"></div>' +
      '<div class="field"><label>Domain</label>' +
        '<input type="text" id="pf-domain" value="' + esc(p.domain) + '" placeholder="e.g. code, docs, general"></div>' +
      '<div class="field"><label>LLM Provider</label>' +
        '<select id="pf-provider">' +
          '<option value="vscode-lm"' + (curProvider === 'vscode-lm' ? ' selected' : '') + '>VS Code LM (Copilot / Cursor \u2014 no key needed)</option>' +
          '<option value="openai"'    + (curProvider === 'openai'    ? ' selected' : '') + '>OpenAI compatible (OpenAI, DeepSeek, Azure, LM Studio, etc.)</option>' +
          '<option value="anthropic"' + (curProvider === 'anthropic' ? ' selected' : '') + '>Anthropic (claude-*)</option>' +
        '</select></div>' +
      '<div id="pf-model-row" class="field"' + (isVsLm ? ' style="display:none"' : '') + '>' +
        '<label>Model</label>' +
        '<input type="text" id="pf-model" value="' + esc(p.model || '') + '" placeholder="e.g. claude-sonnet-4-6 or gpt-4o"></div>' +
      '<div id="pf-baseurl-row" class="field"' + (isVsLm ? ' style="display:none"' : '') + '>' +
        '<label>Base URL (leave blank for default)</label>' +
        '<input type="text" id="pf-baseurl" value="' + esc(p.baseUrl || '') + '"' +
        ' placeholder="' + (curProvider === 'openai' ? 'https://api.openai.com' : 'https://api.anthropic.com') + '"></div>' +
      '<div id="pf-apikey-row" class="field"' + (isVsLm ? ' style="display:none"' : '') + '>' +
        '<label>API Key</label>' +
        '<div style="display:flex;gap:6px;align-items:center">' +
          '<input type="password" id="pf-apikey" placeholder="' + (p.apiKeyRef ? '(key stored)' : 'Paste key to store') + '" style="flex:1">' +
          '<button id="pf-store-key" class="ghost">Store Key</button>' +
        '</div>' +
        '<div id="pf-key-status" style="font-size:0.78em;opacity:0.5;margin-top:3px">' +
          (p.apiKeyRef ? 'Key stored (' + esc(p.apiKeyRef) + ')' : 'No key stored') +
        '</div>' +
      '</div>' +
      (isVsLm ? '<div class="field" style="font-size:0.82em;opacity:0.6;padding:6px 0">Uses your VS Code Copilot or Cursor subscription \u2014 no API key required.</div>' : '') +
      '<div class="field"><label>System Prompt Hint (optional)</label>' +
        '<textarea id="pf-prompt">' + esc(p.systemPromptHint || '') + '</textarea></div>' +
      '<div class="form-actions">' +
        '<button id="pf-save">Save</button>' +
        '<button class="ghost" id="pf-cancel">Cancel</button>' +
      '</div></div>';

    // Toggle field visibility when provider changes
    document.getElementById('pf-provider').addEventListener('change', function() {
      const isVs = this.value === 'vscode-lm';
      document.getElementById('pf-model-row').style.display  = isVs ? 'none' : '';
      document.getElementById('pf-baseurl-row').style.display = isVs ? 'none' : '';
      document.getElementById('pf-apikey-row').style.display  = isVs ? 'none' : '';
    });

    document.getElementById('pf-store-key').addEventListener('click', function() {
      const key = document.getElementById('pf-apikey').value.trim();
      const id  = document.getElementById('pf-id').value.trim() || (isNew ? '' : p.id);
      if (!key) { alert('Paste an API key first.'); return; }
      if (!id)  { alert('Save the profile ID first.'); return; }
      vscode.postMessage({ type: 'setApiKey', profileId: id, key: key });
      document.getElementById('pf-apikey').value = '';
    });

    document.getElementById('pf-save').addEventListener('click', function() {
      const id       = document.getElementById('pf-id').value.trim();
      const label    = document.getElementById('pf-label').value.trim();
      const domain   = document.getElementById('pf-domain').value.trim();
      const provider = document.getElementById('pf-provider').value;
      const model    = provider === 'vscode-lm' ? '' : document.getElementById('pf-model').value.trim();
      const baseUrl  = provider === 'vscode-lm' ? '' : document.getElementById('pf-baseurl').value.trim();
      const prompt   = document.getElementById('pf-prompt').value.trim();
      if (!id || !label || !domain) { alert('ID, Label, and Domain are required.'); return; }
      const profile = {
        id, label, domain, provider,
        model:            model   || undefined,
        baseUrl:          baseUrl || undefined,
        apiKeyRef:        provider === 'vscode-lm' ? undefined : (isNew ? undefined : p.apiKeyRef),
        systemPromptHint: prompt  || undefined,
      };
      if (isNew) { profiles.push(profile); }
      else       { profiles[idx] = profile; }
      document.getElementById('profile-form-area').innerHTML = '';
      renderProfiles();
    });
    document.getElementById('pf-cancel').addEventListener('click', function() {
      document.getElementById('profile-form-area').innerHTML = '';
    });
  }

  document.getElementById('btn-add-profile').addEventListener('click', function() {
    showProfileForm(-1);
  });

  // \u2500\u2500 Templates \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500
  function renderTemplates() {
    const tbody = document.getElementById('template-tbody');
    if (!tbody) { return; }
    tbody.innerHTML = '';
    templates.forEach(function(t, i) {
      const workers   = (t.workers || []).map(function(w) { return w.profile; }).join(', ') || '\u2014';
      const isDefault = t.name === defaultTopology;
      const tr = document.createElement('tr');
      tr.innerHTML =
        '<td>' + esc(t.name) + (isDefault ? '<span class="default-badge">default</span>' : '') + '</td>' +
        '<td class="mono">' + esc(t.orchestrator) + '</td>' +
        '<td class="mono">' + esc(workers) + '</td>' +
        '<td><div class="act-cell">' +
          (isDefault ? '' : '<button class="ghost" data-action="setDefault" data-idx="' + i + '">Set Default</button>') +
          '<button class="ghost" data-action="edit" data-idx="' + i + '">Edit</button>' +
          '<button class="danger" data-action="delete" data-idx="' + i + '">Delete</button>' +
        '</div></td>';
      tbody.appendChild(tr);
    });
    updateSpawnTemplates();
  }

  document.getElementById('template-tbody').addEventListener('click', function(e) {
    const btn = e.target.closest('button');
    if (!btn) { return; }
    const idx    = parseInt(btn.getAttribute('data-idx'), 10);
    const action = btn.getAttribute('data-action');
    if (action === 'edit')       { showTemplateForm(idx); }
    if (action === 'delete')     { deleteTemplate(idx); }
    if (action === 'setDefault') {
      defaultTopology = templates[idx].name;
      vscode.postMessage({ type: 'setDefault', name: defaultTopology });
      renderTemplates();
    }
  });

  function deleteTemplate(idx) {
    templates.splice(idx, 1);
    document.getElementById('template-form-area').innerHTML = '';
    renderTemplates();
  }

  function showTemplateForm(idx) {
    const isNew = idx === -1;
    const t = isNew ? { name: '', orchestrator: '', workers: [] } : templates[idx];
    const workersStr = (t.workers || []).map(function(w) { return w.profile; }).join(', ');
    const area = document.getElementById('template-form-area');
    area.innerHTML =
      '<div class="form-box">' +
      '<h3>' + (isNew ? 'Add Template' : 'Edit Template') + '</h3>' +
      '<div class="field"><label>Name</label>' +
        '<input type="text" id="tmpl-name" value="' + esc(t.name) + '" placeholder="e.g. Default"></div>' +
      '<div class="field"><label>Orchestrator Profile ID</label>' +
        '<input type="text" id="tmpl-orch" value="' + esc(t.orchestrator) + '" placeholder="e.g. orchestrator"></div>' +
      '<div class="field"><label>Worker Profile IDs (comma-separated)</label>' +
        '<input type="text" id="tmpl-workers" value="' + esc(workersStr) + '" placeholder="e.g. worker, docs-agent"></div>' +
      '<div class="form-actions">' +
        '<button id="tmpl-save">Save</button>' +
        '<button class="ghost" id="tmpl-cancel">Cancel</button>' +
      '</div></div>';

    document.getElementById('tmpl-save').addEventListener('click', function() {
      const name  = document.getElementById('tmpl-name').value.trim();
      const orch  = document.getElementById('tmpl-orch').value.trim();
      const wStr  = document.getElementById('tmpl-workers').value.trim();
      if (!name || !orch) { alert('Name and Orchestrator are required.'); return; }
      const workers = wStr
        ? wStr.split(',').map(function(s) { return { profile: s.trim() }; }).filter(function(w) { return w.profile; })
        : [];
      const tmpl = { name: name, orchestrator: orch, workers: workers };
      if (isNew) { templates.push(tmpl); }
      else       { templates[idx] = tmpl; }
      document.getElementById('template-form-area').innerHTML = '';
      renderTemplates();
    });
    document.getElementById('tmpl-cancel').addEventListener('click', function() {
      document.getElementById('template-form-area').innerHTML = '';
    });
  }

  document.getElementById('btn-add-template').addEventListener('click', function() {
    showTemplateForm(-1);
  });

  // \u2500\u2500 Spawn template selector \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500
  function updateSpawnTemplates() {
    const sel = document.getElementById('spawn-template');
    if (!sel) { return; }
    const current = sel.value;
    sel.innerHTML = templates.map(function(t) {
      return '<option value="' + esc(t.name) + '">' + esc(t.name) + '</option>';
    }).join('');
    if (templates.find(function(t) { return t.name === current; })) {
      sel.value = current;
    } else if (defaultTopology) {
      sel.value = defaultTopology;
    }
  }

  // \u2500\u2500 Quick Spawn \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500
  document.getElementById('btn-spawn').addEventListener('click', function() {
    const templateName = document.getElementById('spawn-template').value;
    const goal = document.getElementById('spawn-goal').value.trim();
    if (!goal) { alert('Goal is required.'); return; }
    this.disabled    = true;
    this.textContent = 'Spawning\u2026';
    vscode.postMessage({ type: 'quickSpawn', templateName: templateName, goal: goal });
  });

  // \u2500\u2500 Save \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500
  document.getElementById('btn-save-profiles').addEventListener('click', function() {
    vscode.postMessage({ type: 'saveProfiles', profiles: profiles });
    setStatus('Profiles saved.');
  });
  document.getElementById('btn-save-templates').addEventListener('click', function() {
    vscode.postMessage({ type: 'saveTemplates', templates: templates });
    setStatus('Templates saved.');
  });

  // \u2500\u2500 Extension host messages \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500
  window.addEventListener('message', function(event) {
    const msg = event.data;
    if (msg.type === 'config') {
      profiles        = msg.profiles        || [];
      templates       = msg.templates       || [];
      defaultTopology = msg.defaultTopology || '';
      renderProfiles();
      renderTemplates();
      updateSpawnTemplates();
      return;
    }
    if (msg.type === 'apiKeySaved') {
      const statusEl = document.getElementById('pf-key-status');
      if (statusEl) { statusEl.textContent = 'Key stored (' + esc(msg.profileId) + ')'; }
      return;
    }
    if (msg.type === 'spawnResult') {
      const btn = document.getElementById('btn-spawn');
      btn.disabled    = false;
      btn.textContent = '\\u25B6 Quick Spawn';
      const result = document.getElementById('spawn-result');
      result.style.display = '';
      result.className     = 'spawn-result ' + (msg.success ? 'ok' : 'err');
      result.textContent   = msg.success ? 'Spawned successfully!' : ('Error: ' + (msg.message || 'unknown'));
      if (msg.success) {
        document.getElementById('spawn-goal').value = '';
        setTimeout(function() { result.style.display = 'none'; }, 5000);
      }
    }
  });
`;

// src/NotificationManager.ts
var vscode6 = __toESM(require("vscode"));
var NotificationManager = class {
  constructor(onOpenReview) {
    // Track last known status per proposal so we only fire once per transition.
    this.seenStatuses = /* @__PURE__ */ new Map();
    this.onOpenReview = onOpenReview;
  }
  update(proposals) {
    for (const p of proposals) {
      const prev = this.seenStatuses.get(p.proposalId);
      const curr = (p.status ?? "").toLowerCase();
      if (curr === "readyforreview" && prev !== "readyforreview") {
        void this.notifyReady(p);
      }
      this.seenStatuses.set(p.proposalId, curr);
    }
  }
  async notifyReady(p) {
    const action = await vscode6.window.showInformationMessage(
      'NodalMerge: "' + p.sourceBranch + '" is ready for review.',
      "Open Review",
      "Dismiss"
    );
    if (action === "Open Review") {
      this.onOpenReview(p.proposalId);
    }
  }
};

// src/AgentConfigService.ts
var vscode7 = __toESM(require("vscode"));
var DEFAULT_PROFILES = [
  { id: "orchestrator", label: "Orchestrator", domain: "orchestration", model: "" },
  { id: "worker", label: "Worker", domain: "general", model: "" }
];
var DEFAULT_TEMPLATES = [
  { name: "Default", orchestrator: "orchestrator", workers: [{ profile: "worker" }] }
];
var AgentConfigService = class {
  getProfiles() {
    const cfg = vscode7.workspace.getConfiguration("nodalmerge");
    const stored = cfg.get("agentProfiles") ?? [];
    return stored.length > 0 ? stored : [...DEFAULT_PROFILES];
  }
  getTemplates() {
    const cfg = vscode7.workspace.getConfiguration("nodalmerge");
    const stored = cfg.get("topologyTemplates") ?? [];
    return stored.length > 0 ? stored : [...DEFAULT_TEMPLATES];
  }
  getDefaultTopology() {
    return vscode7.workspace.getConfiguration("nodalmerge").get("defaultTopology") ?? "";
  }
  async saveProfiles(profiles) {
    await vscode7.workspace.getConfiguration("nodalmerge").update("agentProfiles", profiles, vscode7.ConfigurationTarget.Workspace);
  }
  async saveTemplates(templates) {
    await vscode7.workspace.getConfiguration("nodalmerge").update("topologyTemplates", templates, vscode7.ConfigurationTarget.Workspace);
  }
  async setDefaultTopology(name) {
    await vscode7.workspace.getConfiguration("nodalmerge").update("defaultTopology", name, vscode7.ConfigurationTarget.Workspace);
  }
  async resolveApiKey(profile, secrets) {
    if (!profile.apiKeyRef) {
      return void 0;
    }
    return secrets.get(profile.apiKeyRef);
  }
  async storeApiKey(profile, key, secrets) {
    const ref = profile.apiKeyRef ?? `nodalmerge.apikey.${profile.id}`;
    await secrets.store(ref, key);
    if (!profile.apiKeyRef) {
      const profiles = this.getProfiles();
      const idx = profiles.findIndex((p) => p.id === profile.id);
      if (idx >= 0) {
        profiles[idx] = { ...profiles[idx], apiKeyRef: ref };
        await this.saveProfiles(profiles);
      }
    }
  }
  async pickProfile(placeHolder = "Select an agent profile") {
    const profiles = this.getProfiles();
    const items = profiles.map((p) => ({
      label: p.label,
      description: p.domain + (p.model ? " \xB7 " + p.model : ""),
      detail: p.id,
      profile: p
    }));
    const picked = await vscode7.window.showQuickPick(items, { placeHolder });
    return picked?.profile;
  }
};

// src/LmApiProxy.ts
var http2 = __toESM(require("http"));
var vscode8 = __toESM(require("vscode"));
var LmApiProxy = class {
  constructor() {
    this._port = 0;
  }
  get baseUrl() {
    return `http://127.0.0.1:${this._port}`;
  }
  async start() {
    this.server = http2.createServer((req, res) => {
      void this.handle(req, res);
    });
    await new Promise((resolve, reject) => {
      this.server.listen(0, "127.0.0.1", () => {
        const addr = this.server.address();
        this._port = addr.port;
        resolve();
      });
      this.server.on("error", reject);
    });
  }
  async handle(req, res) {
    if (req.method !== "POST") {
      res.writeHead(405);
      res.end();
      return;
    }
    try {
      const body = await readBody(req);
      const oaiReq = JSON.parse(body);
      const oaiRes = await this.dispatch(oaiReq);
      res.writeHead(200, { "Content-Type": "application/json" });
      res.end(JSON.stringify(oaiRes));
    } catch (err) {
      res.writeHead(500, { "Content-Type": "application/json" });
      res.end(JSON.stringify({ error: { message: String(err), type: "server_error" } }));
    }
  }
  async dispatch(req) {
    const modelHint = req.model ?? "";
    const models = await vscode8.lm.selectChatModels(modelHint ? { family: modelHint } : void 0);
    if (!models.length) {
      throw new Error(
        "No VS Code language models available. Ensure GitHub Copilot (or another LM provider) is signed in."
      );
    }
    const model = models[0];
    const vsMessages = toVsMessages(req.messages ?? []);
    const vsTools = toVsTools(req.tools ?? []);
    const cts = new vscode8.CancellationTokenSource();
    const response = await model.sendRequest(vsMessages, { tools: vsTools }, cts.token);
    let textContent = "";
    const toolCalls = [];
    let callIndex = 0;
    for await (const part of response.stream) {
      if (part instanceof vscode8.LanguageModelTextPart) {
        textContent += part.value;
      } else if (part instanceof vscode8.LanguageModelToolCallPart) {
        toolCalls.push({
          id: part.callId,
          type: "function",
          function: {
            name: part.name,
            arguments: JSON.stringify(part.input)
          }
        });
        callIndex++;
      }
    }
    const finishReason = toolCalls.length > 0 ? "tool_calls" : "stop";
    const message = {
      role: "assistant",
      content: textContent || null,
      tool_calls: toolCalls.length > 0 ? toolCalls : void 0
    };
    return {
      id: `chatcmpl-vscode-${Date.now()}`,
      object: "chat.completion",
      created: Math.floor(Date.now() / 1e3),
      model: model.id,
      choices: [{ index: 0, message, finish_reason: finishReason }],
      usage: { prompt_tokens: 0, completion_tokens: 0, total_tokens: 0 }
    };
  }
  dispose() {
    this.server?.close();
  }
};
function toVsMessages(msgs) {
  const out = [];
  for (const msg of msgs) {
    if (msg.role === "system" || msg.role === "user") {
      out.push(vscode8.LanguageModelChatMessage.User(msg.content ?? ""));
    } else if (msg.role === "assistant") {
      const parts = [];
      if (msg.content) parts.push(new vscode8.LanguageModelTextPart(msg.content));
      for (const tc of msg.tool_calls ?? []) {
        let input = {};
        try {
          input = JSON.parse(tc.function.arguments || "{}");
        } catch {
        }
        parts.push(new vscode8.LanguageModelToolCallPart(tc.id, tc.function.name, input));
      }
      out.push(vscode8.LanguageModelChatMessage.Assistant(parts.length === 1 && parts[0] instanceof vscode8.LanguageModelTextPart ? parts[0].value : parts));
    } else if (msg.role === "tool") {
      out.push(vscode8.LanguageModelChatMessage.User([
        new vscode8.LanguageModelToolResultPart(
          msg.tool_call_id ?? "",
          [new vscode8.LanguageModelTextPart(msg.content ?? "")]
        )
      ]));
    }
  }
  return out;
}
function toVsTools(tools) {
  return tools.filter((t) => t.type === "function").map((t) => ({
    name: t.function.name,
    description: t.function.description ?? "",
    inputSchema: t.function.parameters
  }));
}
function readBody(req) {
  return new Promise((resolve, reject) => {
    const chunks = [];
    req.on("data", (chunk) => chunks.push(chunk));
    req.on("end", () => resolve(Buffer.concat(chunks).toString("utf8")));
    req.on("error", reject);
  });
}

// src/extension.ts
async function activate(context) {
  const output = vscode9.window.createOutputChannel("NodalMerge Studio");
  context.subscriptions.push(output);
  const manager = new HostManager(output, context);
  const agentConfig = new AgentConfigService();
  const lmProxy = new LmApiProxy();
  context.subscriptions.push(manager, lmProxy);
  try {
    await lmProxy.start();
    output.appendLine(`[NodalMerge] LM proxy listening at ${lmProxy.baseUrl}`);
  } catch (err) {
    output.appendLine(`[NodalMerge] LM proxy failed to start (vscode-lm provider unavailable): ${String(err)}`);
  }
  context.subscriptions.push(
    vscode9.commands.registerCommand(COMMANDS.RESTART_HOST, async () => {
      output.show();
      try {
        await manager.restart();
        vscode9.window.showInformationMessage("NodalMerge Studio Host restarted.");
      } catch (err) {
        vscode9.window.showErrorMessage(`Failed to restart host: ${String(err)}`);
      }
    }),
    vscode9.commands.registerCommand(COMMANDS.SHOW_OUTPUT, () => {
      output.show();
    }),
    vscode9.commands.registerCommand(COMMANDS.OPEN_DASHBOARD, () => {
      WorkspaceDashboardPanel.createOrShow(manager.hostBaseUrl, notificationManager, agentConfig);
    }),
    vscode9.commands.registerCommand(COMMANDS.OPEN_MERGE_REVIEW, (proposalId) => {
      MergeReviewPanel.createOrShow(manager.hostBaseUrl, proposalId);
    }),
    vscode9.commands.registerCommand(COMMANDS.OPEN_DAG_REPLAY, () => {
      DagReplayPanel.createOrShow(manager.hostBaseUrl, context.extensionUri);
    }),
    vscode9.commands.registerCommand(COMMANDS.OPEN_AGENT_CONFIG, () => {
      AgentConfigPanel.createOrShow(manager.hostBaseUrl, agentConfig, context.secrets, lmProxy.baseUrl);
    })
  );
  const notificationManager = new NotificationManager((proposalId) => {
    void vscode9.commands.executeCommand(COMMANDS.OPEN_MERGE_REVIEW, proposalId);
  });
  try {
    await manager.start();
  } catch (err) {
    const action = await vscode9.window.showErrorMessage(
      `NodalMerge Studio Host failed to start: ${String(err)}`,
      "Show Output",
      "Retry"
    );
    if (action === "Show Output") {
      output.show();
    } else if (action === "Retry") {
      vscode9.commands.executeCommand(COMMANDS.RESTART_HOST);
    }
  }
}
function deactivate() {
}
// Annotate the CommonJS export names for ESM import in node:
0 && (module.exports = {
  activate,
  deactivate
});
