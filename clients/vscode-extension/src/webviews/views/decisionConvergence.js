// Extracted from src/panels/MergeReviewPanel.ts (DC_JS) by scripts/extract-view.mjs.
// Body is the exact runtime string of the former inline <script>, with the historical
// scopeViewScript() rewrite (getElementById -> $, querySelector(All) -> root.*) baked in.
// One deliberate change: local esc() didn't escape quotes yet was used inside HTML
// attributes; it now uses the shared escaper.

import { esc } from './lib/esc.js';

/** @param {{ root: HTMLElement, vscode: { postMessage(m: any): void }, $: (id: string) => HTMLElement | null }} ctx */
export function init(ctx) {
  var root = ctx.root;
  var vscode = ctx.vscode;
  var $ = ctx.$;

  // vscode supplied by ctx (was: acquireVsCodeApi())

  var dcSessionOverride = $('dc-session-override');
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

  // esc() imported from ./lib/esc.js (local copy didn't escape quotes, but is used in attribute contexts)

  function setText(id, val) {
    var el = $(id);
    if (el) el.textContent = val || '';
  }

  function setHtml(id, html) {
    var el = $(id);
    if (el) el.innerHTML = html;
  }

  function showIf(id, cond) {
    var el = $(id);
    if (el) el.classList.toggle('hidden', !cond);
  }

  function setDisabled(id, disabled) {
    var el = $(id);
    if (el) el.disabled = disabled;
  }

  $('btn-validate').addEventListener('click', function() {
    vscode.postMessage({ type: 'validateEvidence' });
  });
  function reviewNotesValue() {
    var el = $('review-notes');
    var v = el && el.value ? el.value.trim() : '';
    return v.length ? v : undefined;
  }
  $('btn-accept').addEventListener('click', function() {
    vscode.postMessage({ type: 'acceptDecision', notes: reviewNotesValue() });
  });
  $('btn-reject').addEventListener('click', function() {
    vscode.postMessage({ type: 'rejectDecision', notes: reviewNotesValue() });
  });
  $('btn-apply').addEventListener('click', function() {
    vscode.postMessage({ type: 'applyDecision' });
  });
  $('btn-fork').addEventListener('click', function() {
    vscode.postMessage({ type: 'forkHypothesis' });
  });
  $('btn-restore').addEventListener('click', function() {
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
      if (hasStderr) html += esc(b.stdErr) + '\n';
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
    var statusEl = $('status-' + id);
    var runBtn = $('btn-run-' + id);
    var stopBtn = $('btn-stop-' + id);
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
    var buildBtn = $('btn-build-' + id);
    var testBtn  = $('btn-test-' + id);
    var runBtn   = $('btn-run-' + id);
    var stopBtn  = $('btn-stop-' + id);
    if (buildBtn) buildBtn.disabled = busy || !caps.build;
    if (testBtn)  testBtn.disabled  = busy || !caps.test;
    if (runBtn)   runBtn.disabled   = busy || !caps.run;
    if (stopBtn)  stopBtn.disabled  = busy;
  }

  var rootRowsEl = $('root-rows');
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
      var statusKey = (c.status || '').toLowerCase().replace(/\s+/g, '');
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
    var inlineBtn = $('btn-mode-inline');
    var splitBtn = $('btn-mode-split');
    if (inlineBtn) inlineBtn.classList.toggle('active', mode === 'inline');
    if (splitBtn) splitBtn.classList.toggle('active', mode === 'split');
    setHtml('file-changes', renderFileChanges(window.__fileChanges || [], mode));
  }

  $('btn-mode-inline').addEventListener('click', function() {
    setDiffMode('inline');
    rerenderFileChanges();
  });
  $('btn-mode-split').addEventListener('click', function() {
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
      var pre = $(targetId);
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
      var sel = $('dc-session-override');
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
      var loadingEl2 = $('loading');
      var contentEl2 = $('content');
      if (loadingEl2) {
        loadingEl2.textContent = 'No pending decisions to review.';
        loadingEl2.style.opacity = '0.55';
      }
      if (contentEl2) { contentEl2.classList.add('hidden'); }
      return;
    }

    if (msg.type === 'loadError') {
      var loadingEl2 = $('loading');
      if (loadingEl2) {
        loadingEl2.textContent = 'Failed to load: ' + (msg.error || 'Unknown error');
        loadingEl2.style.opacity = '0.7';
        loadingEl2.style.color = 'var(--nm-error, #f14c4c)';
      }
      return;
    }

    if (msg.type === 'conflict') {
      var loadingEl3 = $('loading');
      var contentEl3 = $('content');
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
      var resultsEl = $('results-' + rootRowId(rootPath));
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
      var outputEl = $('run-output-' + rootRowId(typeof msg.rootPath === 'string' ? msg.rootPath : ''));
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
      var stopResultsEl = $('results-' + rootRowId(stopRootPath));
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
    var status = (p.status || '').toLowerCase().replace(/\s+/g, '');

    var loadingEl = $('loading');
    var contentEl = $('content');
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
    var evidenceEl = $('evidence-results');
    var execResultsEl = $('execution-results');
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
    var noChangesBannerEl = $('no-changes-banner');
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

    var reviewNotesEl = $('review-notes');
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

}
