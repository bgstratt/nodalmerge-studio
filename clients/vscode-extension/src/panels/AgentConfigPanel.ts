import * as vscode from 'vscode';
import type { AgentConfigService, AgentProfile, TopologyTemplate } from '../AgentConfigService';

export class AgentConfigPanel implements vscode.Disposable {
  static current: AgentConfigPanel | undefined;
  private static readonly viewType = 'nodalmerge.agentConfig';

  private readonly panel: vscode.WebviewPanel;
  private readonly baseUrl: string;
  private readonly configService: AgentConfigService;
  private readonly secrets: vscode.SecretStorage;
  private readonly lmProxyBaseUrl: string;
  private readonly disposables: vscode.Disposable[] = [];

  private constructor(
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

    this.panel.webview.html = buildHtml();
    this.panel.onDidDispose(() => this.dispose(), null, this.disposables);
    this.panel.webview.onDidReceiveMessage(
      (msg: Record<string, unknown>) => { void this.handleMessage(msg); },
      null,
      this.disposables,
    );
    void this.sendConfig();
  }

  static createOrShow(
    baseUrl: string,
    configService: AgentConfigService,
    secrets: vscode.SecretStorage,
    lmProxyBaseUrl: string,
  ): void {
    if (AgentConfigPanel.current) {
      AgentConfigPanel.current.panel.reveal(vscode.ViewColumn.Two);
      void AgentConfigPanel.current.sendConfig();
      return;
    }
    const panel = vscode.window.createWebviewPanel(
      AgentConfigPanel.viewType,
      'NodalMerge — Agent Config',
      vscode.ViewColumn.Two,
      { enableScripts: true, retainContextWhenHidden: true },
    );
    AgentConfigPanel.current = new AgentConfigPanel(panel, baseUrl, configService, secrets, lmProxyBaseUrl);
  }

  private async sendConfig(): Promise<void> {
    void this.panel.webview.postMessage({
      type:            'config',
      profiles:        this.configService.getProfiles(),
      templates:       this.configService.getTemplates(),
      defaultTopology: this.configService.getDefaultTopology(),
    });
  }

  private async handleMessage(msg: Record<string, unknown>): Promise<void> {
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
          void vscode.window.showInformationMessage(`NodalMerge: API key stored for profile "${profileId}".`);
          void this.panel.webview.postMessage({ type: 'apiKeySaved', profileId });
        }
        break;
      }

      case 'quickSpawn':
        await this.handleQuickSpawn(msg.templateName as string, msg.goal as string);
        break;
    }
  }

  private async handleQuickSpawn(templateName: string, goal: string): Promise<void> {
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

      const orchWu = await this.post<{ workUnitId: string }>('/studio/workunits', {
        goal,
        owner: template.orchestrator,
      });
      await this.post('/studio/agents/spawn', {
        agentType:  template.orchestrator,
        workUnitId: orchWu.workUnitId,
        ...orchCfg,
      });

      for (const worker of template.workers ?? []) {
        const workerCfg = await this.configService.resolveSpawnLlmConfig(
          worker.profile, this.secrets, this.lmProxyBaseUrl,
        );
        if (!workerCfg) {
          throw new Error(`Profile "${worker.profile}" is missing LLM credentials (set VS Code LM or an API key).`);
        }
        const workerWu = await this.post<{ workUnitId: string }>('/studio/workunits', {
          goal:  `[${worker.profile}] ${goal}`,
          owner: worker.profile,
        });
        await this.post('/studio/agents/spawn', {
          agentType:  worker.profile,
          workUnitId: workerWu.workUnitId,
          ...workerCfg,
        });
      }

      void this.panel.webview.postMessage({ type: 'spawnResult', success: true });
      void vscode.window.showInformationMessage(
        `NodalMerge: Spawned "${templateName}" topology for: ${goal}`,
      );
    } catch (err) {
      void this.panel.webview.postMessage({
        type: 'spawnResult', success: false, message: String(err),
      });
      void vscode.window.showErrorMessage('NodalMerge: Quick spawn failed — ' + String(err));
    }
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

  dispose(): void {
    AgentConfigPanel.current = undefined;
    this.panel.dispose();
    for (const d of this.disposables) { d.dispose(); }
    this.disposables.length = 0;
  }
}

// ── HTML builder ───────────────────────────────────────────────────────────

function buildNonce(): string {
  let text = '';
  const chars = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789';
  for (let i = 0; i < 32; i++) { text += chars[Math.floor(Math.random() * chars.length)]; }
  return text;
}

function buildHtml(): string {
  const n = buildNonce();
  return [
    '<!DOCTYPE html>',
    '<html lang="en">',
    '<head>',
    '  <meta charset="UTF-8">',
    '  <meta http-equiv="Content-Security-Policy"',
    '        content="default-src \'none\'; style-src \'nonce-' + n + '\'; script-src \'nonce-' + n + '\';">',
    '  <meta name="viewport" content="width=device-width, initial-scale=1.0">',
    '  <title>Agent Config</title>',
    '  <style nonce="' + n + '">',
    AGENT_CONFIG_CSS,
    '  </style>',
    '</head>',
    '<body>',
    AGENT_CONFIG_HTML,
    '<script nonce="' + n + '">',
    AGENT_CONFIG_JS,
    '</script>',
    '</body>',
    '</html>',
  ].join('\n');
}

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
      <div id="spawn-result" class="spawn-result hidden"></div>
    </div>
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
    const modelRowClass = isVsLm ? 'field hidden' : 'field';
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
      '<div id="pf-model-row" class="' + modelRowClass + '">' +
        '<label>Model</label>' +
        '<input type="text" id="pf-model" value="' + esc(p.model || '') + '" placeholder="e.g. claude-sonnet-4-6 or gpt-4o"></div>' +
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

    // Toggle field visibility when provider changes
    document.getElementById('pf-provider').addEventListener('change', function() {
      const isVs = this.value === 'vscode-lm';
      document.getElementById('pf-model-row').classList.toggle('hidden', isVs);
      document.getElementById('pf-baseurl-row').classList.toggle('hidden', isVs);
      document.getElementById('pf-apikey-row').classList.toggle('hidden', isVs);
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
      vscode.postMessage({ type: 'saveProfiles', profiles: profiles });
    });
    document.getElementById('pf-cancel').addEventListener('click', function() {
      document.getElementById('profile-form-area').innerHTML = '';
    });
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
    this.disabled    = true;
    this.textContent = 'Spawning…';
    vscode.postMessage({ type: 'quickSpawn', templateName: templateName, goal: goal });
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

  // ── Extension host messages ────────────────────────────────────────────────
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
