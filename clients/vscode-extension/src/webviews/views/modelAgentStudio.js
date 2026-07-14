// Extracted from src/panels/AgentConfigPanel.ts (MAS_JS) by scripts/extract-view.mjs.
// Body is the exact runtime string of the former inline <script>, with the historical
// scopeViewScript() rewrite (getElementById -> $, querySelector(All) -> root.*) baked in.
// One deliberate change from the inline original: the local esc() had lost its HTML
// entities (`.replace(/&/g, '&')` — a no-op), leaving markup injection open; it now
// uses the shared escaper.

import { esc } from './lib/esc.js';

/** @param {{ root: HTMLElement, vscode: { postMessage(m: any): void }, $: (id: string) => HTMLElement | null }} ctx */
export function init(ctx) {
  var root = ctx.root;
  var vscode = ctx.vscode;
  var $ = ctx.$;

  // vscode supplied by ctx (was: acquireVsCodeApi())

  let profiles = [];
  let credentialStatus = {};
  let templates = [];
  let defaultTopology = '';
  var onModelsLoaded = null;
  // plans/harness-hosting-architecture.md Phase C.3 (phase-c-implementation.md C2) — CLI provider
  // options for the Model Profile dropdown, sent by AgentConfigPanel.sendConfig() from
  // GET /studio/executors (data-driven, falls back to a static claude-cli/codex-cli list
  // server-side if that endpoint can't be reached) instead of one hardcoded <option> per adapter.
  var cliProviders = [];
  function isCliProviderKey(key) {
    return cliProviders.some(function(cp) { return cp.providerKey === key; });
  }
  function cliDisplayName(key) {
    var found = cliProviders.find(function(cp) { return cp.providerKey === key; });
    return found ? found.displayName : key;
  }

  // ── Tab switching ──────────────────────────────────────────────────────────
  root.querySelectorAll('.tab-btn').forEach(function(btn) {
    btn.addEventListener('click', function() {
      const tab = this.getAttribute('data-tab');
      root.querySelectorAll('.tab-btn').forEach(function(b) { b.classList.remove('active'); });
      root.querySelectorAll('.tab-pane').forEach(function(p) { p.classList.remove('visible'); });
      this.classList.add('active');
      const pane = $('pane-' + tab);
      if (pane) { pane.classList.add('visible'); }
    });
  });

  // ── Escape helper ──────────────────────────────────────────────────────────
  // esc() imported from ./lib/esc.js (local copy had broken entity replacements)

  // ── Status flash ──────────────────────────────────────────────────────────
  function setStatus(msg) {
    const el = $('save-status');
    if (el) {
      el.textContent = msg;
      setTimeout(function() { el.textContent = ''; }, 3000);
    }
  }

  // ── Profiles ──────────────────────────────────────────────────────────────
  function renderProfiles() {
    const tbody = $('profile-tbody');
    if (!tbody) { return; }
    tbody.innerHTML = '';
    profiles.forEach(function(p, i) {
      const tr = document.createElement('tr');
      const credWarning = credentialStatus[p.id] === 'secret-missing'
        ? ' <span title="API key was stored previously but is no longer in VS Code\'s secret storage — re-enter it." style="color:#e5a000">&#9888; key missing</span>'
        : '';
      tr.innerHTML =
        '<td class="mono">' + esc(p.id) + '</td>' +
        '<td>' + esc(p.label) + '</td>' +
        '<td class="mono">' + esc(p.domain) + '</td>' +
        '<td class="mono">' + esc(p.provider || 'anthropic') + credWarning + '</td>' +
        '<td class="mono">' + esc(p.model || '—') + '</td>' +
        '<td><div class="act-cell">' +
          '<button class="ghost" data-action="edit" data-idx="' + i + '">Edit</button>' +
          '<button class="danger" data-action="delete" data-idx="' + i + '">Delete</button>' +
        '</div></td>';
      tbody.appendChild(tr);
    });
  }

  $('profile-tbody').addEventListener('click', function(e) {
    const btn = e.target.closest('button');
    if (!btn) { return; }
    const idx    = parseInt(btn.getAttribute('data-idx'), 10);
    const action = btn.getAttribute('data-action');
    if (action === 'edit')   { showProfileForm(idx); }
    if (action === 'delete') { deleteProfile(idx); }
  });

  function deleteProfile(idx) {
    profiles.splice(idx, 1);
    $('profile-form-area').innerHTML = '';
    renderProfiles();
  }

  function showProfileForm(idx) {
    const isNew = idx === -1;
    const p = isNew
      ? { id: '', label: '', domain: '', deploymentMode: 'inline', provider: 'vscode-lm', model: '', baseUrl: '', systemPrompt: '', apiKeyRef: '' }
      : profiles[idx];
    const curProvider = p.provider || 'anthropic';
    const isVsLm = curProvider === 'vscode-lm';
    const isCli = isCliProviderKey(curProvider);
    const secretMissing = !isNew && credentialStatus[p.id] === 'secret-missing';
    const area = $('profile-form-area');
    const modelRowClass = 'field';
    const baseUrlRowClass = (isVsLm || isCli) ? 'field hidden' : 'field';
    const apiKeyRowClass = isVsLm ? 'field hidden' : 'field';
    const cliOptionsHtml = cliProviders.map(function(cp) {
      return '<option value="' + esc(cp.providerKey) + '"' + (curProvider === cp.providerKey ? ' selected' : '') +
        '>' + esc(cp.displayName) + ' (local binary — uses your CLI login)</option>';
    }).join('');
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
          '<option value="anthropic"' + (curProvider === 'anthropic' ? ' selected' : '') + '>Anthropic API (claude-*)</option>' +
          cliOptionsHtml +
        '</select></div>' +
      '<div id="pf-cli-note" class="field muted' + (isCli ? '' : ' hidden') + '">' +
        'Runs the role via the local <code>' + esc(cliDisplayName(curProvider)) + '</code> CLI in the branch working ' +
        'directory (assignable to any role, or as the topology\'s Default profile). Auth comes from that CLI\'s own login; ' +
        'storing an API key below is optional and switches that role to key-based auth. ' +
        'Leave Model blank to use the CLI\'s default.' +
      '</div>' +
      '<div id="pf-model-row" class="field">' +
        '<div style="display:flex;align-items:center;gap:6px;margin-bottom:3px">' +
          '<label style="margin:0;flex:1;font-size:0.8em;opacity:0.6">Model</label>' +
          '<button type="button" id="pf-refresh-models" class="ghost" style="padding:1px 8px;font-size:0.75em">&#x21BB; Refresh</button>' +
        '</div>' +
        '<select id="pf-model-select"><option value="__custom__">— enter manually —</option>' +
          (p.model ? '<option value="' + esc(p.model) + '" selected>' + esc(p.model) + '</option>' : '') +
        '</select>' +
        '<input type="text" id="pf-model-custom" style="margin-top:4px;' + (p.model ? 'display:none;' : '') + '" value="' + esc(p.model || '') + '" placeholder="' + (isVsLm ? 'blank = active VS Code model' : isCli ? 'blank = CLI default (or a CLI-specific alias/model id)' : 'e.g. claude-sonnet-4-6') + '">' +
        '<div id="pf-model-loading" class="muted hidden" style="font-size:0.8em;padding:2px 0">Fetching models…</div>' +
      '</div>' +
      '<div id="pf-baseurl-row" class="' + baseUrlRowClass + '">' +
        '<label>Base URL (leave blank for default)</label>' +
        '<input type="text" id="pf-baseurl" value="' + esc(p.baseUrl || '') + '"' +
        ' placeholder="' + (curProvider === 'openai' ? 'https://api.openai.com' : 'https://api.anthropic.com') + '"></div>' +
      '<div id="pf-apikey-row" class="' + apiKeyRowClass + '">' +
        '<label>API Key</label>' +
        '<div class="flex-row">' +
          '<input type="password" id="pf-apikey" placeholder="' + (secretMissing ? 'Key missing — paste key to re-store' : p.apiKeyRef ? '(key stored)' : 'Paste key to store') + '" class="grow">' +
          '<button id="pf-store-key" class="ghost">Store Key</button>' +
          '<button id="pf-remove-key" class="ghost">Remove Key</button>' +
        '</div>' +
        '<div id="pf-key-status" class="' + (secretMissing ? '' : 'muted') + '"' + (secretMissing ? ' style="color:#e5a000"' : '') + '>' +
          (secretMissing
            ? '&#9888; Key not found in secret storage (ref: ' + esc(p.apiKeyRef) + ') — this usually happens after an extension uninstall/reinstall. Paste the key above and click Store Key to fix it.'
            : (p.apiKeyRef ? 'Key stored (' + esc(p.apiKeyRef) + ')' : 'No key stored')) +
        '</div>' +
      '</div>' +
      '<div class="field"><label>Deployment Mode</label>' +
        '<select id="pf-deploy-mode">' +
          '<option value="inline"'   + ((p.deploymentMode || 'inline') === 'inline'   ? ' selected' : '') + '>inline — managed by this runtime (default)</option>' +
          '<option value="headless"' + ((p.deploymentMode || 'inline') === 'headless' ? ' selected' : '') + '>headless — standalone peer process (no vscode-lm)</option>' +
        '</select></div>' +
      (isVsLm ? '<div class="field muted">Uses your VS Code Copilot or Cursor subscription — no API key required.</div>' : '') +
      '<div class="field"><label>System Prompt (optional)</label>' +
        '<textarea id="pf-prompt">' + esc(p.systemPrompt || p.systemPromptHint || '') + '</textarea></div>' +
      '<div class="form-actions">' +
        '<button id="pf-save">Save</button>' +
        '<button class="ghost" id="pf-cancel">Cancel</button>' +
      '</div></div>';

    // Toggle field visibility when provider changes. Keep model visible; update its placeholder.
    $('pf-provider').addEventListener('change', function() {
      const isVs = this.value === 'vscode-lm';
      const cli  = isCliProviderKey(this.value);
      $('pf-baseurl-row').classList.toggle('hidden', isVs || cli);
      $('pf-apikey-row').classList.toggle('hidden', isVs);
      const noteEl = $('pf-cli-note');
      noteEl.classList.toggle('hidden', !cli);
      if (cli) {
        noteEl.innerHTML =
          'Runs the role via the local <code>' + esc(cliDisplayName(this.value)) + '</code> CLI in the branch ' +
          'working directory (assignable to any role, or as the topology\'s Default profile). Auth comes from that CLI\'s own login; ' +
          'storing an API key below is optional and switches that role to key-based auth. ' +
          'Leave Model blank to use the CLI\'s default.';
      }
      const m = $('pf-model-custom');
      if (m) {
        m.setAttribute('placeholder', isVs ? 'blank = active VS Code model'
          : cli ? 'blank = CLI default (or a CLI-specific alias/model id)'
          : 'e.g. claude-sonnet-4-6 or gpt-4o');
      }
      requestModels();
    });

    function requestModels() {
      var providerEl = $('pf-provider');
      var baseUrlEl  = $('pf-baseurl');
      var apiKeyEl   = $('pf-apikey');
      if (!providerEl) { return; }
      var loading = $('pf-model-loading');
      if (loading) { loading.classList.remove('hidden'); }
      vscode.postMessage({
        type:     'getModels',
        provider: providerEl.value,
        baseUrl:  baseUrlEl ? baseUrlEl.value.trim() : undefined,
        apiKey:   apiKeyEl  ? apiKeyEl.value.trim()  : undefined,
      });
    }
    function getModelValue() {
      var sel    = $('pf-model-select');
      var custom = $('pf-model-custom');
      if (sel && sel.value !== '__custom__') { return sel.value.trim(); }
      return custom ? custom.value.trim() : '';
    }
    onModelsLoaded = function(models) {
      var sel     = $('pf-model-select');
      var custom  = $('pf-model-custom');
      var loading = $('pf-model-loading');
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
    $('pf-model-select').addEventListener('change', function() {
      var custom = $('pf-model-custom');
      if (!custom) { return; }
      if (this.value === '__custom__') { custom.style.display = ''; }
      else { custom.style.display = 'none'; custom.value = this.value; }
    });
    $('pf-refresh-models').addEventListener('click', function() {
      requestModels();
    });

    $('pf-store-key').addEventListener('click', function() {
      const key = $('pf-apikey').value.trim();
      const id  = $('pf-id').value.trim() || (isNew ? '' : p.id);
      if (!key) { alert('Paste an API key first.'); return; }
      if (!id)  { alert('Save the profile ID first.'); return; }
      vscode.postMessage({ type: 'setApiKey', profileId: id, key: key });
      $('pf-apikey').value = '';
    });

    var removeKeyBtn = $('pf-remove-key');
    if (removeKeyBtn) {
      removeKeyBtn.addEventListener('click', function() {
        // Always clear the (possibly unsaved) input so "add a key, change your mind, remove it
        // before saving" works with no round-trip. Also ask the host to drop any *persisted* key
        // for this profile — a no-op server-side when nothing was ever stored (or the profile is
        // new and unsaved), so it's safe to fire unconditionally.
        $('pf-apikey').value = '';
        const id = $('pf-id').value.trim() || (isNew ? '' : p.id);
        if (!id) { return; }
        vscode.postMessage({ type: 'removeApiKey', profileId: id });
      });
    }

    $('pf-save').addEventListener('click', function() {
      const id           = $('pf-id').value.trim();
      const label        = $('pf-label').value.trim();
      const domain       = $('pf-domain').value.trim();
      const provider     = $('pf-provider').value;
      const deployMode   = $('pf-deploy-mode').value;
      const model        = getModelValue();
      const baseUrl      = (provider === 'vscode-lm' || isCliProviderKey(provider)) ? '' : $('pf-baseurl').value.trim();
      const prompt       = $('pf-prompt').value.trim();
      if (!id || !label || !domain) { alert('ID, Label, and Domain are required.'); return; }
      const keyEl     = $('pf-apikey');
      const pendingKey = (keyEl && provider !== 'vscode-lm') ? keyEl.value.trim() : '';
      const liveProfile = isNew ? null : profiles.find(function(pr) { return pr.id === p.id; });
      const existingRef = liveProfile ? liveProfile.apiKeyRef : (isNew ? undefined : p.apiKeyRef);
      const apiKeyRef   = provider === 'vscode-lm' ? undefined
        : (pendingKey ? ('nodalmerge.apikey.' + id) : existingRef);
      const profile = {
        id, label, domain, provider,
        deploymentMode:   deployMode === 'headless' ? 'headless' : undefined,
        model:            model   || undefined,
        baseUrl:          baseUrl || undefined,
        apiKeyRef:        apiKeyRef,
        systemPrompt:     prompt  || undefined,
      };
      if (isNew) { profiles.push(profile); }
      else       { profiles[idx] = profile; }
      onModelsLoaded = null;
      $('profile-form-area').innerHTML = '';
      renderProfiles();
      vscode.postMessage({ type: 'saveProfiles', profiles: profiles });
      if (pendingKey) {
        vscode.postMessage({ type: 'setApiKey', profileId: id, key: pendingKey });
      }
    });
    $('pf-cancel').addEventListener('click', function() {
      onModelsLoaded = null;
      $('profile-form-area').innerHTML = '';
    });
    setTimeout(function() { requestModels(); }, 0);
  }

  $('btn-add-profile').addEventListener('click', function() {
    showProfileForm(-1);
  });

  // ── Agent Topology ───────────────────────────────────────────────────────────
  function profileLabel(profileId) {
    if (!profileId) { return '— inherit Default —'; }
    const p = profiles.find(function(pr) { return pr.id === profileId; });
    return p ? p.label : profileId;
  }

  function renderTemplates() {
    const tbody = $('template-tbody');
    if (!tbody) { return; }
    tbody.innerHTML = '';
    templates.forEach(function(t, i) {
      const isDefault = t.name === defaultTopology;
      const tr = document.createElement('tr');
      tr.innerHTML =
        '<td>' + esc(t.name) + (isDefault ? '<span class="default-badge">default</span>' : '') + '</td>' +
        '<td class="mono">' + esc(profileLabel(t.orchestrator)) + '</td>' +
        '<td class="mono">' + esc(profileLabel(t.planner)) + '</td>' +
        '<td class="mono">' + esc(profileLabel(t.worker)) + '</td>' +
        '<td class="mono">' + esc(profileLabel(t.reviewer)) + '</td>' +
        '<td class="mono">' + esc(profileLabel(t.reconciler)) + '</td>' +
        '<td><div class="act-cell">' +
          (isDefault ? '' : '<button class="ghost" data-action="setDefault" data-idx="' + i + '">Set Default</button>') +
          '<button class="ghost" data-action="edit" data-idx="' + i + '">Edit</button>' +
          '<button class="danger" data-action="delete" data-idx="' + i + '">Delete</button>' +
        '</div></td>';
      tbody.appendChild(tr);
    });
  }

  $('template-tbody').addEventListener('click', function(e) {
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
    $('template-form-area').innerHTML = '';
    renderTemplates();
  }

  function profileOptions(selected, includeInherit) {
    const lead = includeInherit ? '<option value="">— inherit Default —</option>' : '';
    return lead + profiles.map(function(p) {
      const sel = p.id === selected ? ' selected' : '';
      return '<option value="' + esc(p.id) + '"' + sel + '>' + esc(p.label) + ' (' + esc(p.domain) + ')</option>';
    }).join('');
  }

  function showTemplateForm(idx) {
    const isNew = idx === -1;
    const t = isNew ? { name: '', orchestrator: '', planner: '', worker: '', reviewer: '', reconciler: '' } : templates[idx];
    const area = $('template-form-area');
    area.innerHTML =
      '<div class="form-box">' +
      '<h3>' + (isNew ? 'Add Topology' : 'Edit Topology') + '</h3>' +
      '<div class="field"><label>Name</label>' +
        '<input type="text" id="tmpl-name" value="' + esc(t.name) + '" placeholder="e.g. Default"></div>' +
      '<div class="field"><label>Default Profile <span style="opacity:0.6">(the goal\'s credential anchor — every unset role inherits it)</span></label>' +
        '<select id="tmpl-orch">' + profileOptions(t.orchestrator, false) + '</select></div>' +
      '<div class="field"><label>Planner Profile <span style="opacity:0.6">(optional — falls back to Default)</span></label>' +
        '<select id="tmpl-planner">' + profileOptions(t.planner, true) + '</select></div>' +
      '<div class="field"><label>Worker Profile <span style="opacity:0.6">(optional — falls back to Default)</span></label>' +
        '<select id="tmpl-worker">' + profileOptions(t.worker, true) + '</select></div>' +
      '<div class="field"><label>Reviewer Profile <span style="opacity:0.6">(optional — falls back to Default)</span></label>' +
        '<select id="tmpl-reviewer">' + profileOptions(t.reviewer, true) + '</select></div>' +
      '<div class="field"><label>Reconciler Profile <span style="opacity:0.6">(optional — falls back to Default; used to spawn conflict-reconciliation goals)</span></label>' +
        '<select id="tmpl-reconciler">' + profileOptions(t.reconciler, true) + '</select></div>' +
      '<div class="form-actions">' +
        '<button id="tmpl-save">Save</button>' +
        '<button class="ghost" id="tmpl-cancel">Cancel</button>' +
      '</div></div>';

    $('tmpl-save').addEventListener('click', function() {
      const name       = $('tmpl-name').value.trim();
      const orch       = $('tmpl-orch').value;
      const planner    = $('tmpl-planner').value;
      const worker     = $('tmpl-worker').value;
      const reviewer   = $('tmpl-reviewer').value;
      const reconciler = $('tmpl-reconciler').value;
      if (!name || !orch) { alert('Name and Default Profile are required.'); return; }
      const tmpl = {
        name: name,
        orchestrator: orch,
        planner: planner || undefined,
        worker: worker || undefined,
        reviewer: reviewer || undefined,
        reconciler: reconciler || undefined,
      };
      if (isNew) { templates.push(tmpl); }
      else       { templates[idx] = tmpl; }
      $('template-form-area').innerHTML = '';
      renderTemplates();
    });
    $('tmpl-cancel').addEventListener('click', function() {
      $('template-form-area').innerHTML = '';
    });
  }

  $('btn-add-template').addEventListener('click', function() {
    showTemplateForm(-1);
  });

  // ── Save ──────────────────────────────────────────────────────────────────
  $('btn-save-profiles').addEventListener('click', function() {
    vscode.postMessage({ type: 'saveProfiles', profiles: profiles });
    setStatus('Profiles saved.');
  });
  $('btn-save-strategies').addEventListener('click', function() {
    vscode.postMessage({ type: 'saveTemplates', templates: templates });
    setStatus('Strategies saved.');
  });

  // ── Session Defaults ──────────────────────────────────────────────────────
  var domainAgents = [];
  var enabledDomainAgents = [];

  function renderDomainAgentToggles() {
    var container = $('domain-agent-toggles');
    if (!container) { return; }
    container.innerHTML = domainAgents.map(function(d) {
      var checked = enabledDomainAgents.indexOf(d.name) >= 0 ? ' checked' : '';
      var keywords = (d.keywords || []).slice(0, 6).join(', ');
      return '<label style="display:flex;align-items:center;gap:6px;cursor:pointer;margin-bottom:4px;" title="' + esc(keywords) + '">' +
        '<input type="checkbox" class="domain-agent-toggle" data-name="' + esc(d.name) + '"' + checked + '> ' + esc(d.name) +
        '</label>';
    }).join('');
  }

  $('btn-save-session-defaults').addEventListener('click', function() {
    var taskSel = $('default-task-review-policy');
    var workspaceSel = $('default-workspace-review-policy');
    var taskPolicy = taskSel ? taskSel.value : 'HumanRequired';
    var workspacePolicy = workspaceSel ? workspaceSel.value : 'HumanRequired';
    var checkedAgents = Array.prototype.slice.call(root.querySelectorAll('.domain-agent-toggle:checked'))
      .map(function(el) { return el.getAttribute('data-name'); });
    vscode.postMessage({
      type: 'saveSessionDefaults',
      defaultTaskReviewPolicy: taskPolicy,
      defaultWorkspaceReviewPolicy: workspacePolicy,
      enabledDomainAgents: checkedAgents,
    });
    var statusEl = $('session-defaults-status');
    if (statusEl) { statusEl.textContent = 'Saved.'; setTimeout(function() { statusEl.textContent = ''; }, 2000); }
  });

  // ── Pipeline Profiles ─────────────────────────────────────────────────────
  var pipelineProfiles = [];
  var PIPELINE_STAGES = ['Orchestrate', 'Plan', 'Execute', 'Review', 'Merge', 'Reconcile'];

  function renderPipelineProfiles() {
    const tbody = $('pipeline-profile-tbody');
    if (!tbody) { return; }
    tbody.innerHTML = '';
    pipelineProfiles.forEach(function(p, i) {
      const toolCount = p.allowedTools && p.allowedTools.length > 0 ? p.allowedTools.length + ' tools' : 'all tools';
      const fileScope = p.fileScopePatterns && p.fileScopePatterns.length > 0 ? p.fileScopePatterns.join(', ') : '—';
      const tr = document.createElement('tr');
      tr.innerHTML =
        '<td class="mono">' + esc(p.agentProfileId) + '</td>' +
        '<td>' + esc(p.name) + '</td>' +
        '<td class="mono">' + esc(p.stage) + '</td>' +
        '<td class="mono">' + esc(toolCount) + '</td>' +
        '<td class="mono">' + esc(fileScope) + '</td>' +
        '<td class="mono">' + esc(String(p.maxIterations)) + '</td>' +
        '<td><div class="act-cell">' +
          '<button class="ghost" data-action="edit" data-idx="' + i + '">Edit</button>' +
        '</div></td>';
      tbody.appendChild(tr);
    });
  }

  $('pipeline-profile-tbody').addEventListener('click', function(e) {
    const btn = e.target.closest('button');
    if (!btn) { return; }
    const idx = parseInt(btn.getAttribute('data-idx'), 10);
    if (btn.getAttribute('data-action') === 'edit') { showPipelineProfileForm(idx); }
  });

  function showPipelineProfileForm(idx) {
    const isNew = idx === -1;
    const p = isNew
      ? { agentProfileId: '', name: '', stage: 'Execute', systemPrompt: '', allowedTools: [], maxIterations: 20, fileScopePatterns: [] }
      : pipelineProfiles[idx];
    const stageOptions = PIPELINE_STAGES.map(function(s) {
      return '<option value="' + s + '"' + (p.stage === s ? ' selected' : '') + '>' + s + '</option>';
    }).join('');
    const area = $('pipeline-profile-form-area');
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
      '<div class="field"><label>File Scope Patterns (comma-separated globs, empty = no declared specialty)</label>' +
        '<input type="text" id="pp-filescope" value="' + esc((p.fileScopePatterns || []).join(', ')) + '" placeholder="e.g. src/**/*.tsx, src/**/*.css"></div>' +
      '<div class="field"><label>Max Iterations</label>' +
        '<input type="text" id="pp-maxiter" value="' + esc(String(p.maxIterations)) + '" placeholder="20"></div>' +
      '<div class="field"><label>System Prompt (optional)</label>' +
        '<textarea id="pp-prompt" style="min-height:80px">' + esc(p.systemPrompt || '') + '</textarea></div>' +
      '<div class="form-actions">' +
        '<button id="pp-save">Save</button>' +
        '<button class="ghost" id="pp-cancel">Cancel</button>' +
      '</div></div>';

    $('pp-save').addEventListener('click', function() {
      const id       = $('pp-id').value.trim();
      const name     = $('pp-name').value.trim();
      const stage    = $('pp-stage').value;
      const toolsRaw = $('pp-tools').value.trim();
      const fileScopeRaw = $('pp-filescope').value.trim();
      const maxIter  = parseInt($('pp-maxiter').value.trim(), 10) || 20;
      const prompt   = $('pp-prompt').value.trim();
      if (!id || !name) { alert('Profile ID and Name are required.'); return; }
      const allowedTools = toolsRaw ? toolsRaw.split(',').map(function(s) { return s.trim(); }).filter(Boolean) : [];
      const fileScopePatterns = fileScopeRaw ? fileScopeRaw.split(',').map(function(s) { return s.trim(); }).filter(Boolean) : [];
      // executor/injectApiKeyEnv are not editable here (provider-driven via Model Profiles +
      // Agent Topology), but REST-set values must survive this form's PUT round-trip.
      const profile = { agentProfileId: id, name: name, stage: stage, systemPrompt: prompt, allowedTools: allowedTools, maxIterations: maxIter, fileScopePatterns: fileScopePatterns, executor: p.executor, injectApiKeyEnv: p.injectApiKeyEnv };
      vscode.postMessage({ type: 'savePipelineProfile', profile: profile });
      $('pipeline-profile-form-area').innerHTML = '';
    });
    $('pp-cancel').addEventListener('click', function() {
      $('pipeline-profile-form-area').innerHTML = '';
    });
  }

  $('btn-add-pipeline-profile').addEventListener('click', function() {
    showPipelineProfileForm(-1);
  });

  // ── Extension host messages ────────────────────────────────────────────────
  // ── Participants ──────────────────────────────────────────────────────────
  function renderParticipants(participants) {
    const tbody = $('participant-tbody');
    const empty = $('participants-empty');
    if (!tbody) { return; }
    tbody.innerHTML = '';
    if (!participants || participants.length === 0) {
      if (empty) { empty.style.display = ''; }
      return;
    }
    if (empty) { empty.style.display = 'none'; }
    participants.forEach(function(p) {
      const tr = document.createElement('tr');
      const statusClass = p.status === 'running' ? 'chip-running' : p.status === 'connected' ? 'chip-connected' : 'chip-idle';
      tr.innerHTML =
        '<td class="mono" style="max-width:120px;overflow:hidden;text-overflow:ellipsis" title="' + esc(p.id) + '">' + esc(p.id.substring(0, 12)) + (p.id.length > 12 ? '…' : '') + '</td>' +
        '<td><span class="chip ' + statusClass + '">' + esc(p.kind) + '</span></td>' +
        '<td><span class="chip ' + statusClass + '">' + esc(p.status) + '</span></td>' +
        '<td class="mono">' + esc(p.workUnitId || '—') + '</td>' +
        '<td class="mono">' + esc(p.currentActivity || p.peerType || '—') + '</td>' +
        '<td><button class="danger" data-stop-id="' + esc(p.id) + '">Stop</button></td>';
      tbody.appendChild(tr);
    });
  }

  $('participant-tbody').addEventListener('click', function(e) {
    const btn = e.target.closest('button[data-stop-id]');
    if (!btn) { return; }
    const id = btn.getAttribute('data-stop-id');
    if (id && confirm('Stop participant ' + id + '?')) {
      btn.disabled = true;
      vscode.postMessage({ type: 'stopParticipant', id: id });
    }
  });

  $('btn-refresh-participants').addEventListener('click', function() {
    vscode.postMessage({ type: 'refreshParticipants' });
  });

  window.addEventListener('message', function(event) {
    const msg = event.data;
    if (msg.type === 'models') {
      if (onModelsLoaded) { onModelsLoaded(msg.models || []); }
      return;
    }
    if (msg.type === 'config') {
      profiles         = msg.profiles        || [];
      credentialStatus = msg.credentialStatus || {};
      templates        = msg.templates       || [];
      defaultTopology  = msg.defaultTopology || '';
      pipelineProfiles = msg.pipelineProfiles || [];
      cliProviders     = msg.cliProviders || [];
      renderProfiles();
      renderTemplates();
      renderPipelineProfiles();
      var taskRpSel = $('default-task-review-policy');
      if (taskRpSel && msg.defaultTaskReviewPolicy) { taskRpSel.value = msg.defaultTaskReviewPolicy; }
      var workspaceRpSel = $('default-workspace-review-policy');
      if (workspaceRpSel && msg.defaultWorkspaceReviewPolicy) { workspaceRpSel.value = msg.defaultWorkspaceReviewPolicy; }
      domainAgents = msg.domainAgents || [];
      enabledDomainAgents = msg.enabledDomainAgents || [];
      renderDomainAgentToggles();
      return;
    }
    if (msg.type === 'apiKeySaved') {
      const statusEl = $('pf-key-status');
      if (statusEl) {
        statusEl.textContent = 'Key stored (' + esc(msg.apiKeyRef || msg.profileId) + ')';
        statusEl.className = 'muted';
        statusEl.removeAttribute('style');
      }
      if (msg.apiKeyRef) {
        const pi = profiles.findIndex(function(pr) { return pr.id === msg.profileId; });
        if (pi >= 0) { profiles[pi] = Object.assign({}, profiles[pi], { apiKeyRef: msg.apiKeyRef }); }
      }
      credentialStatus[msg.profileId] = 'ok';
      renderProfiles();
      return;
    }
    if (msg.type === 'apiKeyRemoved') {
      const statusEl = $('pf-key-status');
      if (statusEl) {
        statusEl.textContent = 'No key stored';
        statusEl.className = 'muted';
        statusEl.removeAttribute('style');
      }
      const pi = profiles.findIndex(function(pr) { return pr.id === msg.profileId; });
      if (pi >= 0) {
        var np = Object.assign({}, profiles[pi]);
        delete np.apiKeyRef;
        profiles[pi] = np;
      }
      delete credentialStatus[msg.profileId];
      renderProfiles();
      return;
    }
    if (msg.type === 'sessionDefaults') {
      var taskSel = $('default-task-review-policy');
      if (taskSel && msg.defaultTaskReviewPolicy) { taskSel.value = msg.defaultTaskReviewPolicy; }
      var workspaceSel = $('default-workspace-review-policy');
      if (workspaceSel && msg.defaultWorkspaceReviewPolicy) { workspaceSel.value = msg.defaultWorkspaceReviewPolicy; }
      if (msg.enabledDomainAgents) {
        enabledDomainAgents = msg.enabledDomainAgents;
        renderDomainAgentToggles();
      }
      return;
    }
    if (msg.type === 'participants') {
      renderParticipants(msg.participants || []);
      return;
    }
  });

}
