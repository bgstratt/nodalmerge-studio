import * as vscode from 'vscode';
import { scopeViewCss, wrapViewScript } from './sharedWebviewChrome';
import type { AgentConfigService } from '../AgentConfigService';

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
  autoApplied?: boolean;
  filesTouched?: string[];
  noFileChangesJustification?: string | null;
  reviewNotes?: string | null;
}

export interface DiffLine {
  kind: 'Context' | 'Added' | 'Removed';
  beforeLineNumber?: number | null;
  afterLineNumber?: number | null;
  text: string;
}

export interface DiffHunk {
  beforeStart: number;
  beforeCount: number;
  afterStart: number;
  afterCount: number;
  lines: DiffLine[];
}

export interface ProposalFileChange {
  path: string;
  changeKind: 'Added' | 'Modified' | 'Deleted';
  beforeContent?: string | null;
  afterContent?: string | null;
  hunks: DiffHunk[];
}

export interface ConstituentProposal {
  proposalId: string;
  status: string;
  goal?: string | null;
  summary?: string | null;
  model?: string | null;
  provider?: string | null;
  confidence?: number | null;
  rationale?: string | null;
  agentId?: string | null;
  workspaceChanges?: string | null;
}

// Phase 9f — mirrors NodalMerge.Studio.Contracts.Domain.ProjectRoot/WorkspaceProfile.
export interface ProjectRoot {
  relativePath: string;   // "" = branch root itself
  stack: string;
  buildCommand?: string | null;
  testCommand?: string | null;
  runCommand?: string | null;
  isLongRunning: boolean;
}

export interface WorkspaceProfile {
  branchId: string;
  roots: ProjectRoot[];
  detectedAt: string;
}

// ── Panel ──────────────────────────────────────────────────────────────────

export class DecisionConvergencePanel {
  static readonly containerId = 'shell-pane-decision-convergence';

  private readonly panel: vscode.WebviewPanel;
  private readonly baseUrl: string;
  private readonly configService: AgentConfigService | undefined;
  private readonly getSelectedSessionId?: () => string | undefined;
  private localSessionOverride?: string;
  private mode: 'proposal' | 'conflict' = 'proposal';
  private proposalId?: string;
  private workUnitId?: string;
  private lastProposal?: MergeProposal;

  constructor(
    panel: vscode.WebviewPanel,
    baseUrl: string,
    configService?: AgentConfigService,
    getSelectedSessionId?: () => string | undefined,
  ) {
    this.panel = panel;
    this.baseUrl = baseUrl;
    this.configService = configService;
    this.getSelectedSessionId = getSelectedSessionId;
  }

  activate(): void {
    void this.sendSessionPicker();
  }

  loadProposal(proposalId: string): void {
    this.mode = 'proposal';
    this.proposalId = proposalId;
    this.workUnitId = undefined;
    void this.load();
  }

  loadConflict(workUnitId: string): void {
    this.mode = 'conflict';
    this.workUnitId = workUnitId;
    this.proposalId = undefined;
    void this.load();
  }

  /** Reloads the currently loaded proposal/conflict, or loads latest pending — used by the shell when the selected session changes. */
  async triggerReload(): Promise<void> {
    void this.sendSessionPicker();
    if (this.proposalId) {
      void this.load();
    } else if (this.workUnitId) {
      void this.load();
    } else {
      await this.loadLatestPending();
    }
  }

  setSessionOverride(sessionId: string | undefined): void {
    this.localSessionOverride = sessionId;
    void this.sendSessionPicker();
    void this.triggerReload();
  }

  private getEffectiveSessionId(): string | undefined {
    return this.localSessionOverride ?? this.getSelectedSessionId?.();
  }

  private async sendSessionPicker(): Promise<void> {
    try {
      const sessions = await this.get<Array<{ sessionId: string; status: string }>>('/studio/sessions');
      void this.panel.webview.postMessage({
        type: 'updateSessionPicker',
        panelId: DecisionConvergencePanel.containerId,
        sessions,
        shellSessionId: this.getSelectedSessionId?.(),
        overrideSessionId: this.localSessionOverride,
      });
    } catch { /* host not ready */ }
  }

  static getFragment(): { css: string; html: string; script: string } {
    return {
      css: scopeViewCss(DC_CSS, DecisionConvergencePanel.containerId),
      html: `<div id="${DecisionConvergencePanel.containerId}" class="nm-shell-pane">${DC_HTML}</div>`,
      script: wrapViewScript(DC_JS, DecisionConvergencePanel.containerId),
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
      this.lastProposal = proposal;
      const changesRes = await this.get<{ fileChanges: ProposalFileChange[] }>(
        '/studio/merges/' + this.proposalId + '/file-changes');
      const constituents = (proposal.reconciledFrom && proposal.reconciledFrom.length)
        ? await this.get<ConstituentProposal[]>('/studio/merges/' + this.proposalId + '/constituents')
        : [];
      // Phase 9f — per-root Build/Test/Run controls need the detected project roots.
      // Best-effort: a repo this can't profile (or a host predating Phase 9) still gets the
      // single-row "repo root" fallback the webview renders for an empty roots array.
      let roots: ProjectRoot[] = [];
      try {
        const profile = await this.get<WorkspaceProfile>(
          '/studio/workspace/profile?branchId=' + encodeURIComponent(proposal.sourceBranch));
        roots = profile.roots ?? [];
      } catch { /* fall back to single "repo root" row */ }
      await this.panel.webview.postMessage({
        type: 'proposal',
        proposal,
        fileChanges: changesRes.fileChanges ?? [],
        constituents,
        roots,
      });
    } catch (err) {
      const msg = 'NodalMerge: failed to load proposal — ' + String(err);
      void vscode.window.showErrorMessage(msg);
      void this.panel.webview.postMessage({ type: 'loadError', error: msg });
    }
  }

  private async loadLatestPending(): Promise<void> {
    try {
      const sessionId = this.getEffectiveSessionId();
      const proposals = await this.get<MergeProposal[]>('/studio/merges' + (sessionId ? '?sessionId=' + encodeURIComponent(sessionId) : ''));
      const pending = proposals.find(p => {
        const status = (p.status || '').toLowerCase();
        return status === 'readyforreview' || status === 'approved' || status === 'draft' || status === 'proposed' || status === 'executing' || status === 'merge';
      });
      if (pending) {
        this.loadProposal(pending.proposalId);
        return;
      }

      const sessionParams = sessionId ? '?sessionId=' + encodeURIComponent(sessionId) : '';
      const workUnits = await this.get<{ workUnitId: string; status: string }[]>('/studio/workunits' + sessionParams);
      const reviewing = workUnits.find(wu => (wu.status || '').toLowerCase() === 'reviewing');
      if (reviewing) {
        this.loadConflict(reviewing.workUnitId);
        return;
      }

      // No pending proposals or conflicts — tell the webview
      void this.panel.webview.postMessage({ type: 'noPending' });
    } catch (err) {
      void vscode.window.showWarningMessage('NodalMerge: failed to check for a pending decision — ' + String(err));
    }
  }

  async handleMessage(msg: Record<string, unknown>): Promise<void> {
    try {
      switch (msg.type as string) {
        case 'studio.tabActivated':
          if (msg.tab === DecisionConvergencePanel.containerId
            && this.proposalId === undefined && this.workUnitId === undefined) {
            await this.loadLatestPending();
          }
          return;
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
        case 'validateEvidence':
          await this.post('/studio/merges/' + this.proposalId + '/validate', {});
          break;
        case 'acceptDecision':
          await this.post('/studio/merges/' + this.proposalId + '/review', {
            decision: 'Approved',
            notes: (msg.notes as string | undefined) || undefined,
            sessionId: this.getEffectiveSessionId(),
          });
          void vscode.window.showInformationMessage('Decision accepted.');
          break;
        case 'rejectDecision':
          await this.post('/studio/merges/' + this.proposalId + '/review', {
            decision: 'Rejected',
            notes: (msg.notes as string | undefined) || undefined,
            sessionId: this.getEffectiveSessionId(),
          });
          void vscode.window.showWarningMessage('Decision rejected — retrying with your notes as steering context.');
          break;
        case 'applyDecision':
          await this.post('/studio/merges/' + this.proposalId + '/apply', {});
          void vscode.window.showInformationMessage('Decision applied successfully.');
          break;
        case 'forkHypothesis': {
          const goal = await vscode.window.showInputBox({
            prompt: 'Goal for the new hypothesis fork',
            placeHolder: 'e.g. Retry with a different model',
            ignoreFocusOut: true,
          });
          if (!goal) { return; }
          let profileId: string | undefined;
          if (this.configService) {
            const profile = await this.configService.pickProfile('Select profile to run the new fork');
            profileId = profile?.id;
          } else {
            profileId = await vscode.window.showInputBox({ prompt: 'Profile ID', ignoreFocusOut: true });
          }
          if (!profileId) { return; }
          const result = await this.post<{ workUnitId: string }>(
            '/studio/merges/' + this.proposalId + '/branch', { goal, profileId });
          void vscode.window.showInformationMessage(
            'NodalMerge: Forked new work unit ' + result.workUnitId + '.');
          break;
        }
        case 'restoreWorkspace': {
          const result = await this.post<{ branchId: string }>(
            '/studio/merges/' + this.proposalId + '/restore-workspace', {});
          const changesRes = await this.get<{ fileChanges: ProposalFileChange[] }>(
            '/studio/merges/' + this.proposalId + '/file-changes');
          let opened = 0;
          for (const fc of changesRes.fileChanges ?? []) {
            if (fc.beforeContent == null) { continue; }
            const lang = fc.path.includes('.') ? fc.path.split('.').pop() : 'plaintext';
            const doc = await vscode.workspace.openTextDocument({ language: lang, content: fc.beforeContent });
            await vscode.window.showTextDocument(doc, { preview: false });
            opened++;
          }
          void vscode.window.showInformationMessage(
            'NodalMerge: Restored workspace to branch ' + result.branchId
            + ' (' + opened + ' file(s) opened read-only).');
          break;
        }
        case 'runWorkspaceCheck': {
          const kind = String(msg.kind ?? 'build');
          // "" is a valid rootPath (the branch root itself) — only treat undefined/missing as
          // "no scoping" (run every detected root), never coalesce "" away.
          const rootPath = typeof msg.rootPath === 'string' ? msg.rootPath : undefined;
          const branchId = this.lastProposal?.sourceBranch;
          if (!branchId) { return; }
          // branchId is passed as a query param, not a route segment — ids like
          // "merge/{workUnitId}" contain a literal "/", which a {branchId} route segment can
          // never match (every call with one of those ids 404'd).
          const endpoint = kind === 'run' ? 'run' : kind === 'test' ? 'test' : 'build';
          try {
            const result = await this.post(
              '/studio/workspace/' + endpoint + '?branchId=' + encodeURIComponent(branchId),
              rootPath !== undefined ? { rootPath } : {});
            void this.panel.webview.postMessage({ type: 'executionResult', kind, rootPath, result });
          } catch (err) {
            void this.panel.webview.postMessage({ type: 'executionResult', kind, rootPath, error: String(err) });
            void vscode.window.showErrorMessage('NodalMerge: ' + kind + ' failed — ' + String(err));
          }
          return;
        }
        case 'stopWorkspaceRun': {
          const rootPath = typeof msg.rootPath === 'string' ? msg.rootPath : undefined;
          const branchId = this.lastProposal?.sourceBranch;
          if (!branchId) { return; }
          try {
            const result = await this.post<{ stopped: number }>(
              '/studio/workspace/run/stop?branchId=' + encodeURIComponent(branchId),
              rootPath !== undefined ? { rootPath } : {});
            void this.panel.webview.postMessage({ type: 'runStopResult', rootPath, stopped: result.stopped });
          } catch (err) {
            void this.panel.webview.postMessage({ type: 'runStopResult', rootPath, error: String(err) });
            void vscode.window.showErrorMessage('NodalMerge: stop failed — ' + String(err));
          }
          return;
        }
        case 'downloadExecOutput': {
          const branchId = String(msg.branchId ?? '');
          const resultId = String(msg.resultId ?? '');
          if (!branchId || !resultId) { break; }
          try {
            const output = await this.get<{
              branchId: string;
              resultId: string;
              entries: {
                kind: string;
                buildSystem?: string;
                command: string;
                stdOut: string;
                stdErr: string;
                truncated: boolean;
              }[];
            }>('/studio/workspace/exec/output?branchId=' + encodeURIComponent(branchId)
              + '&resultId=' + encodeURIComponent(resultId));
            const lines: string[] = [];
            for (const entry of output.entries) {
              lines.push(`# ${entry.kind}: ${entry.command || ''} (${entry.buildSystem || 'cmd'}) ${entry.truncated ? '[truncated]' : ''}`);
              if (entry.stdErr) { lines.push('## stderr'); lines.push(entry.stdErr); }
              if (entry.stdOut) { lines.push('## stdout'); lines.push(entry.stdOut); }
              lines.push('');
            }
            const doc = await vscode.workspace.openTextDocument({
              language: 'plaintext',
              content: lines.join('\n'),
            });
            await vscode.window.showTextDocument(doc, { preview: false });
            void vscode.window.showInformationMessage(
              'NodalMerge: Downloaded execution output for ' + resultId);
          } catch (err) {
            void vscode.window.showErrorMessage('NodalMerge: failed to download execution output — ' + String(err));
          }
          break;
        }
        case 'pollRunOutput': {
          const rootPath = typeof msg.rootPath === 'string' ? msg.rootPath : undefined;
          const branchId = this.lastProposal?.sourceBranch;
          if (!branchId) { return; }
          try {
            const params = '/studio/workspace/run/output?branchId=' + encodeURIComponent(branchId)
              + (rootPath !== undefined ? '&rootPath=' + encodeURIComponent(rootPath) : '');
            const result = await this.get<{ output: string }>(params);
            void this.panel.webview.postMessage({ type: 'runOutputUpdate', rootPath, output: result.output });
          } catch {
            // Process likely exited — the webview will stop polling when stop is clicked.
          }
          return;
        }
        case 'openRootFolder': {
          const rootPath = typeof msg.rootPath === 'string' ? msg.rootPath : '';
          const branchId = this.lastProposal?.sourceBranch;
          if (!branchId) { return; }
          try {
            const result = await this.get<{ workingDirectory: string | null }>(
              '/studio/workspace/path?branchId=' + encodeURIComponent(branchId));
            if (!result.workingDirectory) { return; }
            const baseUri = vscode.Uri.file(result.workingDirectory);
            const folderUri = rootPath ? vscode.Uri.joinPath(baseUri, rootPath) : baseUri;
            await vscode.env.openExternal(folderUri);
          } catch (err) {
            void vscode.window.showErrorMessage('NodalMerge: could not reveal folder — ' + String(err));
          }
          return;
        }
        default:
          return;
      }
      await this.load();
    } catch (err) {
      void vscode.window.showWarningMessage('NodalMerge: ' + String(err));
    }
  }

  private async get<T>(path: string): Promise<T> {
    const controller = new AbortController();
    const timeout = setTimeout(() => controller.abort(new Error('timed out after 8000ms')), 8000);
    try {
      const res = await fetch(this.baseUrl + path, { signal: controller.signal });
      if (!res.ok) {
        const text = await res.text();
        throw new Error('GET ' + path + ' → ' + String(res.status) + ': ' + text);
      }
      return res.json() as Promise<T>;
    } catch (err) {
      throw new Error('GET ' + path + ' failed — ' + String(err));
    } finally {
      clearTimeout(timeout);
    }
  }

  private async post<T = unknown>(path: string, body: unknown): Promise<T> {
    const controller = new AbortController();
    const timeout = setTimeout(() => controller.abort(new Error('timed out after 8000ms')), 8000);
    try {
      const res = await fetch(this.baseUrl + path, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(body),
        signal: controller.signal,
      });
      if (!res.ok) {
        const text = await res.text();
        throw new Error('POST ' + path + ' → ' + String(res.status) + ': ' + text);
      }
      return res.json() as Promise<T>;
    } catch (err) {
      throw new Error('POST ' + path + ' failed — ' + String(err));
    } finally {
      clearTimeout(timeout);
    }
  }

}

// ── HTML ───────────────────────────────────────────────────────────────────

const DC_CSS = `
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
  .dc-topbar {
    display: flex; align-items: center; justify-content: flex-end;
    padding: 8px 0 4px; border-bottom: 1px solid var(--nm-border);
    margin-bottom: 4px;
  }
  .session-override-picker {
    font-size: 0.75em; padding: 1px 4px;
    border: 1px solid var(--nm-border); border-radius: 3px;
    background: var(--vscode-input-background, #3c3c3c);
    color: var(--vscode-input-foreground, #ccc);
    max-width: 150px;
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
  .badge.draft, .badge.exploring { background: #555; color: #ccc; }
  .badge.readyforreview, .badge.proposed { background: var(--nm-info); color: #fff; }
  .badge.approved, .badge.accepted { background: var(--nm-success); color: #fff; }
  .badge.rejected        { background: var(--nm-error); color: #fff; }
  .badge.merged, .badge.converged { background: #7c4dff; color: #fff; }
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
  button.accept { background: var(--nm-success); color: #fff; }
  button.accept:hover:not(:disabled) { filter: brightness(1.15); }
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
  .diff-line {
    font-family: var(--nm-mono);
    font-size: 0.85em;
    white-space: pre;
    overflow-x: auto;
    padding: 0 8px;
  }
  .diff-add  { color: var(--nm-success); background: rgba(35, 134, 54, 0.12); }
  .diff-del  { color: var(--nm-error);   background: rgba(241, 76, 76, 0.12); }
  .diff-meta {
    color: var(--nm-info); opacity: 0.7;
    font-family: var(--nm-mono); font-size: 0.85em;
    padding: 2px 8px;
  }
  .diff-mode-toggle {
    display: flex; gap: 0; margin: 0 0 8px;
  }
  .diff-mode-toggle button {
    border-radius: 0; opacity: 0.6;
    background: transparent; border: 1px solid var(--nm-border);
  }
  .diff-mode-toggle button.active { opacity: 1; background: var(--nm-btn); color: var(--nm-btn-fg); }
  .diff-mode-toggle button:first-child { border-radius: 3px 0 0 3px; }
  .diff-mode-toggle button:last-child  { border-radius: 0 3px 3px 0; border-left: none; }
  .diff-split {
    display: grid;
    grid-template-columns: 1fr 1fr;
  }
  .diff-split-cell {
    font-family: var(--nm-mono);
    font-size: 0.85em;
    white-space: pre;
    overflow-x: auto;
    padding: 0 8px;
    min-width: 0;
  }
  .diff-split-cell.right { border-left: 1px solid var(--nm-border); }
  .diff-split-meta { grid-column: 1 / -1; }
  .diff-empty { opacity: 0.6; padding: 8px 12px; font-size: 0.9em; }
  .auto-applied-banner {
    border-left: 3px solid var(--nm-success);
    background: color-mix(in srgb, var(--nm-success) 8%, transparent);
    padding: 8px 12px;
    margin: 12px 0;
  }
  .converged-banner {
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
  /* ── Slice 16m — workspace execution results ──────────────────────── */
  .exec-section { margin: 8px 0; }
  .exec-row {
    display: flex;
    align-items: center;
    gap: 8px;
    padding: 4px 0;
    font-family: var(--nm-mono);
    font-size: 0.85em;
  }
  .exec-row .badge { font-family: var(--nm-font); }
  .exec-row .cmd { opacity: 0.7; margin-left: 4px; }
  .exec-output-toggle {
    background: transparent;
    border: 1px solid var(--nm-border);
    border-radius: 3px;
    color: var(--nm-fg);
    cursor: pointer;
    font-size: 0.78em;
    padding: 2px 8px;
    margin: 2px 0 4px 20px;
    font-family: var(--nm-font);
  }
  .exec-output-pre {
    font-family: var(--nm-mono);
    font-size: 0.8em;
    background: rgba(128,128,128,0.06);
    border: 1px solid var(--nm-border);
    border-radius: 3px;
    padding: 6px 10px;
    margin: 2px 0 8px 20px;
    max-height: 300px;
    overflow-y: auto;
    white-space: pre-wrap;
    word-break: break-all;
  }
  .exec-download-link {
    font-size: 0.78em;
    margin: 0 0 4px 20px;
    color: var(--nm-info);
    cursor: pointer;
    text-decoration: none;
    font-family: var(--nm-font);
  }
  .exec-download-link:hover { text-decoration: underline; }
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
    border-top: 1px solid var(--nm-border);
  }
  .evidence-accepted {
    border-left: 3px solid var(--nm-success);
    padding: 8px 12px;
    background: rgba(35, 134, 54, 0.12);
    color: var(--nm-success);
    font-weight: 600;
  }
  .evidence-rejected {
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
  .no-changes-banner {
    border-left: 3px solid var(--nm-warn);
    background: rgba(204, 167, 0, 0.12);
    color: var(--nm-warn);
    font-weight: 600;
    padding: 8px 12px;
    margin-bottom: 8px;
    font-size: 0.9em;
  }
  /* ── Phase 9f — per-root Build/Test/Run-Stop rows ──────────────────── */
  .root-row {
    border: 1px solid var(--nm-border);
    border-radius: 4px;
    margin: 8px 0;
    padding: 8px 10px;
  }
  .root-row-header {
    display: flex;
    align-items: center;
    gap: 8px;
    margin-bottom: 6px;
  }
  .root-label {
    font-family: var(--nm-mono);
    font-size: 0.9em;
    font-weight: 600;
  }
  .root-run-status {
    font-size: 0.78em;
    opacity: 0.65;
    margin-left: auto;
  }
  .root-run-status.running { color: var(--nm-success); opacity: 1; font-weight: 600; }
  .root-row-actions {
    display: flex;
    gap: 6px;
  }
  .root-row-actions button.ghost {
    margin: 0;
  }
  .root-row-actions button.ghost:disabled {
    opacity: 0.4;
  }
  .root-row-results { margin-top: 6px; }
  .review-notes-row {
    margin: 12px 0;
  }
  .review-notes-row label {
    display: block;
    font-size: 0.85em;
    opacity: 0.8;
    margin-bottom: 4px;
  }
  .review-notes-row textarea {
    width: 100%;
    box-sizing: border-box;
    background: var(--vscode-input-background);
    color: var(--vscode-input-foreground);
    border: 1px solid var(--vscode-input-border, var(--nm-border));
    border-radius: 4px;
    padding: 6px 8px;
    font-family: inherit;
    font-size: 0.9em;
    resize: vertical;
  }
`;

const DC_HTML = `
  <div class="dc-topbar">
    <select id="dc-session-override" class="session-override-picker"><option value="">Follow Workspace</option></select>
  </div>
  <div id="loading">Loading decision candidate…</div>
  <div id="content" class="hidden">
    <h1 id="title">Decision Convergence</h1>
    <div id="meta-grid" class="meta-grid">
      <span class="meta-label">Decision Status</span> <span id="status-badge"></span>
      <span class="meta-label">Hypothesis Fork</span><span class="meta-value" id="source-branch"></span>
      <span class="meta-label">Target</span>         <span class="meta-value" id="target-branch"></span>
      <span class="meta-label">Confidence</span>     <span class="meta-value" id="confidence"></span>
    </div>
    <section id="section-converged" class="hidden converged-banner">
      <strong>Converged decision</strong> — synthesized from <span id="converged-count"></span> candidate(s).
      <div id="converged-from"></div>
    </section>
    <section id="section-auto-applied" class="hidden auto-applied-banner">
      🤖 <strong>Auto-applied by reviewer agent</strong> — no human action required.
    </section>
    <section id="section-conflict-report" class="hidden">
      <h2>Decision Conflict</h2>
      <pre id="conflict-report-content" class="diff-pre"></pre>
      <p style="opacity:0.7;font-size:0.9em">
        Resolve conflicting hypotheses manually — edit the conflicting files on the affected branches outside this panel,
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
      <h2>Rationale</h2>
      <p id="change-description"></p>
    </section>
    <section id="section-files" class="hidden">
      <h2>Code Changes</h2>
      <div class="diff-mode-toggle">
        <button id="btn-mode-inline">Inline</button>
        <button id="btn-mode-split">Split</button>
      </div>
      <div id="file-changes"></div>
    </section>
  <section id="section-evidence" class="hidden">
      <h2>Evidence</h2>
      <div id="no-changes-banner" class="hidden no-changes-banner"></div>
      <div id="evidence-results"></div>
      <div id="execution-results" class="hidden"></div>
      <h2 style="margin-top:14px">Build / Test / Run</h2>
      <div id="root-rows"></div>
    </section>
    <section id="section-rollback" class="hidden">
      <h2>Rollback plan</h2>
      <p id="rollback-plan"></p>
    </section>
    <div id="review-notes-row" class="review-notes-row">
      <label for="review-notes">Notes (steering direction for a reject, or context for an accept)</label>
      <textarea id="review-notes" rows="3" placeholder="e.g. Missing the edge case for empty input — handle that and resubmit."></textarea>
    </div>
    <div id="actions" class="actions">
      <button id="btn-validate">Validate Evidence</button>
      <button id="btn-accept" class="accept">Accept Decision</button>
      <button id="btn-reject" class="reject">Reject Decision</button>
      <button id="btn-apply"  class="apply">Apply Decision</button>
      <button id="btn-fork"   class="ghost">Fork Hypothesis</button>
      <button id="btn-restore" class="ghost">Restore workspace</button>
    </div>
  </div>
`;

const DC_JS = `
  var vscode = acquireVsCodeApi();

  var dcSessionOverride = document.getElementById('dc-session-override');
  if (dcSessionOverride) {
    dcSessionOverride.addEventListener('change', function() {
      vscode.postMessage({ type: 'sessionOverrideChanged', panelId: 'shell-pane-decision-convergence', sessionId: dcSessionOverride.value || undefined });
    });
  }

  var STATUS_BUTTONS = {
    draft:          { validate: true,  accept: false, reject: false, apply: false },
    readyforreview: { validate: false, accept: true,  reject: true,  apply: false },
    proposed:       { validate: false, accept: true,  reject: true,  apply: false },
    executing:      { validate: true,  accept: false, reject: false, apply: false },
    merge:          { validate: false, accept: false, reject: false, apply: false },
    approved:       { validate: false, accept: false, reject: false, apply: true  },
    merged:         { validate: false, accept: false, reject: false, apply: false },
    rejected:       { validate: false, accept: false, reject: false, apply: false },
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
    vscode.postMessage({ type: 'validateEvidence' });
  });
  function reviewNotesValue() {
    var el = document.getElementById('review-notes');
    var v = el && el.value ? el.value.trim() : '';
    return v.length ? v : undefined;
  }
  document.getElementById('btn-accept').addEventListener('click', function() {
    vscode.postMessage({ type: 'acceptDecision', notes: reviewNotesValue() });
  });
  document.getElementById('btn-reject').addEventListener('click', function() {
    vscode.postMessage({ type: 'rejectDecision', notes: reviewNotesValue() });
  });
  document.getElementById('btn-apply').addEventListener('click', function() {
    vscode.postMessage({ type: 'applyDecision' });
  });
  document.getElementById('btn-fork').addEventListener('click', function() {
    vscode.postMessage({ type: 'forkHypothesis' });
  });
  document.getElementById('btn-restore').addEventListener('click', function() {
    vscode.postMessage({ type: 'restoreWorkspace' });
  });

  // ── Phase 9f — shared single-item renderers (used by both the persisted-evidence
  // view below and the live per-root results) ──────────────────────────────────
  function renderBuildRow(b, nodeId, branchId) {
    var icon = b.success ? '✅' : '❌';
    var sys = b.buildSystem || 'cmd';
    var dur = b.startedAt && b.completedAt
      ? ((new Date(b.completedAt) - new Date(b.startedAt)) / 1000).toFixed(1) + 's'
      : '';
    var html = '<div class="exec-row">' + icon + ' <span class="badge">' + esc(sys) + '</span>'
      + ' <span class="cmd">' + esc(b.command || '') + '</span>'
      + (dur ? ' <span style="opacity:0.6">(' + dur + ')</span>' : '')
      + (b.exitCode !== 0 ? ' <span style="color:var(--nm-error)">exit ' + b.exitCode + '</span>' : '')
      + '</div>';

    var hasStdout = b.stdOut && b.stdOut.length > 0;
    var hasStderr = b.stdErr && b.stdErr.length > 0;
    if (hasStdout || hasStderr) {
      var outId = 'exec-stdout-' + Math.random().toString(36).slice(2,8);
      html += '<button class="exec-output-toggle" data-target="' + outId + '">▼ Output</button>';
      html += '<pre class="exec-output-pre" id="' + outId + '" style="display:none">';
      if (hasStderr) html += esc(b.stdErr) + '\\n';
      if (hasStdout) html += esc(b.stdOut);
      html += '</pre>';
    }

    if (b.truncated && nodeId) {
      html += '<a class="exec-download-link" data-branch="' + esc(branchId) + '"'
        + ' data-result="' + esc(nodeId) + '">'
        + 'Download full output (truncated)</a>';
    }
    return html;
  }

  function renderTestRow(t, nodeId, branchId) {
    var icon = t.success ? '✅' : '⚠';
    var sys = t.buildSystem || 'cmd';
    if (t.failed === 0 && t.totalTests === 0) icon = t.success ? '✅' : '❌';
    var summary = t.totalTests > 0
      ? t.passed + ' passed / ' + t.failed + ' failed' + (t.skipped ? ' / ' + t.skipped + ' skipped' : '')
      : '';
    var dur = t.startedAt && t.completedAt
      ? ((new Date(t.completedAt) - new Date(t.startedAt)) / 1000).toFixed(1) + 's'
      : '';
    var html = '<div class="exec-row">' + icon + ' <span class="badge">' + esc(sys) + '</span>'
      + ' <span class="cmd">' + esc(t.command || '') + '</span>'
      + ' <span>' + summary + '</span>'
      + (dur ? ' <span style="opacity:0.6">(' + dur + ')</span>' : '')
      + '</div>';

    var hasStdout = t.stdOut && t.stdOut.length > 0;
    if (hasStdout) {
      var tid = 'exec-testout-' + Math.random().toString(36).slice(2,8);
      html += '<button class="exec-output-toggle" data-target="' + tid + '">▼ Output</button>';
      html += '<pre class="exec-output-pre" id="' + tid + '" style="display:none">' + esc(t.stdOut) + '</pre>';
    }

    if (t.truncated && nodeId) {
      html += '<a class="exec-download-link" data-branch="' + esc(branchId) + '"'
        + ' data-result="' + esc(nodeId) + '">'
        + 'Download full output (truncated)</a>';
    }
    return html;
  }

  function renderExecResult(parsedExec) {
    var html = '<div class="exec-section">';

    if (parsedExec.builds && parsedExec.builds.length) {
      html += '<strong>Build</strong>';
      parsedExec.builds.forEach(function(b) { html += renderBuildRow(b, parsedExec.nodeId, parsedExec.branchId); });
    }

    if (parsedExec.tests && parsedExec.tests.length) {
      html += '<strong style="margin-top:8px;display:block">Tests</strong>';
      parsedExec.tests.forEach(function(t) { html += renderTestRow(t, parsedExec.nodeId, parsedExec.branchId); });
    }

    if ((!parsedExec.builds || !parsedExec.builds.length) && (!parsedExec.tests || !parsedExec.tests.length)) {
      html += '<span style="opacity:0.6;font-size:0.85em">No build/test results.</span>';
    }

    html += '</div>';
    return html;
  }

  // ── Phase 9f — per-root Build/Test/Run-Stop controls ──────────────────────────
  // Replaces the old single global Build/Test/Run buttons: a repo with more than one detected
  // project root (a dotnet host + a React frontend, say) gets one row per root, each scoped to
  // that root only, instead of one click silently building/testing/running everything at once.
  var rootCapabilities = {}; // relativePath -> { build, test, run }
  var rootRunState = {};     // relativePath -> { running, pid }

  function rootRowId(rootPath) {
    return 'root-row-' + (rootPath || 'repo-root').replace(/[^a-zA-Z0-9_-]/g, '_');
  }

  function renderRootRows(roots) {
    var list = (roots && roots.length) ? roots : [{ relativePath: '', stack: '', buildCommand: null, testCommand: null, runCommand: null, isLongRunning: false }];
    rootCapabilities = {};
    list.forEach(function(root) {
      rootCapabilities[root.relativePath] = {
        build: !!root.buildCommand,
        test: !!root.testCommand,
        run: !!root.runCommand,
      };
      if (!rootRunState[root.relativePath]) { rootRunState[root.relativePath] = { running: false }; }
    });

    var html = list.map(function(root) {
      var id = rootRowId(root.relativePath);
      var label = root.relativePath || 'repo root';
      // "none" is the Phase 9h synthetic rule-file-only root (a branch root with an AGENTS.md
      // but no buildable project there) — not a real stack worth badging.
      var stackBadge = (root.stack && root.stack !== 'none') ? '<span class="badge">' + esc(root.stack) + '</span>' : '';
      var caps = rootCapabilities[root.relativePath];
      return '<div class="root-row" data-root="' + esc(root.relativePath) + '">'
        + '<div class="root-row-header">'
        + '<span class="root-label">' + esc(label) + '</span>' + stackBadge
        + '<span class="root-run-status" id="status-' + id + '"></span>'
        + '</div>'
        + '<div class="root-row-actions">'
        + (caps.build ? '<button class="ghost" data-action="build" id="btn-build-' + id + '">Build</button>' : '')
        + (caps.test  ? '<button class="ghost" data-action="test"  id="btn-test-'  + id + '">Test</button>'  : '')
        + '<button class="ghost" data-action="run" id="btn-run-' + id + '"'
          + (caps.run ? '' : ' disabled title="No run command detected for this root"') + '>Run</button>'
        + '<button class="ghost" data-action="stop" id="btn-stop-' + id + '" style="display:none">Stop</button>'
        + '<button class="ghost" data-action="openFolder" id="btn-folder-' + id + '" title="Open folder in Explorer">Open Folder</button>'
        + '</div>'
        + '<div class="root-row-results" id="results-' + id + '"></div>'
        + '</div>';
    }).join('');

    setHtml('root-rows', html);
    list.forEach(function(root) { updateRunStatusUi(root.relativePath); });
  }

  function updateRunStatusUi(rootPath) {
    var id = rootRowId(rootPath);
    var state = rootRunState[rootPath] || { running: false };
    var statusEl = document.getElementById('status-' + id);
    var runBtn = document.getElementById('btn-run-' + id);
    var stopBtn = document.getElementById('btn-stop-' + id);
    if (!statusEl) return;
    if (state.running) {
      statusEl.textContent = 'Running (pid ' + state.pid + ')';
      statusEl.classList.add('running');
      if (runBtn) runBtn.style.display = 'none';
      if (stopBtn) stopBtn.style.display = '';
    } else {
      statusEl.textContent = '';
      statusEl.classList.remove('running');
      if (runBtn) runBtn.style.display = '';
      if (stopBtn) stopBtn.style.display = 'none';
    }
  }

  function setRootBusy(rootPath, busy) {
    var id = rootRowId(rootPath);
    var caps = rootCapabilities[rootPath] || { build: false, test: false, run: false };
    var buildBtn = document.getElementById('btn-build-' + id);
    var testBtn  = document.getElementById('btn-test-' + id);
    var runBtn   = document.getElementById('btn-run-' + id);
    var stopBtn  = document.getElementById('btn-stop-' + id);
    if (buildBtn) buildBtn.disabled = busy || !caps.build;
    if (testBtn)  testBtn.disabled  = busy || !caps.test;
    if (runBtn)   runBtn.disabled   = busy || !caps.run;
    if (stopBtn)  stopBtn.disabled  = busy;
  }

  var rootRowsEl = document.getElementById('root-rows');
  if (rootRowsEl) {
    rootRowsEl.addEventListener('click', function(ev) {
      var btn = ev.target.closest('[data-action]');
      if (!btn || btn.disabled) return;
      var row = btn.closest('.root-row');
      if (!row) return;
      var rootPath = row.getAttribute('data-root') || '';
      var action = btn.getAttribute('data-action');
      if (action === 'openFolder') {
        vscode.postMessage({ type: 'openRootFolder', rootPath: rootPath });
        return;
      }
      setRootBusy(rootPath, true);
      if (action === 'stop') {
        vscode.postMessage({ type: 'stopWorkspaceRun', rootPath: rootPath });
        return;
      }
      vscode.postMessage({ type: 'runWorkspaceCheck', kind: action, rootPath: rootPath });
    });
  }

  // Slice 18g — card-based constituent rendering with model, confidence, rationale
  function renderConstituents(constituents, fallbackIds) {
    var byId = {};
    (constituents || []).forEach(function(c) { byId[c.proposalId] = c; });
    return (fallbackIds || []).map(function(id) {
      var c = byId[id];
      if (!c) {
        return '<div class="constituent-card" style="border:1px solid var(--nm-border);border-radius:4px;padding:8px;margin:6px 0">'
          + '<div class="constituent-row"><span class="mono">' + esc(id) + '</span></div>'
          + '</div>';
      }
      var statusKey = (c.status || '').toLowerCase().replace(/\\s+/g, '');
      var html = '<div class="constituent-card" style="border:1px solid var(--nm-border);border-radius:4px;padding:10px;margin:6px 0">';
      // Header row
      html += '<div class="constituent-row" style="margin-bottom:4px">';
      html += '<span class="badge ' + statusKey + '">' + esc(c.status) + '</span>';
      html += '<span class="mono">' + esc(c.proposalId) + '</span>';
      if (c.goal) { html += '<span style="font-size:0.88em">' + esc(c.goal) + '</span>'; }
      html += '</div>';
      // Model & confidence row
      html += '<div style="display:flex;gap:8px;flex-wrap:wrap;font-size:0.82em;margin-top:4px">';
      if (c.model) {
        html += '<span style="opacity:0.6">Model:</span><span>' + esc(c.model);
        if (c.provider) { html += ' (' + esc(c.provider) + ')'; }
        html += '</span>';
      }
      if (c.confidence != null) {
        html += '<span style="opacity:0.6">Confidence:</span><span>' + Math.round(c.confidence * 100) + '%</span>';
      }
      if (c.agentId) {
        html += '<span style="opacity:0.6">Agent:</span><span class="mono">' + esc(c.agentId) + '</span>';
      }
      html += '</div>';
      // Rationale excerpt
      if (c.rationale) {
        html += '<div style="font-size:0.82em;opacity:0.7;margin-top:6px;padding-left:6px;border-left:2px solid var(--nm-border)">' + esc(c.rationale.substring(0, 200)) + (c.rationale.length > 200 ? '…' : '') + '</div>';
      }
      // Summary
      if (c.summary) {
        html += '<div style="font-size:0.82em;opacity:0.7;margin-top:4px">' + esc(c.summary) + '</div>';
      }
      html += '</div>';
      return html;
    }).join('');
  }

  function getDiffMode() {
    var state = vscode.getState() || {};
    return state.diffMode === 'split' ? 'split' : 'inline';
  }

  function setDiffMode(mode) {
    var state = vscode.getState() || {};
    state.diffMode = mode;
    vscode.setState(state);
  }

  function hunkHeader(h) {
    return '@@ -' + h.beforeStart + ',' + h.beforeCount + ' +' + h.afterStart + ',' + h.afterCount + ' @@';
  }

  function renderInlineHunks(hunks) {
    if (!hunks || !hunks.length) return '<div class="diff-empty">No textual changes.</div>';
    return hunks.map(function(h) {
      var rows = h.lines.map(function(l) {
        var prefix = l.kind === 'Added' ? '+' : l.kind === 'Removed' ? '-' : ' ';
        var cls = l.kind === 'Added' ? 'diff-add' : l.kind === 'Removed' ? 'diff-del' : '';
        return '<div class="diff-line ' + cls + '">' + prefix + esc(l.text) + '</div>';
      }).join('');
      return '<div class="diff-meta">' + esc(hunkHeader(h)) + '</div>' + rows;
    }).join('');
  }

  function renderSplitHunks(hunks) {
    if (!hunks || !hunks.length) return '<div class="diff-empty">No textual changes.</div>';
    return hunks.map(function(h) {
      var rows = h.lines.map(function(l) {
        var left = l.kind === 'Added' ? '' : esc(l.text);
        var right = l.kind === 'Removed' ? '' : esc(l.text);
        var leftCls = l.kind === 'Removed' ? 'diff-del' : '';
        var rightCls = l.kind === 'Added' ? 'diff-add' : '';
        return '<div class="diff-split-cell ' + leftCls + '">' + left + '</div>'
          + '<div class="diff-split-cell right ' + rightCls + '">' + right + '</div>';
      }).join('');
      return '<div class="diff-split"><div class="diff-split-meta">' + esc(hunkHeader(h)) + '</div>' + rows + '</div>';
    }).join('');
  }

  function renderFileChanges(changes, mode) {
    if (!changes || !changes.length) return '';
    return changes.map(function(fc, idx) {
      var isDeleted = (fc.changeKind || '').toLowerCase() === 'deleted';
      var body = mode === 'split' ? renderSplitHunks(fc.hunks) : renderInlineHunks(fc.hunks);
      var html = '<details class="file-change" open>';
      html += '<summary>' + esc(fc.path) + ' <span class="badge">' + esc(fc.changeKind) + '</span></summary>';
      html += '<div class="file-change-body">' + body + '</div>';
      if (!isDeleted) {
        html += '<button class="ghost" data-open-diff="' + idx + '">Open Diff in Editor</button>';
      }
      html += '</details>';
      return html;
    }).join('');
  }

  function rerenderFileChanges() {
    var mode = getDiffMode();
    var inlineBtn = document.getElementById('btn-mode-inline');
    var splitBtn = document.getElementById('btn-mode-split');
    if (inlineBtn) inlineBtn.classList.toggle('active', mode === 'inline');
    if (splitBtn) splitBtn.classList.toggle('active', mode === 'split');
    setHtml('file-changes', renderFileChanges(window.__fileChanges || [], mode));
  }

  document.getElementById('btn-mode-inline').addEventListener('click', function() {
    setDiffMode('inline');
    rerenderFileChanges();
  });
  document.getElementById('btn-mode-split').addEventListener('click', function() {
    setDiffMode('split');
    rerenderFileChanges();
  });

  document.addEventListener('click', function(ev) {
    var btn = ev.target.closest('[data-open-diff]');
    if (btn) {
      var idx = parseInt(btn.getAttribute('data-open-diff'), 10);
      var fc = window.__fileChanges && window.__fileChanges[idx];
      if (!fc) return;
      vscode.postMessage({
        type: 'openDiff',
        path: fc.path,
        beforeContent: fc.beforeContent,
        afterContent: fc.afterContent
      });
      return;
    }

    var toggle = ev.target.closest('[data-target]');
    if (toggle) {
      var targetId = toggle.getAttribute('data-target');
      var pre = document.getElementById(targetId);
      if (pre) {
        var isVisible = pre.style.display !== 'none';
        pre.style.display = isVisible ? 'none' : 'block';
        toggle.textContent = isVisible ? '▼ Output' : '▲ Output';
      }
      return;
    }

    var download = ev.target.closest('[data-branch]');
    if (download) {
      var branchId = download.getAttribute('data-branch');
      var resultId = download.getAttribute('data-result');
      vscode.postMessage({ type: 'downloadExecOutput', branchId: branchId, resultId: resultId });
      return;
    }
  });

  function showDecisionSections(show) {
    showIf('meta-grid', show);
    showIf('section-goal', show);
    showIf('section-summary', show);
    showIf('section-change', show);
    showIf('actions', show);
  }

  window.addEventListener('message', function(event) {
    var msg = event.data;

    if (msg.type === 'updateSessionPicker' && msg.panelId === 'shell-pane-decision-convergence') {
      var sel = document.getElementById('dc-session-override');
      if (sel) {
        var shellLabel = msg.shellSessionId ? ' (' + String(msg.shellSessionId).slice(0, 8) + '…)' : '';
        sel.innerHTML = '<option value="">Follow Workspace' + esc(shellLabel) + '</option>';
        for (var i = 0; i < (msg.sessions || []).length; i++) {
          var s = msg.sessions[i];
          var opt = document.createElement('option');
          opt.value = s.sessionId;
          opt.textContent = String(s.sessionId).slice(0, 12) + '… (' + s.status + ')';
          sel.appendChild(opt);
        }
        sel.value = msg.overrideSessionId || '';
      }
      return;
    }

    if (msg.type === 'noPending') {
      var loadingEl2 = document.getElementById('loading');
      var contentEl2 = document.getElementById('content');
      if (loadingEl2) {
        loadingEl2.textContent = 'No pending decisions to review.';
        loadingEl2.style.opacity = '0.55';
      }
      if (contentEl2) { contentEl2.classList.add('hidden'); }
      return;
    }

    if (msg.type === 'loadError') {
      var loadingEl2 = document.getElementById('loading');
      if (loadingEl2) {
        loadingEl2.textContent = 'Failed to load: ' + (msg.error || 'Unknown error');
        loadingEl2.style.opacity = '0.7';
        loadingEl2.style.color = 'var(--nm-error, #f14c4c)';
      }
      return;
    }

    if (msg.type === 'conflict') {
      var loadingEl3 = document.getElementById('loading');
      var contentEl3 = document.getElementById('content');
      if (loadingEl3) loadingEl3.classList.add('hidden');
      if (contentEl3) contentEl3.classList.remove('hidden');

      setText('title', 'Decision Conflict: ' + (msg.workUnitId || ''));
      showDecisionSections(false);
      showIf('section-converged', false);
      showIf('section-files', false);
      showIf('section-evidence', false);
      showIf('section-rollback', false);
      showIf('section-conflict-report', true);
      setText('conflict-report-content', msg.content || '');
      return;
    }

    if (msg.type === 'executionResult') {
      var rootPath = typeof msg.rootPath === 'string' ? msg.rootPath : '';
      setRootBusy(rootPath, false);
      var resultsEl = document.getElementById('results-' + rootRowId(rootPath));
      if (!resultsEl) return;

      if (msg.error) {
        resultsEl.innerHTML = '<span style="color:var(--nm-error)">' + esc(msg.kind) + ' failed: ' + esc(msg.error) + '</span>';
        return;
      }

      if (msg.kind === 'run') {
        // RunAsync returns a raw BuildResult[] (not the {builds,tests} shape Build/Test use) —
        // a long-running result comes back immediately with running:true/pid set; a one-shot
        // run command blocks and comes back finished, rendered like a build row.
        var runResults = msg.result || [];
        var first = runResults[0];
        if (first && first.running) {
          var prevState = rootRunState[rootPath];
          if (prevState && prevState.pollId) clearInterval(prevState.pollId);
          var pollId = setInterval(function() {
            vscode.postMessage({ type: 'pollRunOutput', rootPath: rootPath });
          }, 2000);
          rootRunState[rootPath] = { running: true, pid: first.pid, pollId: pollId };
          updateRunStatusUi(rootPath);
          resultsEl.innerHTML = '<div class="exec-row">&#9654; <span class="cmd">' + esc(first.command || '') + '</span>'
            + '<pre class="run-output" id="run-output-' + rootRowId(rootPath) + '" style="margin:4px 0 0;white-space:pre-wrap;max-height:240px;overflow-y:auto;font-size:0.8em;opacity:0.85">Starting…</pre></div>';
        } else {
          var prevState2 = rootRunState[rootPath];
          if (prevState2 && prevState2.pollId) clearInterval(prevState2.pollId);
          rootRunState[rootPath] = { running: false };
          updateRunStatusUi(rootPath);
          resultsEl.innerHTML = runResults.length
            ? runResults.map(function(b) { return renderBuildRow(b, null, null); }).join('')
            : '<span style="opacity:0.6;font-size:0.85em">No run command for this root.</span>';
        }
        return;
      }

      // build/test: BranchExecutionResult shape { builds, tests, nodeId, branchId }
      var result = msg.result || {};
      var builds = result.builds || [];
      var tests = result.tests || [];
      var html = builds.map(function(b) { return renderBuildRow(b, result.nodeId, result.branchId); }).join('')
        + tests.map(function(t) { return renderTestRow(t, result.nodeId, result.branchId); }).join('');
      resultsEl.innerHTML = html || '<span style="opacity:0.6;font-size:0.85em">No results.</span>';
      return;
    }

    if (msg.type === 'runOutputUpdate') {
      var outputEl = document.getElementById('run-output-' + rootRowId(typeof msg.rootPath === 'string' ? msg.rootPath : ''));
      if (outputEl && typeof msg.output === 'string') {
        outputEl.textContent = msg.output || '(no output yet)';
        outputEl.scrollTop = outputEl.scrollHeight;
      }
      return;
    }

    if (msg.type === 'runStopResult') {
      var stopRootPath = typeof msg.rootPath === 'string' ? msg.rootPath : '';
      var stoppedState = rootRunState[stopRootPath];
      if (stoppedState && stoppedState.pollId) clearInterval(stoppedState.pollId);
      setRootBusy(stopRootPath, false);
      var stopResultsEl = document.getElementById('results-' + rootRowId(stopRootPath));
      if (msg.error) {
        if (stopResultsEl) {
          stopResultsEl.innerHTML = '<span style="color:var(--nm-error)">stop failed: ' + esc(msg.error) + '</span>';
        }
        return;
      }
      rootRunState[stopRootPath] = { running: false };
      updateRunStatusUi(stopRootPath);
      return;
    }

    if (msg.type !== 'proposal') { return; }
    showDecisionSections(true);
    showIf('section-conflict-report', false);
    var p = msg.proposal;
    var fileChanges = msg.fileChanges || [];
    window.__fileChanges = fileChanges;
    var status = (p.status || '').toLowerCase().replace(/\\s+/g, '');

    var loadingEl = document.getElementById('loading');
    var contentEl = document.getElementById('content');
    if (loadingEl) loadingEl.classList.add('hidden');
    if (contentEl) contentEl.classList.remove('hidden');

    setText('title', 'Decision Convergence: ' + (p.goal || p.sourceBranch || ''));
    var badgeClass = 'badge ' + status;
    setHtml('status-badge', '<span class="' + badgeClass + '">' + esc(p.status) + '</span>');
    setText('source-branch', p.sourceBranch);
    setText('target-branch', p.targetBranch);
    setText('confidence', p.confidence != null ? (Math.round(p.confidence * 100) + '%') : '—');
    setText('goal', p.goal);
    setText('summary', p.summary);
    setText('change-description', p.changeDescription);

    // ── Parse evidence for execution data or plain review text ──
    var evidenceEl = document.getElementById('evidence-results');
    var execResultsEl = document.getElementById('execution-results');
    var parsedExec = null;
    var blockedInfo = null;
    var plainReview = null;

    if (p.verificationResults) {
      try {
        var parsed = JSON.parse(p.verificationResults);
        if (parsed.blocked) {
          blockedInfo = parsed;
          if (blockedInfo.execution) parsedExec = blockedInfo.execution;
        } else if (parsed.branchId && parsed.builds) {
          parsedExec = parsed;
        } else {
          plainReview = p.verificationResults;
        }
      } catch (_) {
        plainReview = p.verificationResults;
      }
    }

    if (evidenceEl && plainReview) {
      var isRejected = status === 'rejected';
      evidenceEl.className = isRejected ? 'evidence-rejected' : 'evidence-accepted';
      evidenceEl.textContent = plainReview;
    } else if (evidenceEl && blockedInfo) {
      evidenceEl.className = 'evidence-rejected';
      evidenceEl.textContent = 'Policy blocked: ' + (blockedInfo.violations || []).join('; ');
      showIf('section-evidence', true);
    } else if (evidenceEl) {
      evidenceEl.className = '';
      evidenceEl.textContent = '';
    }

    if (parsedExec && execResultsEl) {
      execResultsEl.classList.remove('hidden');
      execResultsEl.innerHTML = renderExecResult(parsedExec);
    } else if (execResultsEl) {
      execResultsEl.classList.add('hidden');
      execResultsEl.innerHTML = '';
    }

    // Evidence section now always visible (it also hosts the per-root Build/Test/Run controls).
    showIf('section-evidence', true);
    renderRootRows(msg.roots || []);

    // Flag proposals with no file diff at all — the goal/summary/rationale text can look fully
    // complete even when the agent never actually wrote anything (see filesTouched).
    var noChangesBannerEl = document.getElementById('no-changes-banner');
    var hasNoFiles = !p.filesTouched || p.filesTouched.length === 0;
    if (noChangesBannerEl) {
      if (hasNoFiles && status !== 'rejected') {
        var bannerText = '⚠ No file changes detected on this branch.';
        if (p.noFileChangesJustification) {
          bannerText += ' Agent justification: "' + esc(p.noFileChangesJustification) + '"';
        } else {
          bannerText += ' This proposal may only describe work without doing it — verify before accepting.';
        }
        noChangesBannerEl.textContent = bannerText;
        noChangesBannerEl.classList.remove('hidden');
      } else {
        noChangesBannerEl.classList.add('hidden');
      }
    }

    setText('rollback-plan', p.rollbackPlan);

    var reviewNotesEl = document.getElementById('review-notes');
    if (reviewNotesEl) { reviewNotesEl.value = p.reviewNotes || ''; }

    var converged = p.reconciledFrom && p.reconciledFrom.length;
    showIf('section-converged', !!converged);
    if (converged) {
      setText('converged-count', String(p.reconciledFrom.length));
      setHtml('converged-from', renderConstituents(msg.constituents || [], p.reconciledFrom));
    }

    rerenderFileChanges();
    showIf('section-files', fileChanges.length > 0);

    showIf('section-rollback', !!p.rollbackPlan);

    // Auto-applied banner: show when merged AND autoApplied flag is set OR verificationResults
    // looks like reviewer text (plain string from the agent, not execution JSON).
    var isAutoApplied = status === 'merged' && (
      p.autoApplied === true ||
      (p.verificationResults && !p.verificationResults.trim().startsWith('{'))
    );
    showIf('section-auto-applied', isAutoApplied);

    var btns = STATUS_BUTTONS[status] || { validate: false, accept: false, reject: false, apply: false };
    setDisabled('btn-validate', !btns.validate);
    setDisabled('btn-accept',  !btns.accept);
    setDisabled('btn-reject',  !btns.reject);
    setDisabled('btn-apply',   !btns.apply);
  });
`;

