import * as vscode from 'vscode';
import type { AgentConfigService } from '../AgentConfigService';
import { scopeViewCss } from './sharedWebviewChrome';

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

// Phase 3 (plans/organizational-knowledge-and-workgroup-scope.md) — a row of GET /studio/constraints:
// a constraint applicable to this peer, labeled with its reach + application scope and whether the
// local toggle currently has it enabled here.
interface ConstraintView {
  artifactId: string;
  title?: string | null;
  body?: string | null;
  reach?: string | null;
  repositoryId?: string | null;
  appliesToAllRepos: boolean;
  enabled: boolean;
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

  // View JS lives in src/webviews/views/insights.js (out/studio-views.js bundle) — no inline script.
  static getFragment(): { css: string; html: string } {
    return {
      css: scopeViewCss(IN_CSS, InsightsPanel.containerId),
      html: `<div id="${InsightsPanel.containerId}" class="nm-shell-pane">${IN_HTML}</div>`,
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
          const created = await this.post<Finding[]>('/studio/insights/detect-findings', {});
          await this.sendFindings();
          void vscode.window.showInformationMessage(
            created.length > 0
              ? `NodalMerge: detected ${created.length} finding(s) — see the Findings list.`
              : 'NodalMerge: no new findings. Deterministic detection needs more run history to spot patterns '
                + '(e.g. ≥5 decided experiment forks, ≥5 rejected proposals, or co-modification patterns). '
                + 'Keep running goals, or try Run LLM Scan with an API model profile.',
          );
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
      case 'insightsLoadConstraints': {
        await this.sendConstraints();
        return;
      }
      case 'insightsToggleConstraint': {
        try {
          await this.post(`/studio/constraints/${encodeURIComponent(msg.artifactId as string)}/toggle`, {
            disabled: msg.disabled as boolean,
          });
        } catch (err) {
          void vscode.window.showErrorMessage('NodalMerge: toggling constraint failed — ' + String(err));
          await this.sendConstraints(); // resync the UI on failure
        }
        return;
      }
      default:
        return;
    }
  }

  private async sendConstraints(): Promise<void> {
    try {
      const constraints = await this.get<ConstraintView[]>('/studio/constraints');
      void this.panel.webview.postMessage({ type: 'insightsConstraintsList', constraints });
    } catch (err) {
      void this.panel.webview.postMessage({ type: 'insightsConstraintsError', message: String(err) });
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
      // CLI profiles (claude-cli / codex-cli) resolve with an empty baseUrl — they map to a local
      // binary executor, not an HTTP endpoint. The insight LLM scan is an OpenAI-style HTTP call, so
      // it can't drive a CLI harness. Explain instead of letting the server 400 on "baseUrl required".
      if (!cfg.baseUrl || !cfg.baseUrl.trim()) {
        void vscode.window.showWarningMessage(
          'NodalMerge: the LLM scan needs an API model profile (OpenAI / Anthropic / vscode-lm). '
          + 'A CLI profile like claude-cli can\'t run it — use "Detect Findings" for free deterministic '
          + 'analysis, or add an API model profile in Model & Agent Studio.',
        );
        void this.panel.webview.postMessage({ type: 'insightsLlmScanDone' });
        return;
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
  .in-tabs { display: flex; gap: 2px; border-bottom: 1px solid var(--nm-border); }
  .in-tab { background: none; border: none; border-bottom: 2px solid transparent; padding: 6px 14px; cursor: pointer; opacity: 0.65; color: inherit; font-size: 0.9em; }
  .in-tab:hover { opacity: 0.9; }
  .in-tab.in-tab-active { border-bottom-color: currentColor; opacity: 1; font-weight: 600; }
  .in-pane { display: flex; flex-direction: column; gap: 14px; }
  .in-subheader { gap: 12px; }
  .in-constraint-group > h4 { font-size: 0.8em; margin: 8px 0 6px; opacity: 0.7; text-transform: uppercase; letter-spacing: 0.03em; }
  .in-constraint-card { display: flex; align-items: flex-start; gap: 10px; border: 1px solid var(--nm-border); border-radius: 4px; padding: 8px 12px; margin-bottom: 6px; }
  .in-constraint-card.in-constraint-off { opacity: 0.5; }
  .in-constraint-toggle { margin-top: 3px; }
  .in-constraint-main { flex: 1; min-width: 0; }
  .in-constraint-title { font-weight: 600; }
  .in-constraint-body { font-size: 0.85em; opacity: 0.85; margin-top: 3px; white-space: pre-wrap; }
  .in-constraint-badges { display: flex; gap: 6px; margin-top: 5px; flex-wrap: wrap; }
`;

const IN_HTML = `
  <div class="in-header">
    <h2>Insights</h2>
  </div>
  <div class="in-tabs">
    <button class="in-tab in-tab-active" data-tab="analysis">Analysis</button>
    <button class="in-tab" data-tab="constraints">Constraints</button>
  </div>

  <div id="in-pane-analysis" class="in-pane">
    <div class="in-header in-subheader">
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
  </div>

  <div id="in-pane-constraints" class="in-pane" style="display:none">
    <div class="in-section">
      <h3>Constraints applied to your agents</h3>
      <p class="in-section-note">Durable guidance folded into every agent's kickoff prompt. Uncheck one to turn it off for you only — it stays active for every other peer. Grouped by reach (who shares it) and application (which repositories it affects).</p>
      <div id="in-constraints-list"><p class="in-empty">Open this tab to load constraints.</p></div>
    </div>
  </div>
`;

