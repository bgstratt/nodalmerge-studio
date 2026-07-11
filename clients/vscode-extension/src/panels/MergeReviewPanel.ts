import * as fs from 'fs';
import * as path from 'path';
import * as vscode from 'vscode';
import { scopeViewCss, openReadOnlyDiff } from './sharedWebviewChrome';
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
  // True once this proposal's own apply actually redirected onto the shared "candidate" staging
  // branch (WorkspaceOptions.UsePromotionBranch). Distinct from targetBranch, which stays the
  // proposal's ultimate declared destination ("main") whether or not promotion is in play — see
  // MergeProposal.LandedOnCandidateBranch's own doc comment.
  landedOnCandidateBranch?: boolean;
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
  private conflictBranchId?: string;
  // Path -> content captured at the moment "Edit File" opened it, for Resync Workspace to diff
  // the (possibly now hand-edited) live file against. Cleared whenever a fresh proposal/conflict
  // loads, since a stale baseline from a previous decision would compare against the wrong thing.
  private editBaselines = new Map<string, string>();

  /** The branch a human-editable file actually lives on, for whichever mode is currently loaded. */
  private get editableBranchId(): string | undefined {
    return this.mode === 'conflict' ? this.conflictBranchId : this.lastProposal?.sourceBranch;
  }

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
    this.editBaselines.clear();
    void this.load();
  }

  loadConflict(workUnitId: string): void {
    this.mode = 'conflict';
    this.workUnitId = workUnitId;
    this.proposalId = undefined;
    this.editBaselines.clear();
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

  static getFragment(): { css: string; html: string } {
    return {
      css: scopeViewCss(DC_CSS, DecisionConvergencePanel.containerId),
      html: `<div id="${DecisionConvergencePanel.containerId}" class="nm-shell-pane">${DC_HTML}</div>`,
    };
  }

  private async load(): Promise<void> {
    if (this.mode === 'conflict') {
      try {
        const report = await this.get<{ workUnitId: string; branchId: string; status: string; content: string }>(
          '/studio/workunits/' + this.workUnitId + '/conflict-report');
        this.conflictBranchId = report.branchId;
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
          const filePath = String(msg.path ?? 'file');
          const before = (msg.beforeContent as string | null | undefined) ?? '';
          const after = (msg.afterContent as string | null | undefined) ?? '';
          await openReadOnlyDiff(filePath, before, after);
          break;
        }
        case 'editFile': {
          const filePath = String(msg.path ?? '');
          const branchId = this.editableBranchId;
          if (!filePath || !branchId) {
            void vscode.window.showWarningMessage('NodalMerge: no branch loaded to edit this file on.');
            return;
          }
          try {
            const [{ content }, { workingDirectory }] = await Promise.all([
              this.get<{ content: string | null }>(
                '/studio/workspace/read?branchId=' + encodeURIComponent(branchId) + '&path=' + encodeURIComponent(filePath)),
              this.get<{ workingDirectory: string | null }>(
                '/studio/workspace/path?branchId=' + encodeURIComponent(branchId)),
            ]);
            if (!workingDirectory) {
              void vscode.window.showErrorMessage('NodalMerge: no materialized working directory for this branch.');
              return;
            }
            // Baseline captured now, at the true "about to edit" boundary — not at whenever the
            // proposal/conflict happened to load, which could be stale by the time editing starts.
            this.editBaselines.set(filePath, content ?? '');
            void this.panel.webview.postMessage({ type: 'editBaselineSet', path: filePath });
            const realUri = vscode.Uri.file(path.join(workingDirectory, filePath));
            const doc = await vscode.workspace.openTextDocument(realUri);
            await vscode.window.showTextDocument(doc, { preview: false });
          } catch (err) {
            void vscode.window.showErrorMessage('NodalMerge: could not open ' + filePath + ' for editing — ' + String(err));
          }
          return;
        }
        case 'resyncWorkspace': {
          const branchId = this.editableBranchId;
          if (!branchId) {
            void vscode.window.showWarningMessage('NodalMerge: no branch loaded to resync.');
            return;
          }
          if (this.editBaselines.size === 0) {
            void vscode.window.showInformationMessage(
              'NodalMerge: nothing to resync — use "Edit File" first to open a file for editing.');
            return;
          }
          try {
            const { workingDirectory } = await this.get<{ workingDirectory: string | null }>(
              '/studio/workspace/path?branchId=' + encodeURIComponent(branchId));
            if (!workingDirectory) {
              void vscode.window.showErrorMessage('NodalMerge: no materialized working directory for this branch.');
              return;
            }
            const files: { path: string; content: string }[] = [];
            const deletedPaths: string[] = [];
            for (const [filePath, baseline] of this.editBaselines) {
              const fullPath = path.join(workingDirectory, filePath);
              let current: string | undefined;
              try {
                current = await fs.promises.readFile(fullPath, 'utf8');
              } catch {
                current = undefined; // file no longer exists on disk
              }
              if (current === undefined) {
                if (baseline !== '') deletedPaths.push(filePath);
              } else if (current !== baseline) {
                files.push({ path: filePath, content: current });
              }
            }
            if (files.length === 0 && deletedPaths.length === 0) {
              void vscode.window.showInformationMessage('NodalMerge: no changes found in the file(s) you edited.');
              return;
            }
            const result = await this.post<{ written: string[]; deleted: string[]; errors: { path: string; error: string }[] }>(
              '/studio/branches/' + encodeURIComponent(branchId) + '/resync-files',
              { files, deletedPaths });
            this.editBaselines.clear();
            if (result.errors?.length) {
              void vscode.window.showWarningMessage(
                'NodalMerge: resynced with errors — ' + result.errors.map(e => e.path + ': ' + e.error).join('; '));
            } else {
              void vscode.window.showInformationMessage(
                `NodalMerge: resynced ${result.written.length} file(s)` +
                (result.deleted.length ? `, deleted ${result.deleted.length}` : '') + '.');
            }
          } catch (err) {
            void vscode.window.showErrorMessage('NodalMerge: resync failed — ' + String(err));
          }
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
        case 'reviseDecision':
          await this.post('/studio/merges/' + this.proposalId + '/review', {
            decision: 'Rejected',
            restartMode: 'Revise',
            notes: (msg.notes as string | undefined) || undefined,
            sessionId: this.getEffectiveSessionId(),
          });
          void vscode.window.showWarningMessage('Revising with your notes as steering context.');
          break;
        case 'revertAndRestart':
          await this.post('/studio/merges/' + this.proposalId + '/review', {
            decision: 'Rejected',
            restartMode: 'Revert',
            notes: (msg.notes as string | undefined) || undefined,
            sessionId: this.getEffectiveSessionId(),
          });
          void vscode.window.showWarningMessage('Reverting the workspace and restarting with your notes as steering context.');
          break;
        case 'unrejectAndRevise': {
          // A proposal that's already Rejected has no legal transition back through /review — the
          // only way back is the standalone retry primitive. Requiring notes here (unlike accept)
          // is deliberate: a hard-rejected proposal already has no recorded reason half the time
          // (see the MCP-bypass bug this button exists to give a manual escape hatch from), so the
          // retried worker needs SOMETHING to go on or it's back to guessing.
          const notes = (msg.notes as string | undefined) || undefined;
          if (!notes) {
            void vscode.window.showWarningMessage('NodalMerge: add a note first — the retried worker has no other context for why this was rejected.');
            return;
          }
          try {
            const result = await this.post<{ outcome: string }>(
              '/studio/merges/' + this.proposalId + '/retry',
              { notes, sessionId: this.getEffectiveSessionId() });
            if (result.outcome === 'EscalatedToDeadLetter') {
              void vscode.window.showWarningMessage(
                'NodalMerge: max retry attempts already reached — escalated to the dead-letter queue instead. Use Dead Letters > Retry with Context.');
            } else {
              void vscode.window.showInformationMessage('NodalMerge: retrying with your notes as steering context.');
            }
          } catch (err) {
            void vscode.window.showErrorMessage('NodalMerge: retry failed — ' + String(err));
          }
          break;
        }
        case 'applyDecision':
          try {
            await this.post('/studio/merges/' + this.proposalId + '/apply', {});
            void vscode.window.showInformationMessage('Decision applied successfully.');
          } catch (err) {
            // A conflicting apply isn't a dead end — the backend auto-spawns a reconciliation
            // agent (or records a conflict a human can Reconcile/Resolve) and says so in the
            // error text. Showing that as a raw failure toast made it look like nothing happened
            // while an agent was already working; distinguish "in progress elsewhere" from a
            // genuine failure.
            const text = String(err);
            if (text.includes('reconciliation agent has been started')) {
              void vscode.window.showInformationMessage(
                'NodalMerge: conflict detected — a reconciliation agent is combining both versions now. ' +
                'Its merged proposal will supersede this one; progress is visible in the Activity Center.');
            } else {
              void vscode.window.showErrorMessage('NodalMerge: apply failed — ' + text);
            }
          }
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
      <span class="meta-label">Target</span>         <span class="meta-value" id="target-branch"></span><button class="ghost hidden" id="btn-view-candidate-promotion" style="margin-left:8px">View in Candidate Promotion</button>
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
        Edit a conflicting file below, then use <strong>Resync Workspace</strong> to record your
        fix as a real, hash-linked change on this branch. This report itself won't clear on its
        own even after a successful fix — re-trigger review for this work unit (from Activity
        Center, or via the API) to actually retry the merge.
      </p>
      <div id="conflict-file-rows"></div>
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
      <label for="review-notes">Notes (steering direction for a revise/revert, or context for an accept)</label>
      <textarea id="review-notes" rows="3" placeholder="e.g. Missing the edge case for empty input — handle that and resubmit."></textarea>
    </div>
    <div id="actions" class="actions">
      <button id="btn-validate">Validate Evidence</button>
      <button id="btn-accept" class="accept">Accept Decision</button>
      <button id="btn-revise" class="reject" title="Keep the current file changes and nudge the agent toward the gap.">Revise</button>
      <button id="btn-revert" class="reject" title="Discard the agent's changes and start the goal over with your notes.">Revert and Restart</button>
      <button id="btn-unreject" class="reject" title="This proposal is already Rejected (a dead end otherwise — no other action can move it) — reset the task to Queued and retry with your notes as steering context.">Unreject and Revise</button>
      <button id="btn-apply"  class="apply">Apply Decision</button>
      <button id="btn-fork"   class="ghost">Fork Hypothesis</button>
      <button id="btn-restore" class="ghost">Restore workspace</button>
    </div>
  </div>
`;

