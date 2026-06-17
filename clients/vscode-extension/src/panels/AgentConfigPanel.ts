import * as vscode from 'vscode';
import type { AgentConfigService, AgentProfile, TopologyTemplate } from '../AgentConfigService';
import { scopeViewCss, wrapViewScript } from './sharedWebviewChrome';

export interface PipelineProfile {
  agentProfileId: string;
  name: string;
  stage: string;
  systemPrompt: string;
  allowedTools: string[];
  maxIterations: number;
}

export class AgentConfigPanel {
  static readonly containerId = 'shell-pane-agent-config';

  private readonly panel: vscode.WebviewPanel;
  private readonly baseUrl: string;
  private readonly configService: AgentConfigService;
  private readonly secrets: vscode.SecretStorage;
  private readonly lmProxyBaseUrl: string;

  constructor(
    panel: vscode.WebviewPanel,
    baseUrl: string,
    configService: AgentConfigService,
    secrets: vscode.SecretStorage,
    lmProxyBaseUrl: string,
  ) {
    this.panel          = panel;
    this.baseUrl        = baseUrl;
    this.configService  = configService;
    this.secrets        = secrets;
    this.lmProxyBaseUrl = lmProxyBaseUrl;
  }

  /** Called once by the shell right after construction — was the tail of createOrShow(). */
  activate(): void {
    void this.sendConfig();
  }

  static getFragment(): { css: string; html: string; script: string } {
    return {
      css: scopeViewCss(AGENT_CONFIG_CSS, AgentConfigPanel.containerId),
      html: `<div id="${AgentConfigPanel.containerId}" class="nm-shell-pane">${AGENT_CONFIG_HTML}</div>`,
      script: wrapViewScript(AGENT_CONFIG_JS, AgentConfigPanel.containerId),
    };
  }

  private async sendConfig(): Promise<void> {
    let pipelineProfiles: PipelineProfile[] = [];
    try {
      pipelineProfiles = await this.get<PipelineProfile[]>('/studio/agent-profiles');
    } catch { /* server may not be running yet */ }
    void this.panel.webview.postMessage({
      type:            'config',
      profiles:        this.configService.getProfiles(),
      templates:       this.configService.getTemplates(),
      defaultTopology: this.configService.getDefaultTopology(),
      pipelineProfiles,
    });
  }

  async handleMessage(msg: Record<string, unknown>): Promise<void> {
    switch (msg.type as string) {
      case 'saveProfiles':
        await this.configService.saveProfiles(msg.profiles as AgentProfile[]);
        void vscode.window.showInformationMessage('NodalMerge: Agent profiles saved.');
        break;

      case 'saveTemplates':
        await this.configService.saveTemplates(msg.templates as TopologyTemplate[]);
        void vscode.window.showInformationMessage('NodalMerge: Topology templates saved.');
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

      case 'savePipelineProfile': {
        const p = msg.profile as PipelineProfile;
        const exists = await this.get<unknown>('/studio/agent-profiles/' + p.agentProfileId).then(() => true).catch(() => false);
        const endpoint = '/studio/agent-profiles' + (exists ? '/' + p.agentProfileId : '');
        const method   = exists ? 'PUT' : 'POST';
        const body = exists
          ? { name: p.name, stage: p.stage, systemPrompt: p.systemPrompt, allowedTools: p.allowedTools, maxIterations: p.maxIterations }
          : { agentProfileId: p.agentProfileId, name: p.name, stage: p.stage, systemPrompt: p.systemPrompt, allowedTools: p.allowedTools, maxIterations: p.maxIterations };
        await (method === 'PUT'
          ? fetch(this.baseUrl + endpoint, { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) })
          : this.post(endpoint, body));
        void vscode.window.showInformationMessage(`NodalMerge: Pipeline profile "${p.agentProfileId}" saved.`);
        void this.sendConfig();
        break;
      }

      case 'quickSpawn':
        await this.handleQuickSpawn(
          msg.templateName as string,
          msg.goal as string,
          msg.autoReviewProfileId as string | undefined,
        );
        break;

      case 'getModels': {
        const models = await this.fetchModels(
          msg.provider as string,
          msg.baseUrl as string | undefined,
          msg.apiKey as string | undefined,
        );
        void this.panel.webview.postMessage({ type: 'models', models });
        break;
      }
    }
  }

  private async handleQuickSpawn(templateName: string, goal: string, autoReviewProfileId?: string): Promise<void> {
    const templates = this.configService.getTemplates();
    const template  = templates.find(t => t.name === templateName);
    if (!template) {
      void vscode.window.showErrorMessage(`NodalMerge: Template "${templateName}" not found.`);
      void this.panel.webview.postMessage({ type: 'spawnResult', success: false, message: 'Template not found.' });
      return;
    }

    try {
      const orchCfg = await this.configService.resolveSpawnLlmConfig(
        template.orchestrator, this.secrets, this.lmProxyBaseUrl,
      );
      if (!orchCfg) {
        const orchProfile = this.configService.getProfiles().find(pr => pr.id === template.orchestrator);
        if (orchProfile?.provider === 'vscode-lm') {
          throw new Error(
            `Profile "${template.orchestrator}": VS Code LM proxy is not running. Reload the extension window.`,
          );
        }
        throw new Error(
          `Profile "${template.orchestrator}" is missing LLM credentials — set Provider to VS Code LM ` +
          `(Edit → Save on the form → Save Profiles), or configure base URL + API key.`,
        );
      }

      const repositoryPath = vscode.workspace.workspaceFolders?.[0]?.uri?.fsPath;
      const orchWu = await this.post<{ workUnitId: string }>('/studio/workunits', {
        goal,
        owner: template.orchestrator,
        ...(repositoryPath ? { repositoryPath } : {}),
      });
      // Always use 'orchestrator' as the agentType so the server starts the correct loop,
      // regardless of what the user named their LLM profile.  Workers are spawned by
      // the orchestrator via nm.v1.agent.spawn once it has created tasks with taskIds.
      await this.post('/studio/agents/spawn', {
        agentType:  'orchestrator',
        workUnitId: orchWu.workUnitId,
        ...orchCfg,
        ...(autoReviewProfileId ? { autoReviewProfileId } : {}),
      });

      void this.panel.webview.postMessage({ type: 'spawnResult', success: true });
      void vscode.window.showInformationMessage(
        `NodalMerge: Spawned orchestrator for "${templateName}": ${goal}`,
      );
    } catch (err) {
      void this.panel.webview.postMessage({
        type: 'spawnResult', success: false, message: String(err),
      });
      void vscode.window.showErrorMessage('NodalMerge: Quick spawn failed — ' + String(err));
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

const AGENT_CONFIG_CSS = `
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

const AGENT_CONFIG_HTML = `
  <div class="header"><h1>Agent Config</h1></div>
  <div class="tabs">
    <button class="tab-btn active" data-tab="profiles">Profiles</button>
    <button class="tab-btn" data-tab="templates">Topology Templates</button>
    <button class="tab-btn" data-tab="spawn">Quick Spawn</button>
    <button class="tab-btn" data-tab="pipeline-profiles">Pipeline Profiles</button>
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
      <div class="field">
        <label class="checkbox-label">
          <input type="checkbox" id="spawn-auto-review" checked>
          Run automated review before human gate
        </label>
      </div>
      <button id="btn-spawn">&#x25B6; Quick Spawn</button>
      <div id="spawn-result" class="spawn-result hidden"></div>
    </div>
  </div>

  <div id="pane-pipeline-profiles" class="tab-pane">
    <div id="pipeline-profile-form-area"></div>
    <table>
      <thead><tr><th>ID</th><th>Name</th><th>Stage</th><th>Tools</th><th>Max Iter</th><th></th></tr></thead>
      <tbody id="pipeline-profile-tbody"></tbody>
    </table>
    <button class="add-btn" id="btn-add-pipeline-profile">+ Add Pipeline Profile</button>
  </div>

  <div class="save-bar">
    <span class="status" id="save-status"></span>
    <button id="btn-save-profiles">Save Profiles</button>
    <button id="btn-save-templates">Save Templates</button>
  </div>
`;

const AGENT_CONFIG_JS = `
  const vscode = acquireVsCodeApi();

  let profiles = [];
  let templates = [];
  let defaultTopology = '';
  var onModelsLoaded = null;

  // ── Tab switching ──────────────────────────────────────────────────────────
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

  // ── Escape helper ──────────────────────────────────────────────────────────
  function esc(str) {
    return String(str || '')
      .replace(/&/g, '&amp;').replace(/</g, '&lt;')
      .replace(/>/g, '&gt;').replace(/"/g, '&quot;').replace(/'/g, '&#39;');
  }

  // ── Status flash ──────────────────────────────────────────────────────────
  function setStatus(msg) {
    const el = document.getElementById('save-status');
    if (el) {
      el.textContent = msg;
      setTimeout(function() { el.textContent = ''; }, 3000);
    }
  }

  // ── Profiles ──────────────────────────────────────────────────────────────
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
        '<td class="mono">' + esc(p.model || '—') + '</td>' +
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
    // Always show the model field so users can supply a model hint for vscode-lm.
    const modelRowClass = 'field';
    const baseUrlRowClass = isVsLm ? 'field hidden' : 'field';
    const apiKeyRowClass = isVsLm ? 'field hidden' : 'field';
    area.innerHTML =
      '<div class="form-box">' +
      '<h3>' + (isNew ? 'Add Profile' : 'Edit Profile') + '</h3>' +
      '<div class="field"><label>ID (agent type key)</label>' +
        '<input type="text" id="pf-id" value="' + esc(p.id) + '"' +
        (isNew ? '' : ' readonly class="readonly"') +
        ' placeholder="e.g. worker"></div>' +
      '<div class="field"><label>Display Label</label>' +
        '<input type="text" id="pf-label" value="' + esc(p.label) + '" placeholder="e.g. Worker Agent"></div>' +
      '<div class="field"><label>Domain</label>' +
        '<input type="text" id="pf-domain" value="' + esc(p.domain) + '" placeholder="e.g. code, docs, general"></div>' +
      '<div class="field"><label>LLM Provider</label>' +
        '<select id="pf-provider">' +
          '<option value="vscode-lm"' + (curProvider === 'vscode-lm' ? ' selected' : '') + '>VS Code LM (Copilot / Cursor — no key needed)</option>' +
          '<option value="openai"'    + (curProvider === 'openai'    ? ' selected' : '') + '>OpenAI compatible (OpenAI, DeepSeek, Azure, LM Studio, etc.)</option>' +
          '<option value="anthropic"' + (curProvider === 'anthropic' ? ' selected' : '') + '>Anthropic (claude-*)</option>' +
        '</select></div>' +
      '<div id="pf-model-row" class="field">' +
        '<div style="display:flex;align-items:center;gap:6px;margin-bottom:3px">' +
          '<label style="margin:0;flex:1;font-size:0.8em;opacity:0.6">Model</label>' +
          '<button type="button" id="pf-refresh-models" class="ghost" style="padding:1px 8px;font-size:0.75em">&#x21BB; Refresh</button>' +
        '</div>' +
        '<select id="pf-model-select"><option value="__custom__">— enter manually —</option>' +
          (p.model ? '<option value="' + esc(p.model) + '" selected>' + esc(p.model) + '</option>' : '') +
        '</select>' +
        '<input type="text" id="pf-model-custom" style="margin-top:4px;' + (p.model ? 'display:none;' : '') + '" value="' + esc(p.model || '') + '" placeholder="' + (isVsLm ? 'blank = active VS Code model' : 'e.g. claude-sonnet-4-6') + '">' +
        '<div id="pf-model-loading" class="muted hidden" style="font-size:0.8em;padding:2px 0">Fetching models…</div>' +
      '</div>' +
      '<div id="pf-baseurl-row" class="' + baseUrlRowClass + '">' +
        '<label>Base URL (leave blank for default)</label>' +
        '<input type="text" id="pf-baseurl" value="' + esc(p.baseUrl || '') + '"' +
        ' placeholder="' + (curProvider === 'openai' ? 'https://api.openai.com' : 'https://api.anthropic.com') + '"></div>' +
      '<div id="pf-apikey-row" class="' + apiKeyRowClass + '">' +
        '<label>API Key</label>' +
        '<div class="flex-row">' +
          '<input type="password" id="pf-apikey" placeholder="' + (p.apiKeyRef ? '(key stored)' : 'Paste key to store') + '" class="grow">' +
          '<button id="pf-store-key" class="ghost">Store Key</button>' +
        '</div>' +
        '<div id="pf-key-status" class="muted">' +
          (p.apiKeyRef ? 'Key stored (' + esc(p.apiKeyRef) + ')' : 'No key stored') +
        '</div>' +
      '</div>' +
      (isVsLm ? '<div class="field muted">Uses your VS Code Copilot or Cursor subscription — no API key required.</div>' : '') +
      '<div class="field"><label>System Prompt Hint (optional)</label>' +
        '<textarea id="pf-prompt">' + esc(p.systemPromptHint || '') + '</textarea></div>' +
      '<div class="form-actions">' +
        '<button id="pf-save">Save</button>' +
        '<button class="ghost" id="pf-cancel">Cancel</button>' +
      '</div></div>';

    // Toggle field visibility when provider changes. Keep model visible; update its placeholder.
    document.getElementById('pf-provider').addEventListener('change', function() {
      const isVs = this.value === 'vscode-lm';
      document.getElementById('pf-baseurl-row').classList.toggle('hidden', isVs);
      document.getElementById('pf-apikey-row').classList.toggle('hidden', isVs);
      const m = document.getElementById('pf-model-custom');
      if (m) {
        m.setAttribute('placeholder', isVs ? 'blank = active VS Code model' : 'e.g. claude-sonnet-4-6 or gpt-4o');
      }
      requestModels();
    });

    function requestModels() {
      var providerEl = document.getElementById('pf-provider');
      var baseUrlEl  = document.getElementById('pf-baseurl');
      var apiKeyEl   = document.getElementById('pf-apikey');
      if (!providerEl) { return; }
      var loading = document.getElementById('pf-model-loading');
      if (loading) { loading.classList.remove('hidden'); }
      vscode.postMessage({
        type:     'getModels',
        provider: providerEl.value,
        baseUrl:  baseUrlEl ? baseUrlEl.value.trim() : undefined,
        apiKey:   apiKeyEl  ? apiKeyEl.value.trim()  : undefined,
      });
    }
    function getModelValue() {
      var sel    = document.getElementById('pf-model-select');
      var custom = document.getElementById('pf-model-custom');
      if (sel && sel.value !== '__custom__') { return sel.value.trim(); }
      return custom ? custom.value.trim() : '';
    }
    onModelsLoaded = function(models) {
      var sel     = document.getElementById('pf-model-select');
      var custom  = document.getElementById('pf-model-custom');
      var loading = document.getElementById('pf-model-loading');
      if (loading) { loading.classList.add('hidden'); }
      if (!sel) { return; }
      var currentVal = custom ? custom.value.trim() : '';
      sel.innerHTML  = '<option value="__custom__">— enter manually —</option>';
      models.forEach(function(id) {
        var opt = document.createElement('option');
        opt.value = id; opt.textContent = id;
        if (id === currentVal) { opt.selected = true; }
        sel.appendChild(opt);
      });
      if (sel.value === '__custom__') {
        if (custom) { custom.style.display = ''; }
      } else {
        if (custom) { custom.style.display = 'none'; custom.value = sel.value; }
      }
    };
    document.getElementById('pf-model-select').addEventListener('change', function() {
      var custom = document.getElementById('pf-model-custom');
      if (!custom) { return; }
      if (this.value === '__custom__') { custom.style.display = ''; }
      else { custom.style.display = 'none'; custom.value = this.value; }
    });
    document.getElementById('pf-refresh-models').addEventListener('click', function() {
      requestModels();
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
      // Always capture the model field; an empty value will mean "use active VS Code model".
      const model    = getModelValue();
      const baseUrl  = provider === 'vscode-lm' ? '' : document.getElementById('pf-baseurl').value.trim();
      const prompt   = document.getElementById('pf-prompt').value.trim();
      if (!id || !label || !domain) { alert('ID, Label, and Domain are required.'); return; }
      // If a key was typed in the password field, store it when saving — no need for a separate "Store Key" click.
      const keyEl     = document.getElementById('pf-apikey');
      const pendingKey = (keyEl && provider !== 'vscode-lm') ? keyEl.value.trim() : '';
      // Determine apiKeyRef: use the pending key's deterministic ref, OR carry forward the existing ref from the live profiles array.
      const liveProfile = isNew ? null : profiles.find(function(pr) { return pr.id === p.id; });
      const existingRef = liveProfile ? liveProfile.apiKeyRef : (isNew ? undefined : p.apiKeyRef);
      const apiKeyRef   = provider === 'vscode-lm' ? undefined
        : (pendingKey ? ('nodalmerge.apikey.' + id) : existingRef);
      const profile = {
        id, label, domain, provider,
        model:            model   || undefined,
        baseUrl:          baseUrl || undefined,
        apiKeyRef:        apiKeyRef,
        systemPromptHint: prompt  || undefined,
      };
      if (isNew) { profiles.push(profile); }
      else       { profiles[idx] = profile; }
      onModelsLoaded = null;
      document.getElementById('profile-form-area').innerHTML = '';
      renderProfiles();
      vscode.postMessage({ type: 'saveProfiles', profiles: profiles });
      // Store the key AFTER saving the profile so the ref is already in place.
      if (pendingKey) {
        vscode.postMessage({ type: 'setApiKey', profileId: id, key: pendingKey });
      }
    });
    document.getElementById('pf-cancel').addEventListener('click', function() {
      onModelsLoaded = null;
      document.getElementById('profile-form-area').innerHTML = '';
    });
    setTimeout(function() { requestModels(); }, 0);
  }

  document.getElementById('btn-add-profile').addEventListener('click', function() {
    showProfileForm(-1);
  });

  // ── Templates ─────────────────────────────────────────────────────────────
  function renderTemplates() {
    const tbody = document.getElementById('template-tbody');
    if (!tbody) { return; }
    tbody.innerHTML = '';
    templates.forEach(function(t, i) {
      const workers   = (t.workers || []).map(function(w) { return w.profile; }).join(', ') || '—';
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

  // ── Spawn template selector ────────────────────────────────────────────────
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

  // ── Quick Spawn ────────────────────────────────────────────────────────────
  document.getElementById('btn-spawn').addEventListener('click', function() {
    const templateName = document.getElementById('spawn-template').value;
    const goal = document.getElementById('spawn-goal').value.trim();
    if (!goal) { alert('Goal is required.'); return; }
    const autoReview = document.getElementById('spawn-auto-review').checked;
    this.disabled    = true;
    this.textContent = 'Spawning…';
    vscode.postMessage({
      type: 'quickSpawn',
      templateName: templateName,
      goal: goal,
      autoReviewProfileId: autoReview ? 'reviewer' : undefined
    });
  });

  // ── Save ──────────────────────────────────────────────────────────────────
  document.getElementById('btn-save-profiles').addEventListener('click', function() {
    vscode.postMessage({ type: 'saveProfiles', profiles: profiles });
    setStatus('Profiles saved.');
  });
  document.getElementById('btn-save-templates').addEventListener('click', function() {
    vscode.postMessage({ type: 'saveTemplates', templates: templates });
    setStatus('Templates saved.');
  });

  // ── Pipeline Profiles ─────────────────────────────────────────────────────
  var pipelineProfiles = [];
  var PIPELINE_STAGES = ['Orchestrate', 'Plan', 'Execute', 'Review', 'Merge'];

  function renderPipelineProfiles() {
    const tbody = document.getElementById('pipeline-profile-tbody');
    if (!tbody) { return; }
    tbody.innerHTML = '';
    pipelineProfiles.forEach(function(p, i) {
      const toolCount = p.allowedTools && p.allowedTools.length > 0 ? p.allowedTools.length + ' tools' : 'all tools';
      const tr = document.createElement('tr');
      tr.innerHTML =
        '<td class="mono">' + esc(p.agentProfileId) + '</td>' +
        '<td>' + esc(p.name) + '</td>' +
        '<td class="mono">' + esc(p.stage) + '</td>' +
        '<td class="mono">' + esc(toolCount) + '</td>' +
        '<td class="mono">' + esc(String(p.maxIterations)) + '</td>' +
        '<td><div class="act-cell">' +
          '<button class="ghost" data-action="edit" data-idx="' + i + '">Edit</button>' +
        '</div></td>';
      tbody.appendChild(tr);
    });
  }

  document.getElementById('pipeline-profile-tbody').addEventListener('click', function(e) {
    const btn = e.target.closest('button');
    if (!btn) { return; }
    const idx = parseInt(btn.getAttribute('data-idx'), 10);
    if (btn.getAttribute('data-action') === 'edit') { showPipelineProfileForm(idx); }
  });

  function showPipelineProfileForm(idx) {
    const isNew = idx === -1;
    const p = isNew
      ? { agentProfileId: '', name: '', stage: 'Execute', systemPrompt: '', allowedTools: [], maxIterations: 20 }
      : pipelineProfiles[idx];
    const stageOptions = PIPELINE_STAGES.map(function(s) {
      return '<option value="' + s + '"' + (p.stage === s ? ' selected' : '') + '>' + s + '</option>';
    }).join('');
    const area = document.getElementById('pipeline-profile-form-area');
    area.innerHTML =
      '<div class="form-box">' +
      '<h3>' + (isNew ? 'Add Pipeline Profile' : 'Edit Pipeline Profile') + '</h3>' +
      '<div class="field"><label>Profile ID</label>' +
        '<input type="text" id="pp-id" value="' + esc(p.agentProfileId) + '"' +
        (isNew ? '' : ' readonly class="readonly"') +
        ' placeholder="e.g. planner"></div>' +
      '<div class="field"><label>Name</label>' +
        '<input type="text" id="pp-name" value="' + esc(p.name) + '" placeholder="e.g. Planner"></div>' +
      '<div class="field"><label>Pipeline Stage</label>' +
        '<select id="pp-stage">' + stageOptions + '</select></div>' +
      '<div class="field"><label>Allowed Tools (comma-separated, empty = all tools)</label>' +
        '<input type="text" id="pp-tools" value="' + esc((p.allowedTools || []).join(', ')) + '" placeholder="e.g. nm_v1_task_create, nm_v1_task_list"></div>' +
      '<div class="field"><label>Max Iterations</label>' +
        '<input type="text" id="pp-maxiter" value="' + esc(String(p.maxIterations)) + '" placeholder="20"></div>' +
      '<div class="field"><label>System Prompt (optional)</label>' +
        '<textarea id="pp-prompt" style="min-height:80px">' + esc(p.systemPrompt || '') + '</textarea></div>' +
      '<div class="form-actions">' +
        '<button id="pp-save">Save</button>' +
        '<button class="ghost" id="pp-cancel">Cancel</button>' +
      '</div></div>';

    document.getElementById('pp-save').addEventListener('click', function() {
      const id       = document.getElementById('pp-id').value.trim();
      const name     = document.getElementById('pp-name').value.trim();
      const stage    = document.getElementById('pp-stage').value;
      const toolsRaw = document.getElementById('pp-tools').value.trim();
      const maxIter  = parseInt(document.getElementById('pp-maxiter').value.trim(), 10) || 20;
      const prompt   = document.getElementById('pp-prompt').value.trim();
      if (!id || !name) { alert('Profile ID and Name are required.'); return; }
      const allowedTools = toolsRaw ? toolsRaw.split(',').map(function(s) { return s.trim(); }).filter(Boolean) : [];
      const profile = { agentProfileId: id, name: name, stage: stage, systemPrompt: prompt, allowedTools: allowedTools, maxIterations: maxIter };
      vscode.postMessage({ type: 'savePipelineProfile', profile: profile });
      document.getElementById('pipeline-profile-form-area').innerHTML = '';
    });
    document.getElementById('pp-cancel').addEventListener('click', function() {
      document.getElementById('pipeline-profile-form-area').innerHTML = '';
    });
  }

  document.getElementById('btn-add-pipeline-profile').addEventListener('click', function() {
    showPipelineProfileForm(-1);
  });

  // ── Extension host messages ────────────────────────────────────────────────
  window.addEventListener('message', function(event) {
    const msg = event.data;
    if (msg.type === 'models') {
      if (onModelsLoaded) { onModelsLoaded(msg.models || []); }
      return;
    }
    if (msg.type === 'config') {
      profiles         = msg.profiles        || [];
      templates        = msg.templates       || [];
      defaultTopology  = msg.defaultTopology || '';
      pipelineProfiles = msg.pipelineProfiles || [];
      renderProfiles();
      renderTemplates();
      updateSpawnTemplates();
      renderPipelineProfiles();
      return;
    }
    if (msg.type === 'apiKeySaved') {
      const statusEl = document.getElementById('pf-key-status');
      if (statusEl) { statusEl.textContent = 'Key stored (' + esc(msg.apiKeyRef || msg.profileId) + ')'; }
      // Update the in-memory profiles array so a subsequent Save preserves the apiKeyRef.
      if (msg.apiKeyRef) {
        const pi = profiles.findIndex(function(pr) { return pr.id === msg.profileId; });
        if (pi >= 0) { profiles[pi] = Object.assign({}, profiles[pi], { apiKeyRef: msg.apiKeyRef }); }
      }
      return;
    }
    if (msg.type === 'spawnResult') {
      const btn = document.getElementById('btn-spawn');
      btn.disabled    = false;
      btn.textContent = '\\u25B6 Quick Spawn';
      const result = document.getElementById('spawn-result');
      if (result) {
        result.classList.remove('hidden');
        result.className     = 'spawn-result ' + (msg.success ? 'ok' : 'err');
        result.textContent   = msg.success ? 'Spawned successfully!' : ('Error: ' + (msg.message || 'unknown'));
      }
      if (msg.success) {
        const g = document.getElementById('spawn-goal');
        if (g && 'value' in g) { g.value = ''; }
        setTimeout(function() { if (result) result.classList.add('hidden'); }, 5000);
      }
    }
  });
`;
