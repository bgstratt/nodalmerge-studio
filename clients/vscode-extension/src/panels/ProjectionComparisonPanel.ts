import * as vscode from 'vscode';
import { scopeViewCss } from './sharedWebviewChrome';

// ── Domain types matching IProjectionSnapshotService's REST shapes (camelCase via
// JsonStringEnumConverter + JsonSerializerOptions.Web on the Host's REST JSON options). ──

interface ArtifactRef {
  artifactId: string;
  type: string;
  parentArtifactId?: string | null;
  status: string;
  createdAt: string;
  ownedByWorkUnitId?: string | null;
  ownedByAgentId?: string | null;
  title?: string | null;
  body?: string | null;
  invalidatedByArtifactId?: string | null;
}

interface AgentWorkspaceProjectionPayload {
  agentId?: string | null;
  workUnitId?: string | null;
  artifacts: { artifacts: ArtifactRef[] };
  inheritedConstraints: ArtifactRef[];
}

interface ProjectionSnapshot {
  snapshotId: string;
  workUnitId: string;
  payload: AgentWorkspaceProjectionPayload;
  createdAt: string;
}

interface ProjectionStaleness {
  snapshotId: string;
  isStale: boolean;
  staleArtifactIds: string[];
}

interface ArtifactStatusDivergence {
  artifactId: string;
  statusA: string;
  statusB: string;
}

interface ProjectionComparison {
  snapshotIdA: string;
  snapshotIdB: string;
  onlyInA: ArtifactRef[];
  onlyInB: ArtifactRef[];
  differingStatus: ArtifactStatusDivergence[];
}

interface KnownGoodState {
  stateId: string;
  branchId: string;
  description: string;
  verificationResults?: string | null;
  createdAt: string;
  createdBy: string;
  snapshotBranchId?: string | null;
}

interface FileDiffEntry {
  relativePath: string;
  status: string; // "Added" | "Removed" | "Modified"
}

interface KnownGoodDiffResult {
  stateIdA: string;
  stateIdB: string;
  differences: FileDiffEntry[];
  addedCount: number;
  removedCount: number;
  modifiedCount: number;

}

interface MaterializationResult {
  workUnitId: string;
  snapshotId: string;
  targetKind: string;
  targetPath: string;
  fileCount: number;
  durationMs: number;
  succeeded: boolean;
  error?: string | null;
}

// ── Panel ──────────────────────────────────────────────────────────────────

// Slice 17b — on-demand inspection tool for the persisted Projection snapshot capability
// (IProjectionSnapshotService): capture an immutable AgentWorkspace projection for a work unit,
// check whether it's gone stale (an artifact it referenced was later invalidated), and diff two
// sibling snapshots (e.g. a React fork vs a Vue fork) as a symmetric set difference. No polling
// timer, unlike WorkspaceDashboardPanel — this mirrors InsightsPanel's manual-trigger shape since
// snapshots are point-in-time captures, not a live feed.
export class ProjectionComparisonPanel {
  static readonly containerId = 'shell-pane-projection-comparison';

  private readonly panel: vscode.WebviewPanel;
  private readonly baseUrl: string;

  constructor(panel: vscode.WebviewPanel, baseUrl: string) {
    this.panel = panel;
    this.baseUrl = baseUrl;
  }

  activate(): void {
    void this.sendList();
    // Slice 18 — lets the webview connect to the same /ws/runtime room ArtifactExplorerPanel
    // already uses for live stage badges, so a snapshot's staleness badge can flip the moment an
    // artifact is invalidated elsewhere, instead of waiting for a manual "Check Stale" click.
    void this.panel.webview.postMessage({
      type: 'pcWsInit',
      wsUrl: this.baseUrl.replace(/^http/, 'ws') + '/ws/runtime',
    });
  }

  dispose(): void {
    // No timers/subscriptions to tear down.
  }

  static getFragment(): { css: string; html: string } {
    return {
      css: scopeViewCss(PC_CSS, ProjectionComparisonPanel.containerId),
      html: `<div id="${ProjectionComparisonPanel.containerId}" class="nm-shell-pane">${PC_HTML}</div>`,
    };
  }

  private async sendList(workUnitId?: string): Promise<void> {
    try {
      const query = workUnitId ? '?workUnitId=' + encodeURIComponent(workUnitId) : '';
      const snapshots = await this.get<ProjectionSnapshot[]>('/studio/projections/snapshots' + query);
      void this.panel.webview.postMessage({ type: 'pcSnapshotList', snapshots });
    } catch {
      // host not ready yet
    }
  }

  async handleMessage(msg: Record<string, unknown>): Promise<void> {
    switch (msg.type as string) {
      case 'pcCapture': {
        try {
          const snapshot = await this.post<ProjectionSnapshot>(
            '/studio/projections/snapshots', { workUnitId: msg.workUnitId as string },
          );
          void this.panel.webview.postMessage({ type: 'pcCaptured', snapshot });
          await this.sendList();
        } catch (err) {
          void this.panel.webview.postMessage({ type: 'pcError', message: String(err) });
        }
        return;
      }
      case 'pcRefreshList': {
        await this.sendList(msg.workUnitId as string | undefined);
        return;
      }
      case 'pcCheckStale': {
        try {
          const snapshotId = msg.snapshotId as string;
          const staleness = await this.get<ProjectionStaleness>(
            `/studio/projections/snapshots/${encodeURIComponent(snapshotId)}/stale`,
          );
          void this.panel.webview.postMessage({ type: 'pcStaleResult', ...staleness });
        } catch (err) {
          void this.panel.webview.postMessage({ type: 'pcError', message: String(err) });
        }
        return;
      }
      case 'pcCompare': {
        try {
          const a = encodeURIComponent(msg.snapshotIdA as string);
          const b = encodeURIComponent(msg.snapshotIdB as string);
          const comparison = await this.get<ProjectionComparison>(
            `/studio/projections/snapshots/compare?a=${a}&b=${b}`,
          );
          void this.panel.webview.postMessage({ type: 'pcCompareResult', comparison });
        } catch (err) {
          void this.panel.webview.postMessage({ type: 'pcError', message: String(err) });
        }
        return;
      }
      case 'pcFindKgs': {
        try {
          const branchId = msg.branchId as string;
          const states = await this.get<KnownGoodState[]>(
            `/studio/state/knownGood/${encodeURIComponent(branchId)}`,
          );
          void this.panel.webview.postMessage({ type: 'pcKgsList', states });
        } catch (err) {
          void this.panel.webview.postMessage({ type: 'pcKgsError', message: String(err) });
        }
        return;
      }
      case 'pcRestoreKgs': {
        try {
          const stateId = msg.stateId as string;
          const result = await this.post<MaterializationResult>(
            `/studio/projections/known-good/${encodeURIComponent(stateId)}/materialize`,
            {},
          );
          void this.panel.webview.postMessage({ type: 'pcKgsRestored', result });
          if (result.succeeded) {
            void vscode.window.showInformationMessage(
              `NodalMerge: Restored ${result.fileCount} file(s) to ${result.targetPath}. Reload editors if needed.`,
              'OK',
            );
          } else {
            void vscode.window.showWarningMessage(
              `NodalMerge: Restore completed with errors — ${result.error ?? 'unknown error'}.`,
            );
          }
        } catch (err) {
          void this.panel.webview.postMessage({ type: 'pcKgsError', message: String(err) });
        }
        return;
      }
      case 'pcDiffKgs': {
        try {
          const a = encodeURIComponent(msg.stateIdA as string);
          const b = encodeURIComponent(msg.stateIdB as string);
          const diff = await this.get<KnownGoodDiffResult>(
            `/studio/projections/known-good/${a}/diff/${b}`,
          );
          void this.panel.webview.postMessage({ type: 'pcKgsDiffResult', diff });
        } catch (err) {
          void this.panel.webview.postMessage({ type: 'pcKgsError', message: String(err) });
        }
        return;
      }
      default:
        return;
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

  private async post<T>(path: string, body: unknown): Promise<T> {
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

const PC_CSS = `
  :scope { display: flex; flex-direction: column; height: 100%; padding: 14px 18px; gap: 14px; overflow-y: auto; }
  .pc-header { display: flex; align-items: center; gap: 8px; flex-wrap: wrap; }
  .pc-header h2 { margin: 0; font-size: 1.1em; }
  .pc-header input { padding: 2px 6px; min-width: 180px; }
  .pc-empty { opacity: 0.65; font-style: italic; }
  .pc-error { color: var(--nm-error); }
  .pc-section h3 { font-size: 0.95em; margin: 0 0 6px; }
  table.pc-table { width: 100%; border-collapse: collapse; font-size: 0.85em; }
  table.pc-table th, table.pc-table td { text-align: left; padding: 4px 8px; border-bottom: 1px solid var(--nm-border); }
  table.pc-table th { opacity: 0.7; font-weight: 600; }
  .pc-badge { font-size: 0.72em; padding: 1px 6px; border-radius: 3px; border: 1px solid var(--nm-border); opacity: 0.8; }
  .pc-badge-stale { color: var(--nm-warn); border-color: var(--nm-warn); }
  .pc-badge-fresh { color: var(--nm-success); border-color: var(--nm-success); }
  .pc-compare-bar { display: flex; align-items: center; gap: 8px; flex-wrap: wrap; }
  .pc-compare-bar select { min-width: 220px; }
  .pc-compare-results { display: flex; gap: 14px; flex-wrap: wrap; }
  .pc-compare-results > div { flex: 1; min-width: 240px; }
  .pc-divider { border: none; border-top: 1px solid var(--nm-border); margin: 18px 0 10px; }
  .pc-kgs-row { display: flex; align-items: center; gap: 8px; padding: 5px 0; border-bottom: 1px solid var(--nm-border); flex-wrap: wrap; }
  .pc-kgs-row:last-child { border-bottom: none; }
  .pc-kgs-desc { font-weight: 600; flex: 1; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .pc-kgs-meta { font-size: 0.78em; opacity: 0.6; white-space: nowrap; }
  .pc-kgs-actions { display: flex; gap: 4px; flex-shrink: 0; }
  .pc-diff-file { display: flex; align-items: center; gap: 8px; padding: 2px 0; font-size: 0.85em; }
  .pc-diff-added   { color: var(--nm-success); }
  .pc-diff-removed { color: var(--nm-error); }
  .pc-diff-modified { color: var(--nm-warn); }
  .pc-diff-summary { font-size: 0.85em; margin-bottom: 6px; opacity: 0.75; }
`;

const PC_HTML = `
  <div class="pc-header">
    <h2>Projection Snapshots</h2>
    <input id="pc-wuid-input" type="text" placeholder="Work Unit ID">
    <button id="pc-capture-btn">Capture Snapshot</button>
    <button id="pc-refresh-btn">Refresh List</button>
  </div>
  <p id="pc-status" class="pc-empty"></p>

  <div class="pc-section">
    <h3>Captured Snapshots</h3>
    <div id="pc-list"><p class="pc-empty">No snapshots captured yet.</p></div>
  </div>

  <div class="pc-section">
    <h3>Compare Two Snapshots</h3>
    <div class="pc-compare-bar">
      <select id="pc-compare-a"><option value="">(select snapshot A)</option></select>
      <select id="pc-compare-b"><option value="">(select snapshot B)</option></select>
      <button id="pc-compare-btn">Compare</button>
    </div>
    <div id="pc-compare-results"></div>
  </div>

  <hr class="pc-divider"/>

  <div class="pc-section">
    <h3>Known Good States</h3>
    <div class="pc-compare-bar">
      <input id="pc-kgs-branch-input" type="text" placeholder="Branch ID">
      <button id="pc-kgs-find-btn">Find</button>
    </div>
    <p id="pc-kgs-status" class="pc-empty"></p>
    <div id="pc-kgs-list"></div>
  </div>

  <div class="pc-section" id="pc-kgs-diff-section" style="display:none">
    <h3>Diff Two Known Good States</h3>
    <div class="pc-compare-bar">
      <select id="pc-kgs-diff-a"><option value="">(select state A)</option></select>
      <select id="pc-kgs-diff-b"><option value="">(select state B)</option></select>
      <button id="pc-kgs-diff-btn">Diff Files</button>
    </div>
    <div id="pc-kgs-diff-results"></div>
  </div>
`;

