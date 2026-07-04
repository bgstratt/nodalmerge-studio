import * as vscode from 'vscode';
import type { AgentConfigService } from '../AgentConfigService';
import { scopeViewCss, wrapViewScript } from './sharedWebviewChrome';

// ── Domain types matching RunRetrospectiveProjectionPayload (camelCase via JsonSerializerOptions.Web) ──

interface ModelPerformanceStat {
  model: string;
  provider?: string | null;
  proposalCount: number;
  mergedCount: number;
  rejectedCount: number;
  acceptanceRate: number;
  avgConfidence?: number | null;
}

interface ModelStagePerformanceStat {
  model: string;
  stage: string;
  proposalCount: number;
  mergedCount: number;
  rejectedCount: number;
  acceptanceRate: number;
}

interface ForkWinRateStat {
  forkType: string;
  totalForks: number;
  wins: number;
  losses: number;
  pending: number;
  winRate: number;
}

interface ForkConstraintWinRateStat {
  forkType: string;
  constraint: string;
  totalForks: number;
  wins: number;
  losses: number;
  pending: number;
  winRate: number;
}

interface FailureCauseStat {
  category: string;
  totalCount: number;
  workUnitsAffected: number;
}

interface ReviewOutcomeStat {
  outcome: string;
  count: number;
  avgConfidence?: number | null;
}

interface RunRetrospective {
  since?: string | null;
  until?: string | null;
  totalSessions: number;
  sessionsByStatus: Record<string, number>;
  totalWorkUnits: number;
  workUnitsByStatus: Record<string, number>;
  overallSuccessRate: number;
  averageReworkCycles: number;
  topFailureCause?: FailureCauseStat | null;
  mostSuccessfulModel?: ModelPerformanceStat | null;
  mostSuccessfulStrategy?: ForkWinRateStat | null;
  modelPerformance: ModelPerformanceStat[];
  modelPerformanceByStage: ModelStagePerformanceStat[];
  forkWinRates: ForkWinRateStat[];
  forkConstraintWinRates: ForkConstraintWinRateStat[];
  failureCauses: FailureCauseStat[];
  reviewOutcomes: ReviewOutcomeStat[];
  generatedAt: string;
}

// Matches NodalMerge.Studio.Contracts.Domain.Finding (camelCase via JsonStringEnumConverter +
// JsonSerializerOptions.Web on the Host's REST JSON options).
interface Finding {
  findingId: string;
  kind: string;
  source: string;
  title: string;
  summary: string;
  supportingDataJson?: string | null;
  status: string;
  createdAt: string;
  reviewNotes?: string | null;
  reviewedAt?: string | null;
  promotedArtifactId?: string | null;
  targetStage?: string | null;
}

// The portable export shape — deliberately excludes findingId/status/source/promotedArtifactId,
// which are meaningless (or actively wrong) once moved to a different repo.
interface ExportableFinding {
  kind: string;
  title: string;
  summary: string;
  targetStage?: string | null;
}

// ── Panel ──────────────────────────────────────────────────────────────────

// Analytics dashboard + Knowledge Promotion review queue for the DAG's run history. Three
// independent manual triggers — "Run Analysis" (dashboard), "Detect Findings" (deterministic
// pattern rules), "Run LLM Scan" (calls a real model using the user's own credentials) — none of
// them chained to each other or to anything automatic. Viewing the Findings queue itself (on tab
// activation) is just a data read, not a trigger.
export class InsightsPanel {
  static readonly containerId = 'shell-pane-insights';

  private readonly panel: vscode.WebviewPanel;
  private readonly baseUrl: string;
  private readonly configService: AgentConfigService | undefined;
  private readonly secrets: vscode.SecretStorage | undefined;
  private readonly lmProxyBaseUrl: string | undefined;
  private currentStatusFilter = 'Open';

  constructor(
    panel: vscode.WebviewPanel,
    baseUrl: string,
    configService?: AgentConfigService,
    secrets?: vscode.SecretStorage,
    lmProxyBaseUrl?: string,
  ) {
    this.panel = panel;
    this.baseUrl = baseUrl;
    this.configService = configService;
    this.secrets = secrets;
    this.lmProxyBaseUrl = lmProxyBaseUrl;
  }

  activate(): void {
    this.sendProfiles();
    void this.sendFindings();
  }

  dispose(): void {
    // No timers/subscriptions to tear down.
  }

  static getFragment(): { css: string; html: string; script: string } {
    return {
      css: scopeViewCss(IN_CSS, InsightsPanel.containerId),
      html: `<div id="${InsightsPanel.containerId}" class="nm-shell-pane">${IN_HTML}</div>`,
      script: wrapViewScript(IN_JS, InsightsPanel.containerId),
    };
  }

  private sendProfiles(): void {
    // vscode-lm profiles legitimately have an empty model — the LM proxy resolves the actual
    // model at runtime, so a blank model string there isn't "unconfigured." Excluding them (the
    // way Multi-Model Comparison's orchProfiles filter does, since that feature needs a distinct
    // model name to label "A vs B") would hide every default Orchestrator/Worker profile here for
    // no reason — this scan has no such requirement.
    const profiles = (this.configService?.getProfiles() ?? [])
      .filter(p => p.model || p.provider === 'vscode-lm');
    void this.panel.webview.postMessage({
      type: 'insightsProfiles',
      profiles: profiles.map(p => ({ id: p.id, label: p.label, model: p.model })),
    });
  }

  private async sendFindings(): Promise<void> {
    try {
      const query = this.currentStatusFilter === 'All' ? '' : '?status=' + encodeURIComponent(this.currentStatusFilter);
      const findings = await this.get<Finding[]>('/studio/findings' + query);
      void this.panel.webview.postMessage({
        type: 'insightsFindingsList',
        findings,
        statusFilter: this.currentStatusFilter,
      });
    } catch {
      // host not ready yet
    }
  }

  async handleMessage(msg: Record<string, unknown>): Promise<void> {
    switch (msg.type as string) {
      case 'insightsRunAnalysis': {
        try {
          const period = msg.period as string | undefined;
          const since = period === 'last30' ? new Date(Date.now() - 30 * 24 * 60 * 60 * 1000).toISOString() : undefined;
          const query = since ? '?level=Normal&since=' + encodeURIComponent(since) : '?level=Normal';
          const result = await this.get<{ data: RunRetrospective }>('/studio/projections/RunRetrospective' + query);
          void this.panel.webview.postMessage({ type: 'insightsResult', data: result.data });
        } catch (err) {
          void this.panel.webview.postMessage({ type: 'insightsError', message: String(err) });
        }
        return;
      }
      case 'insightsDetectFindings': {
        try {
          await this.post('/studio/insights/detect-findings', {});
          await this.sendFindings();
        } catch (err) {
          void vscode.window.showErrorMessage('NodalMerge: Detect Findings failed — ' + String(err));
        }
        return;
      }
      case 'insightsRunLlmScan': {
        await this.handleRunLlmScan(msg.profileId as string);
        return;
      }
      case 'insightsReviewFinding': {
        try {
          await this.post(`/studio/findings/${encodeURIComponent(msg.findingId as string)}/review`, {
            decision: msg.decision as string,
            notes: (msg.notes as string) || undefined,
          });
          await this.sendFindings();
        } catch (err) {
          void vscode.window.showErrorMessage('NodalMerge: Finding review failed — ' + String(err));
        }
        return;
      }
      case 'insightsFindingsFilterChanged': {
        this.currentStatusFilter = (msg.status as string) || 'Open';
        await this.sendFindings();
        return;
      }
      case 'insightsExportFindings': {
        await this.handleExportFindings(msg.findings as ExportableFinding[]);
        return;
      }
      case 'insightsImportFindings': {
        await this.handleImportFindings();
        return;
      }
      default:
        return;
    }
  }

  private async handleExportFindings(findings: ExportableFinding[]): Promise<void> {
    if (!findings || findings.length === 0) {
      void vscode.window.showWarningMessage('NodalMerge: select at least one finding to export.');
      return;
    }
    const uri = await vscode.window.showSaveDialog({
      filters: { 'JSON': ['json'] },
      saveLabel: 'Export Findings',
      defaultUri: vscode.Uri.file('nodalmerge-findings.json'),
    });
    if (!uri) { return; }

    const payload = { version: 1, exportedAt: new Date().toISOString(), findings };
    try {
      await vscode.workspace.fs.writeFile(uri, Buffer.from(JSON.stringify(payload, null, 2), 'utf8'));
      void vscode.window.showInformationMessage(
        `NodalMerge: exported ${findings.length} finding(s) to ${uri.fsPath}.`,
      );
    } catch (err) {
      void vscode.window.showErrorMessage('NodalMerge: export failed — ' + String(err));
    }
  }

  private async handleImportFindings(): Promise<void> {
    const uris = await vscode.window.showOpenDialog({
      canSelectMany: false,
      filters: { 'JSON': ['json'] },
      openLabel: 'Import Findings',
    });
    if (!uris || uris.length === 0) { return; }

    let entries: unknown[];
    try {
      const bytes = await vscode.workspace.fs.readFile(uris[0]);
      const parsed = JSON.parse(Buffer.from(bytes).toString('utf8')) as { findings?: unknown[] };
      entries = Array.isArray(parsed.findings) ? parsed.findings : [];
    } catch (err) {
      void vscode.window.showErrorMessage('NodalMerge: could not read findings file — ' + String(err));
      return;
    }
    if (entries.length === 0) {
      void vscode.window.showWarningMessage('NodalMerge: no findings found in that file.');
      return;
    }

    try {
      const result = await this.post<{ imported: Finding[]; skipped: number }>(
        '/studio/findings/import', { findings: entries },
      );
      const skippedNote = result.skipped > 0
        ? `, skipped ${result.skipped} invalid entr${result.skipped === 1 ? 'y' : 'ies'}`
        : '';
      void vscode.window.showInformationMessage(
        `NodalMerge: imported ${result.imported.length} finding(s)${skippedNote}.`,
      );
      await this.sendFindings();
    } catch (err) {
      void vscode.window.showErrorMessage('NodalMerge: import failed — ' + String(err));
    }
  }

  private async handleRunLlmScan(profileId: string): Promise<void> {
    if (!profileId) {
      void vscode.window.showWarningMessage(
        'NodalMerge: no profile selected — add one with a model set in Model & Agent Studio first.',
      );
      void this.panel.webview.postMessage({ type: 'insightsLlmScanDone' });
      return;
    }
    if (!this.configService || !this.secrets || !this.lmProxyBaseUrl) {
      void vscode.window.showWarningMessage(
        'NodalMerge: LLM credentials required — configure profiles in Model & Agent Studio.',
      );
      void this.panel.webview.postMessage({ type: 'insightsLlmScanDone' });
      return;
    }
    try {
      const cfg = await this.configService.resolveSpawnLlmConfig(profileId, this.secrets, this.lmProxyBaseUrl);
      if (!cfg) {
        const reason = await this.configService.describeMissingCredentials(profileId, this.secrets, this.lmProxyBaseUrl);
        throw new Error(`Selected profile isn't ready — ${reason}.`);
      }
      await this.post('/studio/insights/llm-scan', cfg);
      await this.sendFindings();
    } catch (err) {
      void vscode.window.showErrorMessage('NodalMerge: LLM scan failed — ' + String(err));
    } finally {
      void this.panel.webview.postMessage({ type: 'insightsLlmScanDone' });
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
    // LLM scans can take well over the dashboard's 8s budget — a real model call, not a local read.
    const controller = new AbortController();
    const timeout = setTimeout(() => controller.abort(new Error('timed out after 60000ms')), 60_000);
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

const IN_CSS = `
  :scope { display: flex; flex-direction: column; height: 100%; padding: 14px 18px; gap: 14px; overflow-y: auto; }
  .in-header { display: flex; align-items: center; gap: 12px; flex-wrap: wrap; }
  .in-header h2 { margin: 0; font-size: 1.1em; }
  .in-generated-at { font-size: 0.8em; opacity: 0.65; }
  #in-run-btn { padding: 4px 14px; }
  #in-period { padding: 2px 6px; }
  .in-empty { opacity: 0.65; font-style: italic; }
  .in-error { color: var(--nm-error); }
  .in-overview { display: flex; gap: 18px; flex-wrap: wrap; }
  .in-card { border: 1px solid var(--nm-border); border-radius: 4px; padding: 10px 14px; min-width: 140px; }
  .in-card .in-card-value { font-size: 1.4em; font-weight: 600; }
  .in-card .in-card-label { font-size: 0.8em; opacity: 0.7; }
  .in-card .in-card-sub { font-size: 0.72em; opacity: 0.55; margin-top: 2px; }
  .in-section h3 { font-size: 0.95em; margin: 0 0 6px; }
  .in-section-note { font-size: 0.75em; opacity: 0.6; margin: -4px 0 6px; }
  table.in-table { width: 100%; border-collapse: collapse; font-size: 0.85em; }
  table.in-table th, table.in-table td { text-align: left; padding: 4px 8px; border-bottom: 1px solid var(--nm-border); }
  table.in-table th { opacity: 0.7; font-weight: 600; }
  .in-findings-toolbar { display: flex; align-items: center; gap: 10px; flex-wrap: wrap; border: 1px solid var(--nm-border); border-radius: 4px; padding: 8px 10px; }
  .in-findings-toolbar .in-hint { font-size: 0.72em; opacity: 0.6; }
  .in-finding-card { border: 1px solid var(--nm-border); border-radius: 4px; padding: 8px 12px; margin-bottom: 8px; }
  .in-finding-card-head { display: flex; align-items: center; gap: 8px; }
  .in-finding-title { font-weight: 600; flex: 1; }
  .in-finding-badge { font-size: 0.7em; padding: 1px 6px; border-radius: 3px; border: 1px solid var(--nm-border); opacity: 0.75; }
  .in-finding-summary { font-size: 0.85em; opacity: 0.85; margin: 6px 0; }
  .in-finding-actions { display: flex; gap: 6px; }
  .in-finding-actions button { font-size: 0.8em; padding: 2px 10px; }
  .in-finding-meta { font-size: 0.72em; opacity: 0.6; }
`;

const IN_HTML = `
  <div class="in-header">
    <h2>Insights</h2>
    <select id="in-period">
      <option value="last30">Last 30 Days</option>
      <option value="all">All Time</option>
    </select>
    <button id="in-run-btn">&#x25B6; Run Analysis</button>
    <span class="in-generated-at" id="in-generated-at"></span>
  </div>
  <div id="in-body"><p class="in-empty">No analysis run yet — click "Run Analysis" to aggregate outcomes across every goal, work unit, and proposal recorded so far.</p></div>

  <div class="in-section">
    <h3>Findings — Knowledge &amp; Process Improvements</h3>
    <div class="in-findings-toolbar">
      <button id="in-detect-btn">Detect Findings</button>
      <span class="in-hint">free, instant, deterministic rules</span>
      <span style="flex:1"></span>
      <select id="in-llm-profile"><option value="">(no profile configured)</option></select>
      <button id="in-llm-scan-btn">Run LLM Scan</button>
      <span class="in-hint">calls a real model with your credentials — real cost &amp; latency</span>
    </div>
    <div class="in-findings-toolbar">
      <label class="in-hint" for="in-findings-status">View:</label>
      <select id="in-findings-status">
        <option value="Open">Open</option>
        <option value="Promoted">Promoted</option>
        <option value="Dismissed">Dismissed</option>
        <option value="Investigating">Investigating</option>
        <option value="All">All</option>
      </select>
      <span id="in-export-controls" style="display:none">
        <button id="in-select-all-btn">Select All</button>
        <button id="in-export-btn">Export Selected</button>
      </span>
      <span style="flex:1"></span>
      <button id="in-import-btn">Import Findings&hellip;</button>
      <span class="in-hint">bring in findings exported from another repo</span>
    </div>
    <div id="in-findings-list"><p class="in-empty">No findings yet.</p></div>
  </div>
`;

const IN_JS = `
  var vscode = acquireVsCodeApi();
  var state = { profiles: [], lastFindings: [] };

  function esc(s) { return String(s == null ? '' : s).replace(/[&<>"']/g, function(c) {
    return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c];
  }); }
  function pct(n) { return (n * 100).toFixed(0) + '%'; }
  function conf(n) { return (n === null || n === undefined) ? '—' : n.toFixed(2); }

  document.getElementById('in-run-btn').addEventListener('click', function() {
    document.getElementById('in-body').innerHTML = '<p class="in-empty">Running analysis&hellip;</p>';
    vscode.postMessage({ type: 'insightsRunAnalysis', period: document.getElementById('in-period').value });
  });

  document.getElementById('in-detect-btn').addEventListener('click', function() {
    this.disabled = true;
    vscode.postMessage({ type: 'insightsDetectFindings' });
  });

  document.getElementById('in-llm-scan-btn').addEventListener('click', function() {
    var profileId = document.getElementById('in-llm-profile').value;
    this.disabled = true;
    this.textContent = 'Scanning…';
    vscode.postMessage({ type: 'insightsRunLlmScan', profileId: profileId });
  });

  document.getElementById('in-findings-status').addEventListener('change', function() {
    document.getElementById('in-export-controls').style.display = this.value === 'Promoted' ? '' : 'none';
    vscode.postMessage({ type: 'insightsFindingsFilterChanged', status: this.value });
  });

  document.getElementById('in-select-all-btn').addEventListener('click', function() {
    document.querySelectorAll('.in-finding-select').forEach(function(cb) { cb.checked = true; });
  });

  document.getElementById('in-export-btn').addEventListener('click', function() {
    var ids = Array.prototype.map.call(
      document.querySelectorAll('.in-finding-select:checked'),
      function(cb) { return cb.getAttribute('data-finding-id'); }
    );
    var selected = (state.lastFindings || []).filter(function(f) { return ids.indexOf(f.findingId) !== -1; });
    var exportable = selected.map(function(f) {
      return { kind: f.kind, title: f.title, summary: f.summary, targetStage: f.targetStage || null };
    });
    vscode.postMessage({ type: 'insightsExportFindings', findings: exportable });
  });

  document.getElementById('in-import-btn').addEventListener('click', function() {
    vscode.postMessage({ type: 'insightsImportFindings' });
  });

  function renderHighlights(data) {
    var cards = '';
    cards += '<div class="in-card"><div class="in-card-value">' + data.averageReworkCycles.toFixed(1) + '</div><div class="in-card-label">Avg Rework Cycles</div></div>';
    if (data.topFailureCause) {
      cards += '<div class="in-card"><div class="in-card-value">' + esc(data.topFailureCause.category) + '</div><div class="in-card-label">Top Failure Cause</div>' +
        '<div class="in-card-sub">' + data.topFailureCause.totalCount + ' occurrences</div></div>';
    }
    if (data.mostSuccessfulModel) {
      cards += '<div class="in-card"><div class="in-card-value">' + esc(data.mostSuccessfulModel.model) + '</div><div class="in-card-label">Most Successful Model</div>' +
        '<div class="in-card-sub">' + pct(data.mostSuccessfulModel.acceptanceRate) + ' over ' + data.mostSuccessfulModel.proposalCount + ' proposals</div></div>';
    }
    if (data.mostSuccessfulStrategy) {
      cards += '<div class="in-card"><div class="in-card-value">' + esc(data.mostSuccessfulStrategy.forkType) + '</div><div class="in-card-label">Most Successful Strategy</div>' +
        '<div class="in-card-sub">' + pct(data.mostSuccessfulStrategy.winRate) + ' win rate (' + data.mostSuccessfulStrategy.wins + '/' + (data.mostSuccessfulStrategy.wins + data.mostSuccessfulStrategy.losses) + ')</div></div>';
    }
    if (!cards) { return ''; }
    return '<div class="in-section"><h3>Retrospective Highlights</h3><div class="in-overview">' + cards + '</div></div>';
  }

  function renderOverview(data) {
    var statusRows = Object.keys(data.workUnitsByStatus || {}).map(function(k) {
      return '<div class="in-card"><div class="in-card-value">' + data.workUnitsByStatus[k] + '</div><div class="in-card-label">' + esc(k) + '</div></div>';
    }).join('');
    return '' +
      '<div class="in-section">' +
        '<div class="in-overview">' +
          '<div class="in-card"><div class="in-card-value">' + data.totalSessions + '</div><div class="in-card-label">Sessions</div></div>' +
          '<div class="in-card"><div class="in-card-value">' + data.totalWorkUnits + '</div><div class="in-card-label">Work Units</div></div>' +
          '<div class="in-card"><div class="in-card-value">' + pct(data.overallSuccessRate) + '</div><div class="in-card-label">Overall Success Rate</div></div>' +
          statusRows +
        '</div>' +
      '</div>';
  }

  function renderModelPerformance(rows) {
    if (!rows || !rows.length) { return '<div class="in-section"><h3>Model Performance</h3><p class="in-empty">No proposals with model identity recorded yet.</p></div>'; }
    var body = rows.map(function(r) {
      return '<tr><td>' + esc(r.model) + '</td><td>' + esc(r.provider || '—') + '</td><td>' + r.proposalCount + '</td>' +
        '<td>' + r.mergedCount + '</td><td>' + r.rejectedCount + '</td><td>' + pct(r.acceptanceRate) + '</td><td>' + conf(r.avgConfidence) + '</td></tr>';
    }).join('');
    return '<div class="in-section"><h3>Model Performance</h3><table class="in-table"><thead><tr>' +
      '<th>Model</th><th>Provider</th><th>Proposals</th><th>Merged</th><th>Rejected</th><th>Acceptance</th><th>Avg Confidence</th>' +
      '</tr></thead><tbody>' + body + '</tbody></table></div>';
  }

  function renderModelByStage(rows) {
    if (!rows || !rows.length) { return ''; }
    var body = rows.map(function(r) {
      return '<tr><td>' + esc(r.model) + '</td><td>' + esc(r.stage) + '</td><td>' + r.proposalCount + '</td>' +
        '<td>' + r.mergedCount + '</td><td>' + r.rejectedCount + '</td><td>' + pct(r.acceptanceRate) + '</td></tr>';
    }).join('');
    return '<div class="in-section"><h3>Models by Pipeline Stage</h3>' +
      '<p class="in-section-note">Stage resolved from the owning agent profile when a work unit was created via a strategy/template; rows without a resolvable profile are omitted.</p>' +
      '<table class="in-table"><thead><tr>' +
      '<th>Model</th><th>Stage</th><th>Proposals</th><th>Merged</th><th>Rejected</th><th>Acceptance</th>' +
      '</tr></thead><tbody>' + body + '</tbody></table></div>';
  }

  function renderForkWinRates(rows) {
    if (!rows || !rows.length) { return '<div class="in-section"><h3>Architecture / Fork Win Rates</h3><p class="in-empty">No experiment forks recorded yet.</p></div>'; }
    var body = rows.map(function(r) {
      return '<tr><td>' + esc(r.forkType) + '</td><td>' + r.totalForks + '</td><td>' + r.wins + '</td><td>' + r.losses + '</td><td>' + r.pending + '</td><td>' + pct(r.winRate) + '</td></tr>';
    }).join('');
    return '<div class="in-section"><h3>Architecture / Fork Win Rates</h3><table class="in-table"><thead><tr>' +
      '<th>Fork Type</th><th>Total</th><th>Wins</th><th>Losses</th><th>Pending</th><th>Win Rate</th>' +
      '</tr></thead><tbody>' + body + '</tbody></table></div>';
  }

  function renderForkConstraintWinRates(rows) {
    if (!rows || !rows.length) { return ''; }
    var body = rows.map(function(r) {
      return '<tr><td>' + esc(r.forkType) + '</td><td>' + esc(r.constraint) + '</td><td>' + r.totalForks + '</td>' +
        '<td>' + r.wins + '</td><td>' + r.losses + '</td><td>' + r.pending + '</td><td>' + pct(r.winRate) + '</td></tr>';
    }).join('');
    return '<div class="in-section"><h3>Workspace Intelligence — Architecture / Library / Product Choices</h3>' +
      '<table class="in-table"><thead><tr>' +
      '<th>Fork Type</th><th>Choice</th><th>Total</th><th>Wins</th><th>Losses</th><th>Pending</th><th>Win Rate</th>' +
      '</tr></thead><tbody>' + body + '</tbody></table></div>';
  }

  function renderFailureCauses(rows) {
    if (!rows || !rows.length) { return ''; }
    var body = rows.map(function(r) {
      return '<tr><td>' + esc(r.category) + '</td><td>' + r.totalCount + '</td><td>' + r.workUnitsAffected + '</td></tr>';
    }).join('');
    return '<div class="in-section"><h3>Failure Causes</h3><table class="in-table"><thead><tr>' +
      '<th>Category</th><th>Total Occurrences</th><th>Work Units Affected</th>' +
      '</tr></thead><tbody>' + body + '</tbody></table></div>';
  }

  function renderReviewOutcomes(rows) {
    if (!rows || !rows.length) { return '<div class="in-section"><h3>Review Outcomes</h3><p class="in-empty">No decisions recorded yet.</p></div>'; }
    var body = rows.map(function(r) {
      return '<tr><td>' + esc(r.outcome) + '</td><td>' + r.count + '</td><td>' + conf(r.avgConfidence) + '</td></tr>';
    }).join('');
    return '<div class="in-section"><h3>Review Outcomes</h3><table class="in-table"><thead><tr>' +
      '<th>Outcome</th><th>Count</th><th>Avg Confidence</th>' +
      '</tr></thead><tbody>' + body + '</tbody></table></div>';
  }

  function findingSourceBadgeLabel(source) { return source === 'LlmScan' ? 'LLM scan' : source === 'Imported' ? 'imported' : 'deterministic'; }
  function findingKindBadgeLabel(kind) { return kind === 'PromptImprovement' ? 'Prompt' : 'Knowledge'; }

  function renderFindings(findings, statusFilter) {
    var list = findings || [];
    if (!list.length) { return '<p class="in-empty">No findings yet.</p>'; }
    return list.map(function(f) {
      var stageBadge = f.kind === 'PromptImprovement' && f.targetStage
        ? '<span class="in-finding-badge">' + esc(f.targetStage) + '</span>'
        : '';
      // Open/Investigating are non-terminal — still actionable. Promoted/Dismissed are terminal:
      // ReviewAsync has no revert path, so showing Promote/Dismiss/Investigate there would imply
      // an undo capability that doesn't exist.
      var actionable = f.status === 'Open' || f.status === 'Investigating';
      var actions = actionable
        ? '<div class="in-finding-actions">' +
            '<button class="in-finding-promote">Promote</button>' +
            '<button class="in-finding-dismiss">Dismiss</button>' +
            '<button class="in-finding-investigate">Investigate</button>' +
          '</div>'
        : '';
      var metaParts = [];
      if (f.reviewedAt) { metaParts.push('Reviewed ' + new Date(f.reviewedAt).toLocaleString()); }
      if (f.reviewNotes) { metaParts.push('Notes: ' + f.reviewNotes); }
      if (f.promotedArtifactId) { metaParts.push('Artifact: ' + f.promotedArtifactId); }
      var meta = metaParts.length ? '<div class="in-finding-meta">' + esc(metaParts.join(' · ')) + '</div>' : '';
      var checkbox = statusFilter === 'Promoted'
        ? '<input type="checkbox" class="in-finding-select" data-finding-id="' + esc(f.findingId) + '">'
        : '';
      return '' +
        '<div class="in-finding-card" data-finding-id="' + esc(f.findingId) + '">' +
          '<div class="in-finding-card-head">' +
            checkbox +
            '<span class="in-finding-title">' + esc(f.title) + '</span>' +
            '<span class="in-finding-badge">' + esc(findingKindBadgeLabel(f.kind)) + '</span>' +
            stageBadge +
            '<span class="in-finding-badge">' + esc(findingSourceBadgeLabel(f.source)) + '</span>' +
          '</div>' +
          '<div class="in-finding-summary">' + esc(f.summary) + '</div>' +
          meta +
          actions +
        '</div>';
    }).join('');
  }

  function bindFindingActions() {
    document.querySelectorAll('.in-finding-card').forEach(function(card) {
      var findingId = card.getAttribute('data-finding-id');
      var promote = card.querySelector('.in-finding-promote');
      var dismiss = card.querySelector('.in-finding-dismiss');
      var investigate = card.querySelector('.in-finding-investigate');
      if (promote) { promote.addEventListener('click', function() {
        vscode.postMessage({ type: 'insightsReviewFinding', findingId: findingId, decision: 'Promoted' });
      }); }
      if (dismiss) { dismiss.addEventListener('click', function() {
        vscode.postMessage({ type: 'insightsReviewFinding', findingId: findingId, decision: 'Dismissed' });
      }); }
      if (investigate) { investigate.addEventListener('click', function() {
        vscode.postMessage({ type: 'insightsReviewFinding', findingId: findingId, decision: 'Investigating' });
      }); }
    });
  }

  window.addEventListener('message', function(event) {
    var msg = event.data;
    if (msg.type === 'insightsResult') {
      var period = msg.data.since ? ('since ' + new Date(msg.data.since).toLocaleDateString()) : 'all time';
      document.getElementById('in-generated-at').textContent = 'Generated ' + new Date(msg.data.generatedAt).toLocaleString() + ' — ' + period;
      document.getElementById('in-body').innerHTML =
        renderHighlights(msg.data) +
        renderOverview(msg.data) +
        renderModelPerformance(msg.data.modelPerformance) +
        renderModelByStage(msg.data.modelPerformanceByStage) +
        renderForkWinRates(msg.data.forkWinRates) +
        renderForkConstraintWinRates(msg.data.forkConstraintWinRates) +
        renderFailureCauses(msg.data.failureCauses) +
        renderReviewOutcomes(msg.data.reviewOutcomes);
      return;
    }
    if (msg.type === 'insightsError') {
      document.getElementById('in-body').innerHTML = '<p class="in-error">Analysis failed — ' + esc(msg.message) + '</p>';
      return;
    }
    if (msg.type === 'insightsProfiles') {
      state.profiles = msg.profiles || [];
      var sel = document.getElementById('in-llm-profile');
      sel.innerHTML = state.profiles.length
        ? state.profiles.map(function(p) { return '<option value="' + esc(p.id) + '">' + esc(p.label) + (p.model ? ' (' + esc(p.model) + ')' : '') + '</option>'; }).join('')
        : '<option value="">(no profile configured)</option>';
      return;
    }
    if (msg.type === 'insightsFindingsList') {
      state.lastFindings = msg.findings || [];
      var statusSel = document.getElementById('in-findings-status');
      if (msg.statusFilter && statusSel.value !== msg.statusFilter) { statusSel.value = msg.statusFilter; }
      document.getElementById('in-export-controls').style.display = statusSel.value === 'Promoted' ? '' : 'none';
      document.getElementById('in-findings-list').innerHTML = renderFindings(state.lastFindings, statusSel.value);
      bindFindingActions();
      document.getElementById('in-detect-btn').disabled = false;
      return;
    }
    if (msg.type === 'insightsLlmScanDone') {
      var btn = document.getElementById('in-llm-scan-btn');
      btn.disabled = false;
      btn.textContent = 'Run LLM Scan';
      return;
    }
  });
`;
