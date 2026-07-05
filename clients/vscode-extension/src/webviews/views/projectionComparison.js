// Extracted from src/panels/ProjectionComparisonPanel.ts (PC_JS) by scripts/extract-view.mjs.
// Body is the exact runtime string of the former inline <script>, with the historical
// scopeViewScript() rewrite (getElementById -> $, querySelector(All) -> root.*) baked in.

import { esc } from './lib/esc.js';

/** @param {{ root: HTMLElement, vscode: { postMessage(m: any): void }, $: (id: string) => HTMLElement | null }} ctx */
export function init(ctx) {
  var root = ctx.root;
  var vscode = ctx.vscode;
  var $ = ctx.$;

  // vscode supplied by ctx (was: acquireVsCodeApi())
  var state = { snapshots: [], staleness: {} };

  // esc() imported from ./lib/esc.js
  function shortId(id) { return id && id.length > 14 ? id.slice(0, 6) + '…' + id.slice(-6) : id; }

  function setStatus(text, isError) {
    var el = $('pc-status');
    el.textContent = text || '';
    el.className = isError ? 'pc-error' : 'pc-empty';
  }

  $('pc-capture-btn').addEventListener('click', function() {
    var workUnitId = $('pc-wuid-input').value.trim();
    if (!workUnitId) { setStatus('Enter a work unit ID first.', true); return; }
    setStatus('Capturing…');
    vscode.postMessage({ type: 'pcCapture', workUnitId: workUnitId });
  });

  $('pc-refresh-btn').addEventListener('click', function() {
    vscode.postMessage({ type: 'pcRefreshList' });
  });

  $('pc-compare-btn').addEventListener('click', function() {
    var a = $('pc-compare-a').value;
    var b = $('pc-compare-b').value;
    if (!a || !b) { setStatus('Select two snapshots to compare.', true); return; }
    if (a === b) { setStatus('Select two different snapshots.', true); return; }
    setStatus('Comparing…');
    vscode.postMessage({ type: 'pcCompare', snapshotIdA: a, snapshotIdB: b });
  });

  function staleBadge(snapshotId) {
    var s = state.staleness[snapshotId];
    if (!s) { return '<button class="pc-check-stale" data-id="' + esc(snapshotId) + '">Check Stale</button>'; }
    return s.isStale
      ? '<span class="pc-badge pc-badge-stale" title="' + esc(s.staleArtifactIds.join(', ')) + '">stale</span>'
      : '<span class="pc-badge pc-badge-fresh">fresh</span>';
  }

  function renderList() {
    var list = state.snapshots || [];
    var listEl = $('pc-list');
    if (!list.length) {
      listEl.innerHTML = '<p class="pc-empty">No snapshots captured yet.</p>';
    } else {
      var rows = list.map(function(s) {
        return '<tr><td>' + esc(shortId(s.snapshotId)) + '</td><td>' + esc(s.workUnitId) + '</td>' +
          '<td>' + new Date(s.createdAt).toLocaleString() + '</td>' +
          '<td data-stale-cell="' + esc(s.snapshotId) + '">' + staleBadge(s.snapshotId) + '</td></tr>';
      }).join('');
      listEl.innerHTML = '<table class="pc-table"><thead><tr><th>Snapshot</th><th>Work Unit</th><th>Captured</th><th>Staleness</th></tr></thead><tbody>' + rows + '</tbody></table>';
      listEl.querySelectorAll('.pc-check-stale').forEach(function(btn) {
        btn.addEventListener('click', function() {
          var id = btn.getAttribute('data-id');
          vscode.postMessage({ type: 'pcCheckStale', snapshotId: id });
        });
      });
    }

    var options = '<option value="">(select snapshot A)</option>' +
      list.map(function(s) { return '<option value="' + esc(s.snapshotId) + '">' + esc(shortId(s.snapshotId)) + ' — ' + esc(s.workUnitId) + '</option>'; }).join('');
    var optionsB = options.replace('(select snapshot A)', '(select snapshot B)');
    var selA = $('pc-compare-a');
    var selB = $('pc-compare-b');
    var prevA = selA.value, prevB = selB.value;
    selA.innerHTML = options;
    selB.innerHTML = optionsB;
    if (prevA) { selA.value = prevA; }
    if (prevB) { selB.value = prevB; }
  }

  function renderArtifactTable(rows) {
    if (!rows || !rows.length) { return '<p class="pc-empty">none</p>'; }
    var body = rows.map(function(a) {
      return '<tr><td>' + esc(a.title || a.artifactId) + '</td><td>' + esc(a.type) + '</td><td>' + esc(a.status) + '</td></tr>';
    }).join('');
    return '<table class="pc-table"><thead><tr><th>Artifact</th><th>Type</th><th>Status</th></tr></thead><tbody>' + body + '</tbody></table>';
  }

  function renderDivergenceTable(rows) {
    if (!rows || !rows.length) { return '<p class="pc-empty">none</p>'; }
    var body = rows.map(function(d) {
      return '<tr><td>' + esc(d.artifactId) + '</td><td>' + esc(d.statusA) + '</td><td>' + esc(d.statusB) + '</td></tr>';
    }).join('');
    return '<table class="pc-table"><thead><tr><th>Artifact</th><th>Status A</th><th>Status B</th></tr></thead><tbody>' + body + '</tbody></table>';
  }

  // ── Live invalidation updates ────────────────────────────────────────────

  function connectInvalidationSocket(wsUrl) {
    var ws;
    try { ws = new WebSocket(wsUrl); } catch (e) { return; }
    ws.onopen = function() {
      ws.send(JSON.stringify({ type: 'hello', room: 'studio-main', pubkey: 'studio-projection-snapshots', frontier: [] }));
    };
    ws.onmessage = function(e) {
      var msg;
      try { msg = JSON.parse(e.data); } catch (err) { return; }
      if (msg && msg.type === 'artifact-invalidated') {
        applyArtifactInvalidated(msg.workUnitId);
      }
    };
    ws.onclose = function() { setTimeout(function() { connectInvalidationSocket(wsUrl); }, 2000); };
    ws.onerror = function() { ws.close(); };
  }

  function applyArtifactInvalidated(workUnitId) {
    (state.snapshots || []).forEach(function(s) {
      if (s.workUnitId === workUnitId) {
        vscode.postMessage({ type: 'pcCheckStale', snapshotId: s.snapshotId });
      }
    });
  }

  window.addEventListener('message', function(event) {
    var msg = event.data;
    if (msg.type === 'pcWsInit') {
      connectInvalidationSocket(msg.wsUrl);
      return;
    }
    if (msg.type === 'pcSnapshotList') {
      state.snapshots = msg.snapshots || [];
      renderList();
      return;
    }
    if (msg.type === 'pcCaptured') {
      setStatus('Captured snapshot ' + shortId(msg.snapshot.snapshotId) + '.');
      return;
    }
    if (msg.type === 'pcStaleResult') {
      state.staleness[msg.snapshotId] = { isStale: msg.isStale, staleArtifactIds: msg.staleArtifactIds || [] };
      var cell = root.querySelector('[data-stale-cell="' + msg.snapshotId + '"]');
      if (cell) { cell.innerHTML = staleBadge(msg.snapshotId); }
      return;
    }
    if (msg.type === 'pcCompareResult') {
      var c = msg.comparison;
      $('pc-compare-results').innerHTML =
        '<div class="pc-compare-results">' +
          '<div><h4>Only in A</h4>' + renderArtifactTable(c.onlyInA) + '</div>' +
          '<div><h4>Only in B</h4>' + renderArtifactTable(c.onlyInB) + '</div>' +
          '<div><h4>Differing Status</h4>' + renderDivergenceTable(c.differingStatus) + '</div>' +
        '</div>';
      setStatus('');
      return;
    }
    if (msg.type === 'pcError') {
      setStatus(msg.message, true);
      return;
    }
    if (msg.type === 'pcKgsList') {
      renderKgsList(msg.states || []);
      return;
    }
    if (msg.type === 'pcKgsRestored') {
      var r = msg.result;
      setKgsStatus(r.succeeded
        ? 'Restored ' + r.fileCount + ' file(s) to ' + r.targetPath + ' (' + r.durationMs + 'ms).'
        : 'Restore failed: ' + (r.error || 'unknown error'), !r.succeeded);
      return;
    }
    if (msg.type === 'pcKgsDiffResult') {
      renderKgsDiff(msg.diff);
      return;
    }
    if (msg.type === 'pcKgsError') {
      setKgsStatus(msg.message, true);
      return;
    }
  });

  // ── Known Good States ────────────────────────────────────────────────────

  var kgsStates = [];

  function setKgsStatus(text, isError) {
    var el = $('pc-kgs-status');
    el.textContent = text || '';
    el.className = isError ? 'pc-error' : 'pc-empty';
  }

  $('pc-kgs-find-btn').addEventListener('click', function() {
    var branchId = $('pc-kgs-branch-input').value.trim();
    if (!branchId) { setKgsStatus('Enter a branch ID first.', true); return; }
    setKgsStatus('Loading…');
    vscode.postMessage({ type: 'pcFindKgs', branchId: branchId });
  });

  $('pc-kgs-diff-btn').addEventListener('click', function() {
    var a = $('pc-kgs-diff-a').value;
    var b = $('pc-kgs-diff-b').value;
    if (!a || !b) { setKgsStatus('Select two states to diff.', true); return; }
    if (a === b) { setKgsStatus('Select two different states.', true); return; }
    setKgsStatus('Diffing…');
    vscode.postMessage({ type: 'pcDiffKgs', stateIdA: a, stateIdB: b });
  });

  function renderKgsList(states) {
    kgsStates = states;
    var el = $('pc-kgs-list');
    var diffSection = $('pc-kgs-diff-section');
    if (!states || !states.length) {
      el.innerHTML = '<p class="pc-empty">No known good states for this branch.</p>';
      diffSection.style.display = 'none';
      setKgsStatus('');
      return;
    }
    setKgsStatus('');
    diffSection.style.display = '';
    var html = '';
    for (var i = 0; i < states.length; i++) {
      var s = states[i];
      html += '<div class="pc-kgs-row">';
      html += '<span class="pc-kgs-desc" title="' + esc(s.stateId) + '">' + esc(s.description) + '</span>';
      html += '<span class="pc-kgs-meta">' + new Date(s.createdAt).toLocaleString() + ' · ' + esc(s.createdBy) + '</span>';
      html += '<div class="pc-kgs-actions">';
      html += '<button class="ghost pc-kgs-restore" data-id="' + esc(s.stateId) + '" title="Write known-good files to the configured working tree">Restore</button>';
      html += '</div>';
      html += '</div>';
    }
    el.innerHTML = html;
    el.querySelectorAll('.pc-kgs-restore').forEach(function(btn) {
      btn.addEventListener('click', function() {
        setKgsStatus('Restoring…');
        vscode.postMessage({ type: 'pcRestoreKgs', stateId: btn.getAttribute('data-id') });
      });
    });

    var options = '<option value="">(select state A)</option>' +
      states.map(function(s) { return '<option value="' + esc(s.stateId) + '">' + esc(s.description) + ' (' + new Date(s.createdAt).toLocaleDateString() + ')</option>'; }).join('');
    var optionsB = options.replace('(select state A)', '(select state B)');
    $('pc-kgs-diff-a').innerHTML = options;
    $('pc-kgs-diff-b').innerHTML = optionsB;
  }

  var DIFF_STATUS_CLASS = { Added: 'pc-diff-added', Removed: 'pc-diff-removed', Modified: 'pc-diff-modified' };
  var DIFF_STATUS_ICON  = { Added: '+', Removed: '−', Modified: '~' };

  function renderKgsDiff(diff) {
    setKgsStatus('');
    var el = $('pc-kgs-diff-results');
    if (!diff || !diff.differences || !diff.differences.length) {
      el.innerHTML = '<p class="pc-empty">No file differences.</p>';
      return;
    }
    var summary = '<div class="pc-diff-summary">' +
      '<span class="pc-diff-added">+' + diff.addedCount + ' added</span>  ' +
      '<span class="pc-diff-removed">−' + diff.removedCount + ' removed</span>  ' +
      '<span class="pc-diff-modified">~' + diff.modifiedCount + ' modified</span>' +
      '</div>';
    var rows = diff.differences.map(function(d) {
      var cls = DIFF_STATUS_CLASS[d.status] || '';
      var icon = DIFF_STATUS_ICON[d.status] || '?';
      return '<div class="pc-diff-file ' + cls + '"><span>' + icon + '</span><span>' + esc(d.relativePath) + '</span></div>';
    }).join('');
    el.innerHTML = summary + rows;
  }

}
