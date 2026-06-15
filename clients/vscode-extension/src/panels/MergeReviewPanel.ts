import * as vscode from 'vscode';

// ── Domain types ───────────────────────────────────────────────────────────

export interface MergeProposal {
  proposalId: string;
  sourceBranch: string;
  targetBranch: string;
  goal: string;
  summary: string;
  changeDescription: string;
  verificationResults?: string | null;
  rollbackPlan?: string | null;
  confidence?: number | null;
  status: string;
}

// ── Panel ──────────────────────────────────────────────────────────────────

export class MergeReviewPanel implements vscode.Disposable {
  static current: MergeReviewPanel | undefined;
  private static readonly viewType = 'nodalmerge.mergeReview';

  private readonly panel: vscode.WebviewPanel;
  private readonly baseUrl: string;
  private readonly disposables: vscode.Disposable[] = [];
  private proposalId: string;

  private constructor(panel: vscode.WebviewPanel, baseUrl: string, proposalId: string) {
    this.panel = panel;
    this.baseUrl = baseUrl;
    this.proposalId = proposalId;
    this.panel.webview.html = buildHtml();
    this.panel.onDidDispose(() => this.dispose(), null, this.disposables);
    this.panel.webview.onDidReceiveMessage(
      (msg: Record<string, unknown>) => { void this.handleMessage(msg); },
      null,
      this.disposables
    );
    void this.load();
  }

  static createOrShow(baseUrl: string, proposalId: string): void {
    if (MergeReviewPanel.current) {
      MergeReviewPanel.current.proposalId = proposalId;
      MergeReviewPanel.current.panel.reveal(vscode.ViewColumn.Two);
      void MergeReviewPanel.current.load();
      return;
    }
    const panel = vscode.window.createWebviewPanel(
      MergeReviewPanel.viewType,
      'Merge Review',
      vscode.ViewColumn.Two,
      { enableScripts: true, retainContextWhenHidden: true }
    );
    MergeReviewPanel.current = new MergeReviewPanel(panel, baseUrl, proposalId);
  }

  private async load(): Promise<void> {
    try {
      const proposal = await this.get<MergeProposal>('/studio/merges/' + this.proposalId);
      this.panel.title = 'Merge Review: ' + proposal.sourceBranch;
      void this.panel.webview.postMessage({ type: 'proposal', proposal });
    } catch (err) {
      void vscode.window.showErrorMessage('NodalMerge: failed to load proposal — ' + String(err));
    }
  }

  private async handleMessage(msg: Record<string, unknown>): Promise<void> {
    try {
      switch (msg.type as string) {
        case 'validate':
          await this.post('/studio/merges/' + this.proposalId + '/validate', {});
          break;
        case 'approve':
          await this.post('/studio/merges/' + this.proposalId + '/review', { decision: 'Approved' });
          void vscode.window.showInformationMessage('Merge proposal approved.');
          break;
        case 'reject':
          await this.post('/studio/merges/' + this.proposalId + '/review', { decision: 'Rejected' });
          void vscode.window.showWarningMessage('Merge proposal rejected.');
          break;
        case 'apply':
          await this.post('/studio/merges/' + this.proposalId + '/apply', {});
          void vscode.window.showInformationMessage('Merge applied successfully.');
          break;
      }
      await this.load();
    } catch (err) {
      void vscode.window.showWarningMessage('NodalMerge: ' + String(err));
    }
  }

  private async get<T>(path: string): Promise<T> {
    const res = await fetch(this.baseUrl + path);
    if (!res.ok) {
      const text = await res.text();
      throw new Error('GET ' + path + ' → ' + String(res.status) + ': ' + text);
    }
    return res.json() as Promise<T>;
  }

  private async post(path: string, body: unknown): Promise<unknown> {
    const res = await fetch(this.baseUrl + path, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
    });
    if (!res.ok) {
      const text = await res.text();
      throw new Error('POST ' + path + ' → ' + String(res.status) + ': ' + text);
    }
    return res.json();
  }

  dispose(): void {
    MergeReviewPanel.current = undefined;
    this.panel.dispose();
    for (const d of this.disposables) { d.dispose(); }
    this.disposables.length = 0;
  }
}

// ── HTML ───────────────────────────────────────────────────────────────────

function buildNonce(): string {
  let s = '';
  const c = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789';
  for (let i = 0; i < 32; i++) { s += c[Math.floor(Math.random() * c.length)]; }
  return s;
}

function buildHtml(): string {
  const n = buildNonce();
  return [
    '<!DOCTYPE html><html lang="en"><head>',
    '<meta charset="UTF-8">',
    '<meta http-equiv="Content-Security-Policy"',
    '      content="default-src \'none\'; style-src \'nonce-' + n + '\'; script-src \'nonce-' + n + '\';">',
    '<meta name="viewport" content="width=device-width, initial-scale=1.0">',
    '<title>Merge Review</title>',
    '<style nonce="' + n + '">' + REVIEW_CSS + '</style>',
    '</head><body>',
    REVIEW_HTML,
    '<script nonce="' + n + '">' + REVIEW_JS + '</script>',
    '</body></html>',
  ].join('\n');
}

const REVIEW_CSS = `
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
  .hidden { display: none; }
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

const REVIEW_HTML = `
  <div id="loading">Loading proposal…</div>
  <div id="content" class="hidden">
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
    <section id="section-verification" class="hidden">
      <h2>Verification results</h2>
      <p id="verification-results"></p>
    </section>
    <section id="section-rollback" class="hidden">
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

const REVIEW_JS = `
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
    if (el) el.classList.toggle('hidden', !cond);
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

    var loadingEl = document.getElementById('loading');
    var contentEl = document.getElementById('content');
    if (loadingEl) loadingEl.classList.add('hidden');
    if (contentEl) contentEl.classList.remove('hidden');

    setText('title', 'Merge Review: ' + (p.sourceBranch || ''));
    var badgeClass = 'badge ' + status;
    setHtml('status-badge', '<span class="' + badgeClass + '">' + esc(p.status) + '</span>');
    setText('source-branch', p.sourceBranch);
    setText('target-branch', p.targetBranch);
    setText('confidence', p.confidence != null ? (Math.round(p.confidence * 100) + '%') : '—');
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
