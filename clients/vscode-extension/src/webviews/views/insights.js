// Extracted from src/panels/InsightsPanel.ts (IN_JS) by scripts/extract-view.mjs.
// Body is the exact runtime string of the former inline <script>, with the historical
// scopeViewScript() rewrite (getElementById -> $, querySelector(All) -> root.*) baked in.

import { esc } from './lib/esc.js';

/** @param {{ root: HTMLElement, vscode: { postMessage(m: any): void }, $: (id: string) => HTMLElement | null }} ctx */
export function init(ctx) {
  var root = ctx.root;
  var vscode = ctx.vscode;
  var $ = ctx.$;

  // vscode supplied by ctx (was: acquireVsCodeApi())
  var state = { profiles: [], lastFindings: [] };

  // esc() imported from ./lib/esc.js
  function pct(n) { return (n * 100).toFixed(0) + '%'; }
  function conf(n) { return (n === null || n === undefined) ? '—' : n.toFixed(2); }

  $('in-run-btn').addEventListener('click', function() {
    $('in-body').innerHTML = '<p class="in-empty">Running analysis&hellip;</p>';
    vscode.postMessage({ type: 'insightsRunAnalysis', period: $('in-period').value });
  });

  $('in-detect-btn').addEventListener('click', function() {
    this.disabled = true;
    vscode.postMessage({ type: 'insightsDetectFindings' });
  });

  $('in-llm-scan-btn').addEventListener('click', function() {
    var profileId = $('in-llm-profile').value;
    this.disabled = true;
    this.textContent = 'Scanning…';
    vscode.postMessage({ type: 'insightsRunLlmScan', profileId: profileId });
  });

  $('in-findings-status').addEventListener('change', function() {
    $('in-export-controls').style.display = this.value === 'Promoted' ? '' : 'none';
    vscode.postMessage({ type: 'insightsFindingsFilterChanged', status: this.value });
  });

  $('in-select-all-btn').addEventListener('click', function() {
    root.querySelectorAll('.in-finding-select').forEach(function(cb) { cb.checked = true; });
  });

  $('in-export-btn').addEventListener('click', function() {
    var ids = Array.prototype.map.call(
      root.querySelectorAll('.in-finding-select:checked'),
      function(cb) { return cb.getAttribute('data-finding-id'); }
    );
    var selected = (state.lastFindings || []).filter(function(f) { return ids.indexOf(f.findingId) !== -1; });
    var exportable = selected.map(function(f) {
      return { kind: f.kind, title: f.title, summary: f.summary, targetStage: f.targetStage || null };
    });
    vscode.postMessage({ type: 'insightsExportFindings', findings: exportable });
  });

  $('in-import-btn').addEventListener('click', function() {
    vscode.postMessage({ type: 'insightsImportFindings' });
  });

  // ── Tabs (Analysis | Constraints) ──────────────────────────────────────────
  root.querySelectorAll('.in-tab').forEach(function(tab) {
    tab.addEventListener('click', function() {
      var name = tab.getAttribute('data-tab');
      root.querySelectorAll('.in-tab').forEach(function(t) { t.classList.toggle('in-tab-active', t === tab); });
      $('in-pane-analysis').style.display = name === 'analysis' ? '' : 'none';
      $('in-pane-constraints').style.display = name === 'constraints' ? '' : 'none';
      if (name === 'constraints') {
        $('in-constraints-list').innerHTML = '<p class="in-empty">Loading&hellip;</p>';
        $('in-proposed-list').innerHTML = '<p class="in-empty">Loading&hellip;</p>';
        vscode.postMessage({ type: 'insightsLoadConstraints' });
        vscode.postMessage({ type: 'insightsLoadProposedConstraints' });
      }
    });
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

  // ── Constraints tab ────────────────────────────────────────────────────────
  var addConstraintBtn = $('in-nc-add');
  if (addConstraintBtn) {
    addConstraintBtn.addEventListener('click', function() {
      var title = ($('in-nc-title').value || '').trim();
      var body = ($('in-nc-body').value || '').trim();
      if (!title || !body) { return; }
      var reachEl = root.querySelector('input[name="in-nc-reach"]:checked');
      vscode.postMessage({
        type: 'insightsAddConstraint',
        title: title,
        body: body,
        reach: reachEl ? reachEl.value : 'Workgroup',
        repoSpecific: $('in-nc-repo').checked,
      });
      $('in-nc-title').value = '';
      $('in-nc-body').value = '';
      $('in-nc-repo').checked = false;
    });
  }

  function constraintGroupLabel(c) {
    var reach = c.reach || 'Legacy';
    if (reach === 'Workgroup') { return c.appliesToAllRepos ? 'Workgroup · all repositories' : 'Workgroup · this repository'; }
    if (reach === 'Private') { return 'Private · local to you'; }
    return c.appliesToAllRepos ? 'Legacy · all repositories' : 'Legacy · this repository';
  }

  function renderConstraints(list) {
    if (!list || !list.length) {
      return '<p class="in-empty">No constraints yet. Promote a finding on the Analysis tab to create one.</p>';
    }
    var groups = {};
    var order = [];
    list.forEach(function(c) {
      var label = constraintGroupLabel(c);
      if (!groups[label]) { groups[label] = []; order.push(label); }
      groups[label].push(c);
    });
    return order.map(function(label) {
      var cards = groups[label].map(function(c) {
        var offClass = c.enabled ? '' : ' in-constraint-off';
        var badges =
          '<span class="in-finding-badge">' + esc(c.reach || 'legacy') + '</span>' +
          '<span class="in-finding-badge">' + (c.appliesToAllRepos ? 'all repos' : 'this repo') + '</span>' +
          (c.enabled ? '' : '<span class="in-finding-badge">off</span>');
        return '' +
          '<div class="in-constraint-card' + offClass + '" data-constraint-id="' + esc(c.artifactId) + '">' +
            '<input type="checkbox" class="in-constraint-toggle" data-constraint-id="' + esc(c.artifactId) + '"' + (c.enabled ? ' checked' : '') + '>' +
            '<div class="in-constraint-main">' +
              '<div class="in-constraint-title">' + esc(c.title || c.artifactId) + '</div>' +
              (c.body ? '<div class="in-constraint-body">' + esc(c.body) + '</div>' : '') +
              '<div class="in-constraint-badges">' + badges + '</div>' +
            '</div>' +
          '</div>';
      }).join('');
      return '<div class="in-constraint-group"><h4>' + esc(label) + '</h4>' + cards + '</div>';
    }).join('');
  }

  // ── Proposed (lineage) constraints — promote to global ──────────────────────
  function renderProposed(list) {
    if (!list || !list.length) {
      return '<p class="in-empty">No proposed constraints. Domain observers and agents record these against a work unit when they spot a gap — enable observers or run goals to see them here.</p>';
    }
    return list.map(function(c) {
      var scopeBadge = '<span class="in-finding-badge">' + (c.appliesToAllRepos ? 'all repos' : 'this repo') + '</span>';
      var action = c.promoted
        ? '<span class="in-finding-badge">promoted</span>'
        : '<div class="in-proposed-actions"><button class="in-proposed-promote" data-proposed-id="' + esc(c.artifactId) + '">Promote</button></div>';
      var wu = c.workUnitId ? '<span class="in-finding-badge">' + esc(c.workUnitId) + '</span>' : '';
      return '' +
        '<div class="in-proposed-card" data-proposed-id="' + esc(c.artifactId) + '">' +
          '<div class="in-proposed-main">' +
            '<div class="in-proposed-title">' + esc(c.title || c.artifactId) + '</div>' +
            (c.body ? '<div class="in-proposed-body">' + esc(c.body) + '</div>' : '') +
            '<div class="in-proposed-badges">' + scopeBadge + wu + '</div>' +
          '</div>' +
          action +
        '</div>';
    }).join('');
  }

  function bindProposedActions() {
    root.querySelectorAll('.in-proposed-promote').forEach(function(btn) {
      btn.addEventListener('click', function() {
        btn.disabled = true;
        btn.textContent = 'Promoting…';
        vscode.postMessage({ type: 'insightsPromoteConstraint', artifactId: btn.getAttribute('data-proposed-id') });
      });
    });
  }

  function bindConstraintToggles() {
    root.querySelectorAll('.in-constraint-toggle').forEach(function(cb) {
      cb.addEventListener('change', function() {
        var id = cb.getAttribute('data-constraint-id');
        var disabled = !cb.checked;
        var card = cb.closest('.in-constraint-card');
        if (card) { card.classList.toggle('in-constraint-off', disabled); }
        vscode.postMessage({ type: 'insightsToggleConstraint', artifactId: id, disabled: disabled });
      });
    });
  }

  function bindFindingActions() {
    root.querySelectorAll('.in-finding-card').forEach(function(card) {
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
      $('in-generated-at').textContent = 'Generated ' + new Date(msg.data.generatedAt).toLocaleString() + ' — ' + period;
      $('in-body').innerHTML =
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
      $('in-body').innerHTML = '<p class="in-error">Analysis failed — ' + esc(msg.message) + '</p>';
      return;
    }
    if (msg.type === 'insightsProfiles') {
      state.profiles = msg.profiles || [];
      var sel = $('in-llm-profile');
      sel.innerHTML = state.profiles.length
        ? state.profiles.map(function(p) { return '<option value="' + esc(p.id) + '">' + esc(p.label) + (p.model ? ' (' + esc(p.model) + ')' : '') + '</option>'; }).join('')
        : '<option value="">(no profile configured)</option>';
      return;
    }
    if (msg.type === 'insightsFindingsList') {
      state.lastFindings = msg.findings || [];
      var statusSel = $('in-findings-status');
      if (msg.statusFilter && statusSel.value !== msg.statusFilter) { statusSel.value = msg.statusFilter; }
      $('in-export-controls').style.display = statusSel.value === 'Promoted' ? '' : 'none';
      $('in-findings-list').innerHTML = renderFindings(state.lastFindings, statusSel.value);
      bindFindingActions();
      $('in-detect-btn').disabled = false;
      return;
    }
    if (msg.type === 'insightsLlmScanDone') {
      var btn = $('in-llm-scan-btn');
      btn.disabled = false;
      btn.textContent = 'Run LLM Scan';
      return;
    }
    if (msg.type === 'insightsConstraintsList') {
      $('in-constraints-list').innerHTML = renderConstraints(msg.constraints || []);
      bindConstraintToggles();
      return;
    }
    if (msg.type === 'insightsConstraintsError') {
      $('in-constraints-list').innerHTML = '<p class="in-error">Could not load constraints — ' + esc(msg.message) + '</p>';
      return;
    }
    if (msg.type === 'insightsProposedList') {
      $('in-proposed-list').innerHTML = renderProposed(msg.proposed || []);
      bindProposedActions();
      return;
    }
    if (msg.type === 'insightsProposedError') {
      $('in-proposed-list').innerHTML = '<p class="in-error">Could not load proposed constraints — ' + esc(msg.message) + '</p>';
      return;
    }
  });

}
