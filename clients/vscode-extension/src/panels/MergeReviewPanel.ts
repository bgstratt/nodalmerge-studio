import * as vscode from 'vscode';
import { scopeViewCss, wrapViewScript } from './sharedWebviewChrome';

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
  workspaceChanges?: string | null;
  confidence?: number | null;
  status: string;
  reconciledFrom?: string[];
  supersededBy?: string | null;
}

export interface ProposalFileChange {
  path: string;
  changeKind: 'Added' | 'Modified' | 'Deleted';
  beforeContent?: string | null;
  afterContent?: string | null;
}

export interface ConstituentProposal {
  proposalId: string;
  status: string;
  goal?: string | null;
  summary?: string | null;
}

// ── Panel ──────────────────────────────────────────────────────────────────

export class MergeReviewPanel {
  static readonly containerId = 'shell-pane-merge-review';

  private readonly panel: vscode.WebviewPanel;
  private readonly baseUrl: string;
  private mode: 'proposal' | 'conflict' = 'proposal';
  private proposalId?: string;
  private workUnitId?: string;

  constructor(panel: vscode.WebviewPanel, baseUrl: string) {
    this.panel = panel;
    this.baseUrl = baseUrl;
  }

  /** Slice 0 — was createOrShow(); the Studio Shell now owns the one WebviewPanel, so this
   * just points this view at a proposal and tells the already-open shell to show this tab. */
  loadProposal(proposalId: string): void {
    this.mode = 'proposal';
    this.proposalId = proposalId;
    this.workUnitId = undefined;
    void this.load();
  }

  // Conflict mode: the merger found overlapping changes among child proposals and escalated
  // the parent work unit to Reviewing without producing a proposal — there's nothing to
  // approve/reject/apply yet, just the conflict report to surface (11c).
  loadConflict(workUnitId: string): void {
    this.mode = 'conflict';
    this.workUnitId = workUnitId;
    this.proposalId = undefined;
    void this.load();
  }

  static getFragment(): { css: string; html: string; script: string } {
    return {
      css: scopeViewCss(REVIEW_CSS, MergeReviewPanel.containerId),
      html: `<div id="${MergeReviewPanel.containerId}" class="nm-shell-pane">${REVIEW_HTML}</div>`,
      script: wrapViewScript(REVIEW_JS, MergeReviewPanel.containerId),
    };
  }

  private async load(): Promise<void> {
    if (this.mode === 'conflict') {
      try {
        const report = await this.get<{ workUnitId: string; status: string; content: string }>(
          '/studio/workunits/' + this.workUnitId + '/conflict-report');
        void this.panel.webview.postMessage({
          type: 'conflict',
          workUnitId: report.workUnitId,
          status: report.status,
          content: report.content,
        });
      } catch (err) {
        void vscode.window.showErrorMessage('NodalMerge: failed to load conflict report — ' + String(err));
      }
      return;
    }

    try {
      const proposal = await this.get<MergeProposal>('/studio/merges/' + this.proposalId);
      const changesRes = await this.get<{ fileChanges: ProposalFileChange[] }>(
        '/studio/merges/' + this.proposalId + '/file-changes');
      const constituents = (proposal.reconciledFrom && proposal.reconciledFrom.length)
        ? await this.get<ConstituentProposal[]>('/studio/merges/' + this.proposalId + '/constituents')
        : [];
      void this.panel.webview.postMessage({
        type: 'proposal',
        proposal,
        fileChanges: changesRes.fileChanges ?? [],
        constituents,
      });
    } catch (err) {
      void vscode.window.showErrorMessage('NodalMerge: failed to load proposal — ' + String(err));
    }
  }

  // Slice 0 — the Studio Shell broadcasts every webview message to every view's handleMessage,
  // since the 4 views' message-type vocabularies don't overlap (verified while planning). The
  // `default: return` is load-bearing here specifically: unlike the other 3 views, this one
  // unconditionally reloads after a matched case, so an unmatched (i.e. not-mine) message type
  // must bail out before reaching that reload, or every other view's message would also
  // trigger a needless (and, before a proposal/conflict is ever loaded, erroring) re-fetch here.
  async handleMessage(msg: Record<string, unknown>): Promise<void> {
    try {
      switch (msg.type as string) {
        case 'openDiff': {
          const path = String(msg.path ?? 'file');
          const before = (msg.beforeContent as string | null | undefined) ?? '';
          const after = (msg.afterContent as string | null | undefined) ?? '';
          const lang = path.includes('.') ? path.split('.').pop() : 'plaintext';
          const left = await vscode.workspace.openTextDocument({ language: lang, content: before });
          const right = await vscode.workspace.openTextDocument({ language: lang, content: after });
          const title = path + ' (base ↔ proposed)';
          await vscode.commands.executeCommand('vscode.diff', left.uri, right.uri, title);
          break;
        }
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
        default:
          return;
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

}

// ── HTML ───────────────────────────────────────────────────────────────────

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
  .diff-pre {
    font-family: var(--nm-mono);
    font-size: 0.85em;
    margin: 0;
    overflow-x: auto;
    line-height: 1.45;
    white-space: pre;
  }
  .diff-add  { color: var(--nm-success); }
  .diff-del  { color: var(--nm-error); }
  .diff-meta { color: var(--nm-info); opacity: 0.7; }
  .reconciled-banner {
    border-left: 3px solid var(--nm-info);
    padding: 8px 12px;
    margin: 12px 0;
    background: rgba(55, 148, 255, 0.08);
    font-size: 0.9em;
  }
  .constituent-row {
    display: flex;
    align-items: baseline;
    gap: 8px;
    margin-top: 6px;
    font-size: 0.92em;
  }
  .constituent-row .mono { font-family: var(--nm-mono); opacity: 0.7; }
  .badge.superseded { background: #555; color: #ccc; }
  .file-change {
    border: 1px solid var(--nm-border);
    border-radius: 4px;
    margin: 8px 0;
    overflow: hidden;
  }
  .file-change summary {
    cursor: pointer;
    padding: 8px 12px;
    font-family: var(--nm-mono);
    font-size: 0.9em;
    background: rgba(128,128,128,0.08);
  }
  .file-change-body {
    display: grid;
    grid-template-columns: 1fr 1fr;
    gap: 0;
    border-top: 1px solid var(--nm-border);
  }
  .file-pane {
    padding: 8px 10px;
    min-width: 0;
  }
  .file-pane h3 {
    font-size: 0.72em;
    text-transform: uppercase;
    opacity: 0.55;
    margin: 0 0 6px;
  }
  .file-pane + .file-pane { border-left: 1px solid var(--nm-border); }
  .file-pane.added-only { grid-column: 1 / -1; }
  .verification-approved {
    border-left: 3px solid var(--nm-success);
    padding: 8px 12px;
    background: rgba(35, 134, 54, 0.12);
    color: var(--nm-success);
    font-weight: 600;
  }
  .verification-rejected {
    border-left: 3px solid var(--nm-error);
    padding: 8px 12px;
    background: rgba(241, 76, 76, 0.12);
    color: var(--nm-error);
    font-weight: 600;
  }
  button.ghost {
    background: transparent;
    border: 1px solid var(--nm-border);
    margin: 8px 12px 12px;
  }
`;

const REVIEW_HTML = `
  <div id="loading">Loading proposal…</div>
  <div id="content" class="hidden">
    <h1 id="title">Merge Review</h1>
    <div id="meta-grid" class="meta-grid">
      <span class="meta-label">Status</span>      <span id="status-badge"></span>
      <span class="meta-label">Source branch</span><span class="meta-value" id="source-branch"></span>
      <span class="meta-label">Target branch</span><span class="meta-value" id="target-branch"></span>
      <span class="meta-label">Confidence</span>  <span class="meta-value" id="confidence"></span>
    </div>
    <section id="section-reconciled" class="hidden reconciled-banner">
      <strong>Reconciled proposal</strong> — combined from <span id="reconciled-count"></span> child proposal(s).
      <div id="reconciled-from"></div>
    </section>
    <section id="section-conflict-report" class="hidden">
      <h2>Merge conflict</h2>
      <pre id="conflict-report-content" class="diff-pre"></pre>
      <p style="opacity:0.7;font-size:0.9em">
        Resolve manually: edit the conflicting files on the affected branches outside this panel,
        then re-run the merger for this work unit.
      </p>
    </section>
    <section id="section-goal">
      <h2>Goal</h2>
      <p id="goal"></p>
    </section>
    <section id="section-summary">
      <h2>Summary</h2>
      <p id="summary"></p>
    </section>
    <section id="section-change">
      <h2>Change description</h2>
      <p id="change-description"></p>
    </section>
    <section id="section-files" class="hidden">
      <h2>File changes</h2>
      <p style="opacity:0.7;font-size:0.9em;margin:0 0 8px">Review concrete before/after content per file. Use Open Diff for the VS Code side-by-side editor.</p>
      <div id="file-changes"></div>
    </section>
    <section id="section-diff" class="hidden">
      <h2>Combined diff summary</h2>
      <pre id="diff-content" class="diff-pre"></pre>
    </section>
    <section id="section-verification" class="hidden">
      <h2>Automated review</h2>
      <div id="verification-results"></div>
    </section>
    <section id="section-rollback" class="hidden">
      <h2>Rollback plan</h2>
      <p id="rollback-plan"></p>
    </section>
    <div id="actions" class="actions">
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

  function renderDiff(text) {
    return String(text).split('\\n').map(function(line) {
      var cls = line.startsWith('+') ? 'diff-add'
              : line.startsWith('-') ? 'diff-del'
              : line.startsWith('@@') ? 'diff-meta'
              : '';
      return cls ? '<span class="' + cls + '">' + esc(line) + '</span>' : esc(line);
    }).join('\\n');
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

  function renderConstituents(constituents, fallbackIds) {
    var byId = {};
    (constituents || []).forEach(function(c) { byId[c.proposalId] = c; });
    return (fallbackIds || []).map(function(id) {
      var c = byId[id];
      if (!c) {
        return '<div class="constituent-row"><span class="mono">' + esc(id) + '</span></div>';
      }
      var statusKey = (c.status || '').toLowerCase().replace(/\\s+/g, '');
      return '<div class="constituent-row">'
        + '<span class="badge ' + statusKey + '">' + esc(c.status) + '</span>'
        + '<span class="mono">' + esc(c.proposalId) + '</span>'
        + (c.goal ? '<span>' + esc(c.goal) + '</span>' : '')
        + '</div>';
    }).join('');
  }

  function renderFileChanges(changes) {
    if (!changes || !changes.length) return '';
    return changes.map(function(fc, idx) {
      var kind = (fc.changeKind || '').toLowerCase();
      var isAdded = kind === 'added';
      var isDeleted = kind === 'deleted';
      var bodyClass = isAdded ? 'file-change-body added-only' : 'file-change-body';
      var before = isAdded ? '' : esc(fc.beforeContent || '(empty)');
      var after = isDeleted ? '' : esc(fc.afterContent || '(empty)');
      var html = '<details class="file-change" open>';
      html += '<summary>' + esc(fc.path) + ' <span class="badge">' + esc(fc.changeKind) + '</span></summary>';
      html += '<div class="' + bodyClass + '">';
      if (!isAdded) {
        html += '<div class="file-pane"><h3>Before (base)</h3><pre class="diff-pre">' + before + '</pre></div>';
      }
      if (!isDeleted) {
        html += '<div class="file-pane"><h3>After (proposed)</h3><pre class="diff-pre">' + after + '</pre></div>';
      }
      html += '</div>';
      if (!isDeleted) {
        html += '<button class="ghost" data-open-diff="' + idx + '">Open Diff in Editor</button>';
      }
      html += '</details>';
      return html;
    }).join('');
  }

  document.addEventListener('click', function(ev) {
    var btn = ev.target.closest('[data-open-diff]');
    if (!btn) return;
    var idx = parseInt(btn.getAttribute('data-open-diff'), 10);
    var fc = window.__fileChanges && window.__fileChanges[idx];
    if (!fc) return;
    vscode.postMessage({
      type: 'openDiff',
      path: fc.path,
      beforeContent: fc.beforeContent,
      afterContent: fc.afterContent
    });
  });

  function showProposalSections(show) {
    showIf('meta-grid', show);
    showIf('section-goal', show);
    showIf('section-summary', show);
    showIf('section-change', show);
    showIf('actions', show);
  }

  window.addEventListener('message', function(event) {
    var msg = event.data;

    if (msg.type === 'conflict') {
      var loadingEl2 = document.getElementById('loading');
      var contentEl2 = document.getElementById('content');
      if (loadingEl2) loadingEl2.classList.add('hidden');
      if (contentEl2) contentEl2.classList.remove('hidden');

      setText('title', 'Merge Conflict: ' + (msg.workUnitId || ''));
      showProposalSections(false);
      showIf('section-reconciled', false);
      showIf('section-files', false);
      showIf('section-diff', false);
      showIf('section-verification', false);
      showIf('section-rollback', false);
      showIf('section-conflict-report', true);
      setText('conflict-report-content', msg.content || '');
      return;
    }

    if (msg.type !== 'proposal') { return; }
    showProposalSections(true);
    showIf('section-conflict-report', false);
    var p = msg.proposal;
    var fileChanges = msg.fileChanges || [];
    window.__fileChanges = fileChanges;
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

    var verificationEl = document.getElementById('verification-results');
    if (verificationEl && p.verificationResults) {
      var isRejected = status === 'rejected';
      var label = isRejected
        ? 'Automated Review: Rejected: ' + esc(p.verificationResults)
        : 'Automated Review: Approved — ' + esc(p.verificationResults);
      verificationEl.className = isRejected ? 'verification-rejected' : 'verification-approved';
      verificationEl.textContent = isRejected
        ? 'Automated Review: Rejected: ' + (p.verificationResults || '')
        : 'Automated Review: Approved — ' + (p.verificationResults || '');
    } else if (verificationEl) {
      verificationEl.className = '';
      verificationEl.textContent = '';
    }

    setText('rollback-plan', p.rollbackPlan);

    var reconciled = p.reconciledFrom && p.reconciledFrom.length;
    showIf('section-reconciled', !!reconciled);
    if (reconciled) {
      setText('reconciled-count', String(p.reconciledFrom.length));
      setHtml('reconciled-from', renderConstituents(msg.constituents || [], p.reconciledFrom));
    }

    setHtml('file-changes', renderFileChanges(fileChanges));
    showIf('section-files', fileChanges.length > 0);

    if (p.workspaceChanges) { setHtml('diff-content', renderDiff(p.workspaceChanges)); }

    showIf('section-diff', !!p.workspaceChanges);
    showIf('section-verification', !!p.verificationResults);
    showIf('section-rollback', !!p.rollbackPlan);

    var btns = STATUS_BUTTONS[status] || { validate: false, approve: false, reject: false, apply: false };
    setDisabled('btn-validate', !btns.validate);
    setDisabled('btn-approve',  !btns.approve);
    setDisabled('btn-reject',   !btns.reject);
    setDisabled('btn-apply',    !btns.apply);
  });
`;
