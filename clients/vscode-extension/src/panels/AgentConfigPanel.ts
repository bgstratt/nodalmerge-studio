import * as vscode from 'vscode';
import type { AgentConfigService, AgentProfile, TopologyTemplate } from '../AgentConfigService';
import { scopeViewCss } from './sharedWebviewChrome';

export interface PipelineProfile {
  agentProfileId: string;
  name: string;
  stage: string;
  systemPrompt: string;
  allowedTools: string[];
  maxIterations: number;
  // Slice 14c — glob patterns (e.g. "src/**/*.tsx") declaring this profile's file-scope
  // specialty. Empty = no declared specialty, routing falls through to heuristic/LLM selection.
  fileScopePatterns: string[];
  // Harness-hosting-architecture Phase B1 — which IHarnessExecutor runs this role. Not exposed
  // in the webview: the user-facing selection is provider-driven (a "claude-cli" Model Profile
  // assigned via Agent Topology routes the role to ClaudeCodeExecutor server-side). These fields
  // exist so values set directly over REST survive a UI edit's PUT round-trip.
  executor?: string;
  injectApiKeyEnv?: boolean;
}

export interface DomainAgentInfo {
  name: string;
  titlePrefix: string;
  keywords: string[];
}

export class ModelAgentStudioPanel {
  static readonly containerId = 'shell-pane-model-agent-studio';

  private readonly panel: vscode.WebviewPanel;
  private readonly baseUrl: string;
  private readonly configService: AgentConfigService;
  private readonly secrets: vscode.SecretStorage;
  private readonly onConfigChanged?: () => void;

  constructor(
    panel: vscode.WebviewPanel,
    baseUrl: string,
    configService: AgentConfigService,
    secrets: vscode.SecretStorage,
    // Lets Goal Workspace (agent profiles feed its strategy list and fork-config picker, and
    // session defaults seed its Task/Workspace Review radios) refresh immediately after a save
    // here, instead of waiting for its own ~30s poll cadence.
    onConfigChanged?: () => void,
  ) {
    this.panel          = panel;
    this.baseUrl        = baseUrl;
    this.configService  = configService;
    this.secrets        = secrets;
    this.onConfigChanged = onConfigChanged;
  }

  /** Called once by the shell right after construction — was the tail of createOrShow(). */
  activate(): void {
    void this.sendConfig();
    void this.sendParticipants();
    // Poll participants every 10 s and re-send config every 30 s so domain agents and pipeline
    // profiles always appear even if the server wasn't ready on the initial activate() call.
    let tick = 0;
    const timer = setInterval(() => {
      void this.sendParticipants();
      if (++tick % 3 === 0) { void this.sendConfig(); }
    }, 10_000);
    this.panel.onDidDispose(() => clearInterval(timer));
  }

  static getFragment(): { css: string; html: string } {
    return {
      css: scopeViewCss(MAS_CSS, ModelAgentStudioPanel.containerId),
      html: `<div id="${ModelAgentStudioPanel.containerId}" class="nm-shell-pane">${MAS_HTML}</div>`,
    };
  }

  private async sendConfig(): Promise<void> {
    let pipelineProfiles: PipelineProfile[] = [];
    let domainAgents: DomainAgentInfo[] = [];
    let enabledDomainAgents: string[] = [];
    try {
      [pipelineProfiles, domainAgents] = await Promise.all([
        this.get<PipelineProfile[]>('/studio/agent-profiles'),
        this.get<DomainAgentInfo[]>('/studio/domain-agents'),
      ]);
      const opts = await this.get<{ enabledDomainAgents?: string[] }>('/studio/options');
      enabledDomainAgents = opts.enabledDomainAgents ?? [];
    } catch { /* server may not be running yet */ }
    const profiles = this.configService.getProfiles();
    const credentialStatus: Record<string, string> = {};
    await Promise.all(profiles.map(async p => {
      credentialStatus[p.id] = await this.configService.getCredentialStatus(p, this.secrets);
    }));
    void this.panel.webview.postMessage({
      type:                 'config',
      profiles,
      credentialStatus,
      templates:            this.configService.getTemplates(),
      defaultTopology:      this.configService.getDefaultTopology(),
      defaultTaskReviewPolicy:      this.configService.getDefaultTaskReviewPolicy(),
      defaultWorkspaceReviewPolicy: this.configService.getDefaultWorkspaceReviewPolicy(),
      pipelineProfiles,
      domainAgents,
      enabledDomainAgents,
      cliProviders: await this.fetchCliProviders(),
    });
  }

  // plans/harness-hosting-architecture.md Phase C.3 (phase-c-implementation.md C2) — the Model
  // Profile provider dropdown's CLI entries are data-driven from GET /studio/executors (shipped in
  // C1) rather than one hardcoded <option> per adapter, so a third CLI adapter needs no extension
  // edit. The three API providers (vscode-lm/openai/anthropic) stay static in modelAgentStudio.js —
  // they aren't IHarnessExecutor-backed, so there's nothing for that endpoint to describe about
  // them. Falls back to the known static CLI list if the endpoint can't be reached (server down).
  private async fetchCliProviders(): Promise<Array<{ providerKey: string; displayName: string }>> {
    try {
      const executors = await this.get<Array<{ providerKey?: string | null; displayName: string }>>('/studio/executors');
      const cli = executors
        .filter((e): e is { providerKey: string; displayName: string } => !!e.providerKey)
        .map(e => ({ providerKey: e.providerKey, displayName: e.displayName }));
      return cli.length > 0 ? cli : this.staticCliProviders();
    } catch {
      return this.staticCliProviders();
    }
  }

  private staticCliProviders(): Array<{ providerKey: string; displayName: string }> {
    return [
      { providerKey: 'claude-cli', displayName: 'Claude Code CLI' },
      { providerKey: 'codex-cli', displayName: 'Codex CLI' },
    ];
  }

  private async sendParticipants(): Promise<void> {
    try {
      const participants = await this.get<unknown[]>('/studio/participants');
      void this.panel.webview.postMessage({ type: 'participants', participants });
    } catch { /* server may not be running yet */ }
  }

  async handleMessage(msg: Record<string, unknown>): Promise<void> {
    switch (msg.type as string) {
      case 'saveProfiles':
        await this.configService.saveProfiles(msg.profiles as AgentProfile[]);
        void vscode.window.showInformationMessage('NodalMerge: Agent profiles saved.');
        this.onConfigChanged?.();
        break;

      case 'saveTemplates':
        await this.configService.saveTemplates(msg.templates as TopologyTemplate[]);
        void vscode.window.showInformationMessage('NodalMerge: Topology templates saved.');
        this.onConfigChanged?.();
        break;

      case 'setDefault':
        await this.configService.setDefaultTopology(msg.name as string);
        break;

      case 'setApiKey': {
        const profileId = msg.profileId as string;
        const key       = msg.key as string;
        const profiles  = this.configService.getProfiles();
        const profile   = profiles.find(p => p.id === profileId);
        if (profile && key) {
          await this.configService.storeApiKey(profile, key, this.secrets);
          const apiKeyRef = profile.apiKeyRef ?? `nodalmerge.apikey.${profileId}`;
          void vscode.window.showInformationMessage(`NodalMerge: API key stored for profile "${profileId}".`);
          void this.panel.webview.postMessage({ type: 'apiKeySaved', profileId, apiKeyRef });
        }
        break;
      }

      case 'removeApiKey': {
        const profileId = msg.profileId as string;
        const profile   = this.configService.getProfiles().find(p => p.id === profileId);
        if (profile) {
          const removedRef = await this.configService.removeApiKey(profile, this.secrets);
          // Only a persisted key needs host eviction, a toast, and a config-changed signal. When
          // nothing was stored (the button also serves as "clear the unsaved input"), removedRef is
          // undefined — skip all that and just refresh the row's key-status below.
          if (removedRef) {
            // Evict from the running host's in-memory credential cache — clearing it in settings
            // alone leaves it cached until a restart (Capture can't express removal).
            try {
              await this.post('/studio/credentials/evict', { credentialRef: removedRef });
            } catch { /* host may not be running — the next spawn re-captures from scratch anyway */ }
            void vscode.window.showInformationMessage(`NodalMerge: API key removed for profile "${profileId}".`);
            this.onConfigChanged?.();
          }
          void this.panel.webview.postMessage({ type: 'apiKeyRemoved', profileId });
        }
        break;
      }

      case 'savePipelineProfile': {
        const p = msg.profile as PipelineProfile;
        const exists = await this.get<unknown>('/studio/agent-profiles/' + p.agentProfileId).then(() => true).catch(() => false);
        const endpoint = '/studio/agent-profiles' + (exists ? '/' + p.agentProfileId : '');
        const method   = exists ? 'PUT' : 'POST';
        const body = exists
          ? { name: p.name, stage: p.stage, systemPrompt: p.systemPrompt, allowedTools: p.allowedTools, maxIterations: p.maxIterations, fileScopePatterns: p.fileScopePatterns, executor: p.executor, injectApiKeyEnv: p.injectApiKeyEnv ?? false }
          : { agentProfileId: p.agentProfileId, name: p.name, stage: p.stage, systemPrompt: p.systemPrompt, allowedTools: p.allowedTools, maxIterations: p.maxIterations, fileScopePatterns: p.fileScopePatterns, executor: p.executor, injectApiKeyEnv: p.injectApiKeyEnv ?? false };
        await (method === 'PUT'
          ? fetch(this.baseUrl + endpoint, { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) })
          : this.post(endpoint, body));
        void vscode.window.showInformationMessage(`NodalMerge: Pipeline profile "${p.agentProfileId}" saved.`);
        void this.sendConfig();
        break;
      }

      case 'getModels': {
        const models = await this.fetchModels(
          msg.provider as string,
          msg.baseUrl as string | undefined,
          msg.apiKey as string | undefined,
        );
        void this.panel.webview.postMessage({ type: 'models', models });
        break;
      }

      case 'saveSessionDefaults': {
        const taskPolicy      = msg.defaultTaskReviewPolicy as string;
        const workspacePolicy = msg.defaultWorkspaceReviewPolicy as string;
        const enabledDomainAgents = (msg.enabledDomainAgents as string[] | undefined) ?? [];
        if (taskPolicy) {
          await this.configService.saveDefaultTaskReviewPolicy(taskPolicy);
        }
        if (workspacePolicy) {
          await this.configService.saveDefaultWorkspaceReviewPolicy(workspacePolicy);
        }
        try {
          const currentOpts = await this.get<Record<string, unknown>>('/studio/options');
          await this.post('/studio/options', { ...currentOpts, enabledDomainAgents });
        } catch { /* host may not be running */ }
        void this.panel.webview.postMessage({
          type: 'sessionDefaults',
          defaultTaskReviewPolicy: taskPolicy,
          defaultWorkspaceReviewPolicy: workspacePolicy,
          enabledDomainAgents,
        });
        void vscode.window.showInformationMessage('NodalMerge: Session defaults saved.');
        this.onConfigChanged?.();
        break;
      }

      case 'refreshParticipants':
        void this.sendParticipants();
        break;

      case 'stopParticipant': {
        const id = msg.id as string;
        try {
          await fetch(this.baseUrl + '/studio/participants/' + encodeURIComponent(id), { method: 'POST' });
        } catch { /* host may not be running */ }
        setTimeout(() => void this.sendParticipants(), 1000);
        break;
      }

    }
  }

  private async fetchModels(provider: string, baseUrl?: string, apiKey?: string): Promise<string[]> {
    if (provider === 'vscode-lm') {
      try {
        const models = await vscode.lm.selectChatModels(undefined);
        return models.map(m => m.id);
      } catch { return []; }
    }
    if (provider === 'anthropic') {
      return [
        'claude-fable-5',
        'claude-opus-4-8',
        'claude-sonnet-4-6',
        'claude-haiku-4-5-20251001',
        'claude-3-5-sonnet-20241022',
        'claude-3-5-haiku-20241022',
      ];
    }
    if (provider === 'claude-cli') {
      // The CLI accepts aliases as well as full model ids; blank (manual entry left empty)
      // means "use the CLI's own configured default", so this list is suggestions only.
      return [
        'sonnet',
        'opus',
        'haiku',
        'claude-fable-5',
        'claude-opus-4-8',
        'claude-sonnet-4-6',
        'claude-haiku-4-5-20251001',
      ];
    }
    if (provider === 'codex-cli') {
      // Suggestions only, same as claude-cli — the CLI accepts aliases and full model ids, and
      // blank (manual entry left empty) means "use codex's own configured default". Not fetched
      // from a live endpoint (codex has no local model-listing API this extension calls today).
      return ['gpt-5-codex', 'o4-mini', '(blank = CLI default)'];
    }
    if (provider === 'openai' && baseUrl) {
      try {
        const url = `${baseUrl.replace(/\/+$/, '')}/v1/models`;
        const headers: Record<string, string> = {};
        if (apiKey) { headers['Authorization'] = `Bearer ${apiKey}`; }
        const res  = await fetch(url, { headers });
        if (!res.ok) { return []; }
        const data = await res.json() as { data?: Array<{ id: string }> };
        const ids  = (data.data ?? []).map((m: { id: string }) => m.id).sort();
        if (baseUrl.includes('openai.com')) {
          return ids.filter((id: string) => /^(gpt-|o\d)/.test(id));
        }
        return ids;
      } catch { return []; }
    }
    return [];
  }

  private async get<T>(path: string): Promise<T> {
    const res = await fetch(this.baseUrl + path);
    if (!res.ok) {
      const text = await res.text();
      throw new Error('GET ' + path + ' → ' + String(res.status) + ': ' + text);
    }
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

// ── HTML builder ───────────────────────────────────────────────────────────

const MAS_CSS = `
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
    /* Many themes leave --vscode-input-border unset (flat input design) or set it equal to the
       background, either of which reads as "no separation" against the surrounding UI. Derive a
       subtle border from the foreground color instead, so there's always some visible contrast
       regardless of what the active theme does with inputBorder. */
    --nm-input-bdr:  color-mix(in srgb, var(--nm-fg) 25%, transparent);
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
  .header .sub { font-size: 0.82em; opacity: 0.6; margin: 2px 0 0; }
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
  .checkbox-label { display: flex; align-items: center; gap: 8px; opacity: 1; font-size: 0.9em; margin-bottom: 0; }
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
  .hidden { display: none; }
  .flex-row { display:flex; gap:6px; align-items:center; }
  .readonly { opacity: 0.6; }
  .muted { font-size:0.82em; opacity:0.6; padding:6px 0; }
  .grow { flex: 1; }
  .save-bar .status { font-size: 0.8em; opacity: 0.5; flex: 1; }
  .explore-form { max-width: 440px; }
  .explore-form .field { margin-bottom: 12px; }
  .explore-result {
    margin-top: 14px; font-size: 0.88em; padding: 8px 12px; border-radius: 4px;
  }
  .explore-result.ok  { background: #1e3a1e; color: #7ec87e; }
  .explore-result.err { background: #3a1e1e; color: #f07070; }
  .default-badge {
    background: #2d5016; color: #8fc96a;
    border-radius: 10px; font-size: 0.74em; padding: 1px 7px; margin-left: 5px;
  }
  .chip {
    border-radius: 10px; font-size: 0.75em; padding: 1px 7px; display: inline-block;
  }
  .chip-running   { background: #1e3a1e; color: #7ec87e; }
  .chip-connected { background: #1e2d3a; color: #6eaef0; }
  .chip-idle      { background: #2a2a2a; color: #888; }
`;

const MAS_HTML = `
  <div class="header">
    <h1>Model & Agent Studio</h1>
    <p class="sub">Configure models, agent profiles, and agent topology.</p>
  </div>
  <div class="tabs">
    <button class="tab-btn active" data-tab="profiles">Profiles</button>
    <button class="tab-btn" data-tab="strategies">Agent Topology</button>
    <button class="tab-btn" data-tab="pipeline-profiles">Pipeline Profiles</button>
    <button class="tab-btn" data-tab="session-defaults">Session Defaults</button>
    <button class="tab-btn" data-tab="participants">Participants</button>
  </div>

  <div id="pane-profiles" class="tab-pane visible">
    <div id="profile-form-area"></div>
    <table>
      <thead><tr><th>ID</th><th>Label</th><th>Domain</th><th>Provider</th><th>Model</th><th></th></tr></thead>
      <tbody id="profile-tbody"></tbody>
    </table>
    <button class="add-btn" id="btn-add-profile">+ Add Profile</button>
  </div>

  <div id="pane-strategies" class="tab-pane">
    <div id="template-form-area"></div>
    <table>
      <thead><tr><th>Name</th><th>Orchestrator Profile</th><th>Planner Profile</th><th>Worker Profile</th><th>Reviewer Profile</th><th>Reconciler Profile</th><th></th></tr></thead>
      <tbody id="template-tbody"></tbody>
    </table>
    <button class="add-btn" id="btn-add-template">+ Add Topology</button>
  </div>

  <div id="pane-pipeline-profiles" class="tab-pane">
    <div id="pipeline-profile-form-area"></div>
    <table>
      <thead><tr><th>ID</th><th>Name</th><th>Stage</th><th>Tools</th><th>File Scope</th><th>Max Iter</th><th></th></tr></thead>
      <tbody id="pipeline-profile-tbody"></tbody>
    </table>
    <button class="add-btn" id="btn-add-pipeline-profile">+ Add Pipeline Profile</button>
  </div>

  <div id="pane-session-defaults" class="tab-pane">
    <div class="explore-form">
      <h3>Session Defaults</h3>
      <p class="sub">These defaults apply to new goals created in the Goal Workspace.</p>
      <div class="field">
        <label>Task Review</label>
        <p class="sub">Automatically integrates worker proposals into the agent session</p>
        <select id="default-task-review-policy">
          <option value="HumanRequired">Human Required — manual apply (default)</option>
          <option value="AgentApproval">Agent Approval — reviewer agent auto-merges</option>
          <option value="Hybrid">Hybrid — agent approves; auto-merges after a time delay</option>
        </select>
      </div>
      <div class="field">
        <label>Workspace Review</label>
        <p class="sub">Controls whether session changes are automatically applied to your workspace</p>
        <select id="default-workspace-review-policy">
          <option value="HumanRequired">Human Required — manual apply (default)</option>
          <option value="AgentApproval">Agent Approval — reviewer agent auto-merges</option>
          <option value="Hybrid">Hybrid — agent approves; auto-merges after a time delay</option>
        </select>
      </div>
      <div class="field">
        <label>Domain Agents</label>
        <p class="sub">Reactive observers that watch recorded Research/Decision/Constraint artifacts and may propose a Constraint back — they never own task lifecycle. Off by default.</p>
        <div id="domain-agent-toggles"></div>
      </div>
      <div style="display:flex;align-items:center;gap:8px;flex-wrap:wrap;">
        <button id="btn-save-session-defaults">Save Session Defaults</button>
      </div>
      <span id="session-defaults-status" class="status"></span>
    </div>
  </div>

  <div id="pane-participants" class="tab-pane">
    <div class="header-row" style="display:flex;align-items:center;gap:8px;margin-bottom:8px;">
      <h3 style="margin:0;flex:1">Live Participants</h3>
      <button class="ghost" id="btn-refresh-participants" style="font-size:0.8em">&#x21BB; Refresh</button>
    </div>
    <p class="sub">In-process agents and connected room peers. Agents show work-unit context; peers show their declared type.</p>
    <table>
      <thead><tr><th>ID</th><th>Kind</th><th>Status</th><th>Work Unit</th><th>Activity / Type</th><th></th></tr></thead>
      <tbody id="participant-tbody"></tbody>
    </table>
    <div id="participants-empty" class="muted" style="padding:8px 0">No participants — runtime may not be running.</div>
  </div>

  <div class="save-bar">
    <span class="status" id="save-status"></span>
    <button id="btn-save-profiles">Save Profiles</button>
    <button id="btn-save-strategies">Save Strategies</button>
  </div>
`;

