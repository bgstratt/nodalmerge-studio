// Extracted from src/panels/WorkspaceDashboardPanel.ts (ET_JS) by scripts/extract-view.mjs.
// Body is the exact runtime string of the former inline <script>, with the historical
// scopeViewScript() rewrite (getElementById -> $, querySelector(All) -> root.*) baked in.
// One deliberate change from the inline original: the local esc() had lost its HTML
// entities (a no-op replace chain), leaving markup injection open; it now uses the
// shared escaper.

import { esc } from './lib/esc.js';

/** @param {{ root: HTMLElement, vscode: { postMessage(m: any): void }, $: (id: string) => HTMLElement | null }} ctx */
export function init(ctx) {
  var root = ctx.root;
  var vscode = ctx.vscode;
  var $ = ctx.$;

  // vscode supplied by ctx (was: acquireVsCodeApi())
  var globalUsePromotionBranch = false;
  var globalCandidateBranchId = 'candidate';

  $('btn-new-goal').addEventListener('click', function() {
    vscode.postMessage({ type: 'createWorkUnit' });
  });
  $('btn-start-agent').addEventListener('click', function() {
    vscode.postMessage({ type: 'spawnAgent' });
  });
  $('btn-resume-all').addEventListener('click', function() {
    vscode.postMessage({ type: 'resumeAllWorkers' });
  });
  $('btn-stop-all').addEventListener('click', function() {
    vscode.postMessage({ type: 'stopAll' });
  });

  var etSessionOverride = $('et-session-override');
  if (etSessionOverride) {
    etSessionOverride.addEventListener('change', function() {
      vscode.postMessage({ type: 'sessionOverrideChanged', panelId: 'shell-pane-execution-timeline', sessionId: etSessionOverride.value || undefined });
    });
  }

  // esc() imported from ./lib/esc.js (local copy had broken entity replacements)

  function badge(status) {
    var s = (status || '').toLowerCase();
    return '<span class="badge ' + s + '">' + esc(status || '—') + '</span>';
  }

  // isGoalStore=true when items come from /studio/goals (have goalId + Paused status);
  // false when falling back to /studio/workunits (no goalId, no goal-level pause support).
  function renderGoalCard(g, isGoalStore, guardrailByWorkUnit) {
      var goalId = g.goalId || g.workUnitId;
      var gr = guardrailByWorkUnit[g.workUnitId];
      var status = (g.status || '').toLowerCase();
      var isPaused   = status === 'paused';
      var isReviewing = status === 'reviewing';
      var isTerminal  = ['cancelled', 'completed', 'merged', 'abandoned', 'converged'].indexOf(status) !== -1;
      var html = '';
      html += '<div class="card">';
      html += '<div class="row">';
      html += '<span class="title" title="' + esc(g.goal) + '">' + esc(g.goal) + '</span>';
      html += badge(g.status);
      html += '<div class="actions">';
      if (isGoalStore && isPaused) {
        html += '<button class="ghost" data-action="resumeGoal" data-gid="' + esc(goalId) + '" style="color:var(--nm-success);border-color:var(--nm-success)">▶ Resume</button>';
      } else if (isGoalStore && !isTerminal) {
        html += '<button class="ghost" data-action="pauseGoal" data-gid="' + esc(goalId) + '" style="color:var(--nm-warn);border-color:var(--nm-warn)">⏸ Pause</button>';
      }
      if (isReviewing) {
        html += '<button class="ghost" data-action="openConflictReview" data-wu="' + esc(g.workUnitId) + '">View Conflict →</button>';
      }
      if (!isTerminal && !isPaused) {
        html += '<button class="ghost" data-action="spawnAgent" data-wu="' + esc(g.workUnitId) + '">Spawn</button>';
      }
      html += '<button class="ghost" data-action="markKnownGood" data-wu="' + esc(g.workUnitId) + '" data-branch="' + esc(g.branchId) + '" title="Tag this work unit\'s current branch as a Known Good State">Tag KGS</button>';
      if (!isTerminal) {
        html += '<button class="danger" data-action="cancelWorkUnit" data-wu="' + esc(g.workUnitId) + '">Stop</button>';
      }
      html += '</div>';
      html += '</div>';
      if (isPaused && g.pauseReason) {
        html += '<div class="row"><span class="mono" style="color:var(--nm-warn)">⏸ ' + esc(g.pauseReason) + '</span></div>';
      }
      if (gr && (gr.tokensExceeded || gr.durationExceeded)) {
        var grParts = [];
        if (gr.tokensExceeded) { grParts.push(gr.totalTokens.toLocaleString() + ' tokens (cap ' + gr.maxGoalTokens.toLocaleString() + ')'); }
        if (gr.durationExceeded) { grParts.push(Math.round(gr.elapsedMinutes) + ' min (cap ' + gr.maxGoalDurationMinutes + ')'); }
        html += '<div class="row"><span class="mono" style="color:var(--nm-warn)" title="Alert only — this goal keeps running; guardrails never auto-stop work">⚠ Guardrail exceeded: ' + esc(grParts.join(', ')) + '</span></div>';
      }
      html += '<div class="row">';
      html += '<span class="mono">' + esc(g.workUnitId) + '</span>';
      html += '<span class="mono">fork: ' + esc(g.branchId) + '</span>';
      if (g.owner) { html += '<span class="mono">owner: ' + esc(g.owner) + '</span>'; }
      if (g.taskReviewPolicy && g.taskReviewPolicy !== 'HumanRequired') {
        var trp = g.taskReviewPolicy === 'AgentApproval' ? '🤖 Agent Approval' : '⏱ Hybrid';
        html += '<span class="badge reviewing" title="Task Review">Task: ' + trp + '</span>';
      }
      if (g.workspaceReviewPolicy && g.workspaceReviewPolicy !== 'HumanRequired') {
        var wrp = g.workspaceReviewPolicy === 'AgentApproval' ? '🤖 Agent Approval' : '⏱ Hybrid';
        html += '<span class="badge reviewing" title="Workspace Review">Workspace: ' + wrp + '</span>';
      }
      if (globalUsePromotionBranch) {
        html += '<span class="badge" title="Applies land on ' + esc(globalCandidateBranchId) + '; promote to main manually">→ ' + esc(globalCandidateBranchId) + '</span>';
      }
      html += '</div>';
      html += '</div>';
      return html;
  }

  function wireGoalCardActions(el) {
    el.querySelectorAll('[data-action="pauseGoal"]').forEach(function(btn) {
      btn.addEventListener('click', function() {
        vscode.postMessage({ type: 'dashboardGoalPause', goalId: btn.getAttribute('data-gid') });
      });
    });
    el.querySelectorAll('[data-action="resumeGoal"]').forEach(function(btn) {
      btn.addEventListener('click', function() {
        vscode.postMessage({ type: 'dashboardGoalResume', goalId: btn.getAttribute('data-gid') });
      });
    });
    el.querySelectorAll('[data-action="spawnAgent"]').forEach(function(btn) {
      btn.addEventListener('click', function() {
        vscode.postMessage({ type: 'spawnAgent', workUnitId: btn.getAttribute('data-wu') });
      });
    });
    el.querySelectorAll('[data-action="openConflictReview"]').forEach(function(btn) {
      btn.addEventListener('click', function() {
        vscode.postMessage({ type: 'openConflictReview', workUnitId: btn.getAttribute('data-wu') });
      });
    });
    el.querySelectorAll('[data-action="cancelWorkUnit"]').forEach(function(btn) {
      btn.addEventListener('click', function() {
        vscode.postMessage({ type: 'cancelWorkUnit', workUnitId: btn.getAttribute('data-wu') });
      });
    });
    el.querySelectorAll('[data-action="markKnownGood"]').forEach(function(btn) {
      btn.addEventListener('click', function() {
        vscode.postMessage({ type: 'markKnownGood', workUnitId: btn.getAttribute('data-wu'), branchId: btn.getAttribute('data-branch') });
      });
    });
  }

  // isGoalStore=true when items come from /studio/goals (have goalId + Paused status);
  // false when falling back to /studio/workunits (no goalId, no goal-level pause support).
  // Terminal goals (cancelled/completed/merged/abandoned/converged) are split into a
  // collapsed "Completed Goals" section instead of piling up forever in Active Goals.
  function renderActiveGoals(goals, isGoalStore, guardrailStatuses) {
    var el = $('active-goals');
    var completedSection = $('completed-goals-section');
    var completedEl = $('completed-goals');
    var completedSummary = $('completed-goals-summary');

    if (!goals || !goals.length) {
      el.innerHTML = '<p class="empty">No active goals.</p>';
      if (completedSection) { completedSection.style.display = 'none'; }
      return;
    }

    // Phase 2 item 1 — alert-only guardrail badge. Keyed by workUnitId since that's the one ID
    // both the goal-store and work-unit-fallback shapes always carry (goalId only exists in the
    // former). Never disables or hides anything else on the card — this is purely informational.
    var guardrailByWorkUnit = {};
    for (var gi = 0; gi < (guardrailStatuses || []).length; gi++) {
      guardrailByWorkUnit[guardrailStatuses[gi].workUnitId] = guardrailStatuses[gi];
    }

    var active = [];
    var completed = [];
    for (var i = 0; i < goals.length; i++) {
      var status = (goals[i].status || '').toLowerCase();
      var isTerminal = ['cancelled', 'completed', 'merged', 'abandoned', 'converged'].indexOf(status) !== -1;
      (isTerminal ? completed : active).push(goals[i]);
    }

    if (!active.length) {
      el.innerHTML = '<p class="empty">No active goals.</p>';
    } else {
      var activeHtml = '';
      for (var ai = 0; ai < active.length; ai++) {
        activeHtml += renderGoalCard(active[ai], isGoalStore, guardrailByWorkUnit);
      }
      el.innerHTML = activeHtml;
      wireGoalCardActions(el);
    }

    if (completedSection && completedEl) {
      if (!completed.length) {
        completedSection.style.display = 'none';
        completedEl.innerHTML = '';
      } else {
        completedSection.style.display = '';
        if (completedSummary) { completedSummary.textContent = 'Completed Goals (' + completed.length + ')'; }
        completed.sort(function(a, b) { return Date.parse(b.updatedAt || 0) - Date.parse(a.updatedAt || 0); });
        var capped = completed.slice(0, 20);
        var completedHtml = '';
        for (var ci = 0; ci < capped.length; ci++) {
          completedHtml += renderGoalCard(capped[ci], isGoalStore, guardrailByWorkUnit);
        }
        completedEl.innerHTML = completedHtml;
        wireGoalCardActions(completedEl);
      }
    }
  }

  function renderAgents(agents, goals) {
    var el = $('agents');
    if (!agents || !agents.length) {
      el.innerHTML = '<p class="empty">No running agents.</p>';
      return;
    }
    var goalMap = {};
    for (var j = 0; j < (goals || []).length; j++) { goalMap[goals[j].workUnitId] = goals[j]; }
    var html = '';
    for (var i = 0; i < agents.length; i++) {
      var a = agents[i];
      var wu = goalMap[a.workUnitId];
      var statusLower = (a.status || '').toLowerCase();
      var isPaused = statusLower === 'paused';
      var isInterrupted = statusLower === 'interrupted';
      html += '<div class="card">';
      html += '<div class="row">';
      html += '<span class="title mono">' + esc(a.agentId) + '</span>';
      html += badge(a.status);
      html += '<div class="actions">';
      // Phase 11 — deep-links into Goal Workspace's Decision Lens Conversation tab; the
      // transcript is durable, so this is offered regardless of pause/interrupted/active state.
      html += '<button class="ghost" data-action="viewTranscript" data-wu="' + esc(a.workUnitId) + '">View live transcript</button>';
      if (isInterrupted) {
        html += '<button class="ghost" data-action="resumeInterrupted" data-wu="' + esc(a.workUnitId) + '">↺ Resume</button>';
      } else if (isPaused) {
        html += '<button class="ghost" data-action="resumeAgent" data-id="' + esc(a.agentId) + '">Resume</button>';
        html += '<button class="danger" data-action="stopAgent" data-id="' + esc(a.agentId) + '">Stop</button>';
      } else {
        html += '<button class="ghost" data-action="pauseAgent" data-id="' + esc(a.agentId) + '">Pause</button>';
        html += '<button class="danger" data-action="stopAgent" data-id="' + esc(a.agentId) + '">Stop</button>';
      }
      html += '</div>';
      html += '</div>';
      if (statusLower === 'active' && a.currentActivity) {
        html += '<div class="row"><span class="pulse"></span><span class="mono">' + esc(a.currentActivity) + '</span></div>';
      }
      if (wu) {
        html += '<div class="row"><span class="mono">' + esc(wu.goal) + '</span></div>';
      }
      html += '</div>';
    }
    el.innerHTML = html;
    el.querySelectorAll('[data-action="resumeInterrupted"]').forEach(function(btn) {
      btn.addEventListener('click', function() {
        vscode.postMessage({ type: 'spawnAgent', workUnitId: btn.getAttribute('data-wu') });
      });
    });
    el.querySelectorAll('[data-action="viewTranscript"]').forEach(function(btn) {
      btn.addEventListener('click', function() {
        vscode.postMessage({ type: 'activityViewTranscript', workUnitId: btn.getAttribute('data-wu') });
      });
    });
    el.querySelectorAll('[data-action="pauseAgent"],[data-action="resumeAgent"],[data-action="stopAgent"]').forEach(function(btn) {
      btn.addEventListener('click', function() {
        vscode.postMessage({ type: btn.getAttribute('data-action'), agentId: btn.getAttribute('data-id') });
      });
    });
  }

  // Phase 8c — worker-level scheduler items a Host restart interrupted mid-execution. Mirrors
  // the orchestrator-level "Interrupted" card above: no silent auto-resume, a human must click
  // Resume (or Resume All for a busy fan-out with many interrupted children).
  function renderAwaitingResume(items) {
    var el = $('awaiting-resume');
    var resumeAllBtn = $('btn-resume-all');
    if (!items || !items.length) {
      el.innerHTML = '<p class="empty">Nothing awaiting resume.</p>';
      resumeAllBtn.style.display = 'none';
      return;
    }
    resumeAllBtn.style.display = '';
    var html = '';
    for (var i = 0; i < items.length; i++) {
      var it = items[i];
      html += '<div class="card">';
      html += '<div class="row">';
      html += '<span class="title mono">' + esc(it.workUnitId) + '</span>';
      html += '<span class="badge">' + esc(it.profileId) + '</span>';
      html += '<div class="actions">';
      html += '<button class="ghost" data-action="resumeWorker" data-wu="' + esc(it.workUnitId) + '">↺ Resume</button>';
      html += '</div>';
      html += '</div>';
      html += '</div>';
    }
    el.innerHTML = html;
    el.querySelectorAll('[data-action="resumeWorker"]').forEach(function(btn) {
      btn.addEventListener('click', function() {
        vscode.postMessage({ type: 'resumeWorker', workUnitId: btn.getAttribute('data-wu') });
      });
    });
  }

  function relTime(iso) {
    var ms = Date.now() - Date.parse(iso);
    if (!isFinite(ms)) { return 'unknown age'; }
    var mins = Math.floor(ms / 60000);
    if (mins < 1) { return 'just now'; }
    if (mins < 60) { return mins + 'm ago'; }
    var hrs = Math.floor(mins / 60);
    if (hrs < 24) { return hrs + 'h ago'; }
    var days = Math.floor(hrs / 24);
    return days + 'd ago';
  }

  function renderClarifications(items, metrics) {
    var el = $('clarifications');
    if (!items || !items.length) {
      el.innerHTML = '<p class="empty">No active clarification requests.</p>';
    } else {
      // Group by goal label, preserving insertion order.
      var groupOrder = [];
      var groups = {};
      for (var i = 0; i < items.length; i++) {
        var c = items[i];
        var key = c.goal || c.workUnitId;
        if (!groups[key]) { groups[key] = []; groupOrder.push(key); }
        groups[key].push(c);
      }
      var html = '';
      for (var gi = 0; gi < groupOrder.length; gi++) {
        var groupKey = groupOrder[gi];
        var groupItems = groups[groupKey];
        html += '<div style="margin-bottom:10px">';
        html += '<div style="font-size:0.75em;opacity:0.55;text-transform:uppercase;letter-spacing:0.06em;margin-bottom:4px;padding:0 2px">' + esc(groupKey) + '</div>';
        for (var j = 0; j < groupItems.length; j++) {
          var c = groupItems[j];
          var statusBadge = c.awaitingResume ? 'awaiting' : c.status;
          html += '<div class="card">';
          html += '<div class="row">';
          html += '<span class="title mono" title="' + esc(c.requestId) + '">' + esc(c.question) + '</span>';
          html += '<span class="badge ' + (c.awaitingResume ? 'paused' : '') + '">' + esc(statusBadge) + '</span>';
          html += '<div class="actions">';
          html += '<button class="ghost" data-action="respondClarification" data-rid="' + esc(c.requestId) + '" data-wu="' + esc(c.workUnitId) + '" style="color:var(--nm-success);border-color:var(--nm-success)">Respond</button>';
          html += '</div>';
          html += '</div>';
          if (c.context) {
            html += '<div class="row"><span class="mono" style="opacity:0.7">context: ' + esc(c.context) + '</span></div>';
          }
          if (c.options && c.options.length) {
            html += '<div class="row"><span class="mono">options: ' + esc(c.options.join(' | ')) + '</span></div>';
          }
          if (c.timeoutAt) {
            var now = Date.now();
            var timeoutMs = new Date(c.timeoutAt).getTime() - now;
            var timeoutLabel = timeoutMs > 0
              ? 'auto-' + esc(c.timeoutBehavior || 'continue') + ' in ' + Math.ceil(timeoutMs / 1000) + 's'
              : 'timed out (auto-' + esc(c.timeoutBehavior || 'continue') + ')';
            html += '<div class="row"><span class="mono" style="color:var(--nm-warn)">⏱ ' + timeoutLabel + '</span></div>';
          }
          html += '<div class="row">';
          html += '<span class="mono">age: ' + esc(relTime(c.requestedAt)) + '</span>';
          html += '<span class="mono">blocking: ' + esc(String(!!c.blocking)) + '</span>';
          if (c.sessionId) { html += '<span class="mono">session: ' + esc(c.sessionId) + '</span>'; }
          html += '</div>';
          html += '</div>';
        }
        html += '</div>';
      }
      el.innerHTML = html;
      el.querySelectorAll('[data-action="respondClarification"]').forEach(function(btn) {
        btn.addEventListener('click', function() {
          var rid = btn.getAttribute('data-rid');
          var wu = btn.getAttribute('data-wu');
          var found = null;
          for (var j = 0; j < items.length; j++) {
            if (String(items[j].requestId) === String(rid)) { found = items[j]; break; }
          }
          vscode.postMessage({ type: 'respondClarification', requestId: rid, workUnitId: wu, options: found ? found.options : [] });
        });
      });
    }

    var met = $('clarification-metrics');
    if (!metrics) {
      met.innerHTML = '<p class="empty">No clarification metrics yet.</p>';
      return;
    }
    var top = (metrics.perGoal || []).slice(0, 5).map(function(g) {
      return '<span class="mono">' + esc(g.goal) + ': ' + esc(String(g.requests)) + ' req / ' + esc(String(g.answered)) + ' answered / ' + esc(String(g.abandoned)) + ' abandoned</span>';
    }).join('<br/>');
    met.innerHTML =
      '<div class="card">' +
      '<div class="row"><span class="mono">requests: ' + esc(String(metrics.requests || 0)) + '</span>' +
      '<span class="mono">answered: ' + esc(String(metrics.answered || 0)) + '</span>' +
      '<span class="mono">abandoned: ' + esc(String(metrics.abandoned || 0)) + '</span></div>' +
      (top ? '<div class="row">' + top + '</div>' : '<div class="row"><span class="empty">No per-goal data.</span></div>') +
      '</div>';
  }

  var DECISION_STATUS_COLOR = {
    draft:          '',
    readyforreview: 'active',
    approved:       'active',
    rejected:       'failed',
    merged:         'stopped',
  };

  var RESOLVED_DECISION_STATUSES = ['merged', 'rejected', 'superseded'];

  function renderDecisionCard(m, statusKey) {
    var badgeClass = 'badge ' + (DECISION_STATUS_COLOR[statusKey] || '');
    var canReview = statusKey === 'readyforreview' || statusKey === 'approved' || statusKey === 'draft' || statusKey === 'proposed' || statusKey === 'executing' || statusKey === 'merge';
    var html = '';
    html += '<div class="card">';
    html += '<div class="row">';
    html += '<span class="title" title="' + esc(m.goal) + '">' + esc(m.goal) + '</span>';
    html += '<span class="' + badgeClass + '">' + esc(m.status) + '</span>';
    if (canReview) {
      html += '<div class="actions">';
      html += '<button class="ghost" data-action="openMergeReview" data-pid="' + esc(m.proposalId) + '">Review Decision →</button>';
      html += '</div>';
    }
    html += '</div>';
    html += '<div class="row">';
    html += '<span class="mono">' + esc(m.sourceBranch) + ' → ' + esc(m.targetBranch) + '</span>';
    html += '</div>';
    html += '</div>';
    return html;
  }

  function wireDecisionCardActions(el) {
    el.querySelectorAll('[data-action="openMergeReview"]').forEach(function(btn) {
      btn.addEventListener('click', function() {
        vscode.postMessage({ type: 'openMergeReview', proposalId: btn.getAttribute('data-pid') });
      });
    });
  }

  // Resolved decisions (merged/rejected/superseded) move into a collapsed "Resolved Decisions"
  // section instead of piling up forever in the live Pending Decisions list.
  function renderPendingDecisions(merges) {
    var el = $('decisions');
    var resolvedSection = $('resolved-decisions-section');
    var resolvedEl = $('resolved-decisions');
    var resolvedSummary = $('resolved-decisions-summary');

    if (!merges || !merges.length) {
      el.innerHTML = '<p class="empty">No pending decisions.</p>';
      if (resolvedSection) { resolvedSection.style.display = 'none'; }
      return;
    }

    var pending = [];
    var resolved = [];
    for (var i = 0; i < merges.length; i++) {
      var m = merges[i];
      var statusKey = (m.status || '').toLowerCase().replace(/\s+/g, '');
      (RESOLVED_DECISION_STATUSES.indexOf(statusKey) !== -1
        ? resolved
        : pending).push({ item: m, statusKey: statusKey });
    }

    if (!pending.length) {
      el.innerHTML = '<p class="empty">No pending decisions.</p>';
    } else {
      var pendingHtml = '';
      for (var pi = 0; pi < pending.length; pi++) {
        pendingHtml += renderDecisionCard(pending[pi].item, pending[pi].statusKey);
      }
      el.innerHTML = pendingHtml;
      wireDecisionCardActions(el);
    }

    if (resolvedSection && resolvedEl) {
      if (!resolved.length) {
        resolvedSection.style.display = 'none';
        resolvedEl.innerHTML = '';
      } else {
        resolvedSection.style.display = '';
        if (resolvedSummary) { resolvedSummary.textContent = 'Resolved Decisions (' + resolved.length + ')'; }
        resolved.sort(function(a, b) {
          return Date.parse(b.item.updatedAt || b.item.createdAt || 0) - Date.parse(a.item.updatedAt || a.item.createdAt || 0);
        });
        var capped = resolved.slice(0, 20);
        var resolvedHtml = '';
        for (var ri = 0; ri < capped.length; ri++) {
          resolvedHtml += renderDecisionCard(capped[ri].item, capped[ri].statusKey);
        }
        resolvedEl.innerHTML = resolvedHtml;
        wireDecisionCardActions(resolvedEl);
      }
    }
  }

  // Groups by workUnitId so a work unit that failed more than once (e.g. "max iterations"
  // then, after a manual retry, a transient provider error) shows as ONE card with a history
  // trail, instead of one confusing card per attempt with no visible relationship between them.
  function renderBlockedExplorations(deadLetters, goals, providerRetries) {
    var el = $('blocked');
    if (!deadLetters || !deadLetters.length) {
      el.innerHTML = '<p class="empty">No blocked explorations.</p>';
      return;
    }
    var goalMap = {};
    for (var j = 0; j < (goals || []).length; j++) { goalMap[goals[j].workUnitId] = goals[j]; }

    var groups = {};
    for (var g = 0; g < deadLetters.length; g++) {
      var entry = deadLetters[g];
      (groups[entry.workUnitId] = groups[entry.workUnitId] || []).push(entry);
    }

    var retryCounts = {};
    for (var r = 0; r < (providerRetries || []).length; r++) {
      var pr = providerRetries[r];
      if (!pr.workUnitId) { continue; }
      retryCounts[pr.workUnitId] = (retryCounts[pr.workUnitId] || 0) + 1;
    }

    var html = '';
    Object.keys(groups).forEach(function(workUnitId) {
      var chain = groups[workUnitId].slice().sort(function(a, b) { return String(a.occurredAt).localeCompare(String(b.occurredAt)); });
      var latest = chain[chain.length - 1];
      var earlier = chain.slice(0, -1);
      var wu = goalMap[workUnitId];
      var goal = wu ? wu.goal : workUnitId;
      var canRetry = !latest.maxAttemptsReached && latest.attemptCount < 3;
      var transientRetries = retryCounts[workUnitId] || 0;
      // Phase 1.4 two-track design: MaxIterationsExceeded is the Continue track — both of its
      // options are implemented now: "Continue" (resume the same work unit with reconstructed
      // prior context) and "Re-plan the slice" (decompose into fresh, independently-budgeted
      // siblings). Everything else is the Retry track ("re-plan from scratch" alongside plain
      // Retry). Same replan call either way, label differs only to match how a human would
      // describe what just happened. Unlike Retry, re-planning is deliberately NOT gated on
      // attempt count — it never resumes this same work unit; Continue does resume it, so it's
      // gated on canRetry the same way Retry is.
      var replanLabel = latest.kind === 'MaxIterationsExceeded' ? 'Re-plan the slice' : 'Re-plan from scratch';
      var isMaxIterations = latest.kind === 'MaxIterationsExceeded';

      html += '<div class="card">';
      html += '<div class="row">';
      html += '<span class="title" title="' + esc(goal) + '">' + esc(goal) + '</span>';
      html += badge('failed');
      html += '<div class="actions">';
      if (canRetry) {
        html += '<button class="ghost" data-action="retryDeadLetter" data-id="' + esc(latest.entryId) + '">Retry</button>';
        if (isMaxIterations) {
          html += '<button class="ghost" data-action="continueDeadLetter" data-id="' + esc(latest.entryId) + '">Continue</button>';
        }
      } else {
        html += '<span class="mono" style="opacity:0.6">Max attempts reached</span>';
      }
      html += '<button class="ghost" data-action="replanDeadLetter" data-id="' + esc(latest.entryId) + '">' + esc(replanLabel) + '</button>';
      html += '</div></div>';
      html += '<div class="row">';
      html += '<span class="mono">phase: ' + esc(latest.stage) + '</span>';
      html += '<span class="mono">model: ' + esc(latest.profileId) + '</span>';
      html += '<span class="mono">attempt ' + esc(String(latest.attemptCount)) + '/3</span>';
      if (transientRetries > 0) {
        html += '<span class="mono" title="Transient provider errors (e.g. rate limit/overload) retried automatically before this failure">' + esc(String(transientRetries)) + ' transient retr' + (transientRetries === 1 ? 'y' : 'ies') + '</span>';
      }
      html += '</div>';
      html += '<div class="row"><span class="mono">' + esc(latest.reason) + '</span></div>';
      if (earlier.length > 0) {
        html += '<details><summary class="mono" style="cursor:pointer;opacity:0.7">' + earlier.length + ' earlier attempt' + (earlier.length === 1 ? '' : 's') + '</summary>';
        for (var e = 0; e < earlier.length; e++) {
          html += '<div class="row" style="opacity:0.7"><span class="mono">' + esc(new Date(earlier[e].occurredAt).toLocaleString()) + ' — ' + esc(earlier[e].reason) + '</span></div>';
        }
        html += '</details>';
      }
      html += '</div>';
    });
    el.innerHTML = html;
    el.querySelectorAll('[data-action="retryDeadLetter"]').forEach(function(btn) {
      btn.addEventListener('click', function() {
        vscode.postMessage({ type: 'retryDeadLetter', entryId: btn.getAttribute('data-id') });
      });
    });
    el.querySelectorAll('[data-action="replanDeadLetter"]').forEach(function(btn) {
      btn.addEventListener('click', function() {
        vscode.postMessage({ type: 'replanDeadLetter', entryId: btn.getAttribute('data-id') });
      });
    });
    el.querySelectorAll('[data-action="continueDeadLetter"]').forEach(function(btn) {
      btn.addEventListener('click', function() {
        vscode.postMessage({ type: 'continueDeadLetter', entryId: btn.getAttribute('data-id') });
      });
    });
  }

  // Phase 12 — only leases with a non-empty wait queue are actionable (a held lease with no
  // waiter is just normal in-flight work); "Force Release" is the manual-override path for a
  // holder that crashed mid-write, leaving no live agent to Stop and no proposal to reject.
  function renderFileLeases(leases, goals) {
    var el = $('file-leases');
    var contested = (leases || []).filter(function(l) { return l.waitQueue && l.waitQueue.length > 0; });
    if (!contested.length) {
      el.innerHTML = '<p class="empty">No file lease conflicts.</p>';
      return;
    }
    var goalMap = {};
    for (var j = 0; j < (goals || []).length; j++) { goalMap[goals[j].workUnitId] = goals[j]; }
    function label(workUnitId) {
      var wu = goalMap[workUnitId];
      return wu ? wu.goal : workUnitId;
    }
    var html = '';
    for (var i = 0; i < contested.length; i++) {
      var l = contested[i];
      html += '<div class="card">';
      html += '<div class="row">';
      html += '<span class="title mono" title="' + esc(l.path) + '">' + esc(l.path) + '</span>';
      html += '<span class="badge interrupted">' + esc(String(l.waitQueue.length)) + ' waiting</span>';
      html += '<div class="actions">';
      html += '<button class="danger" data-action="releaseFileLease" data-wu="' + esc(l.holderWorkUnitId) + '">Force Release</button>';
      html += '</div>';
      html += '</div>';
      html += '<div class="row"><span class="mono">held by: ' + esc(label(l.holderWorkUnitId)) + '</span></div>';
      html += '<div class="row"><span class="mono">queued: ' + esc(l.waitQueue.map(label).join(', ')) + '</span></div>';
      html += '</div>';
    }
    el.innerHTML = html;
    el.querySelectorAll('[data-action="releaseFileLease"]').forEach(function(btn) {
      btn.addEventListener('click', function() {
        vscode.postMessage({ type: 'releaseFileLease', workUnitId: btn.getAttribute('data-wu') });
      });
    });
  }

  window.addEventListener('message', function(event) {
    var msg = event.data;
    if (msg.type === 'updateSessionPicker' && msg.panelId === 'shell-pane-execution-timeline') {
      var sel = $('et-session-override');
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
    if (msg.type === 'conflictResolveData') {
      renderManualResolveFiles(msg.conflictId, msg.files || [], msg.parentWorkUnitId || null);
      return;
    }
    if (msg.type === 'focusCandidatePromotion') {
      var promoSection = $('candidate-promotion-section');
      if (promoSection) { promoSection.scrollIntoView({ behavior: 'smooth', block: 'start' }); }
      return;
    }
    if (msg.type !== 'data') { return; }
    if (typeof msg.usePromotionBranch !== 'undefined') {
      globalUsePromotionBranch = !!msg.usePromotionBranch;
      globalCandidateBranchId = msg.candidateBranchId || 'candidate';
    }
    renderActiveGoals(msg.goals && msg.goals.length ? msg.goals : msg.workUnits, !!msg.goals, msg.guardrailStatuses || []);
    renderAgents(msg.agents, msg.workUnits);
    renderAwaitingResume(msg.awaitingResume || []);
    renderClarifications(msg.clarifications || [], msg.clarificationMetrics || null);
    renderPendingDecisions(msg.merges);
    renderBlockedExplorations(msg.deadLetters || [], msg.workUnits, msg.providerRetries || []);
    renderFileLeases(msg.fileLeases || [], msg.workUnits);
    renderCandidatePromotion(msg.candidatePending || [], msg.candidateConflicts || []);
    renderTaskConflicts(msg.taskConflicts || []);
    renderSyncGraph(msg.syncGraph || { frontierHeads: [] });
    var ts = $('last-updated');
    if (ts) { ts.textContent = 'updated ' + new Date().toLocaleTimeString(); }
  });

  // The 2s poll tick re-renders these sections by overwriting innerHTML wholesale — if that
  // happens while the user is mid-keystroke in one of the section's own textareas (steering notes,
  // resolve-manually content), the element gets destroyed and recreated out from under them,
  // silently dropping focus and whatever they'd typed. Skip the rebuild entirely while any
  // textarea/input inside the container is focused; the next poll after they click away picks up
  // fresh data normally.
  function isEditingWithin(container) {
    var active = document.activeElement;
    return !!(active && container && container.contains(active) &&
      (active.tagName === 'TEXTAREA' || active.tagName === 'INPUT'));
  }

  // pending: CandidatePendingItem[] (GET /studio/branches/candidate/pending), conflicts:
  // CandidateConflictRecord[] (GET /studio/branches/candidate/conflicts). Only rendered/visible
  // when globalUsePromotionBranch is on — see the 'data' handler below.
  function renderCandidatePromotion(pending, conflicts) {
    var section = $('candidate-promotion-section');
    var el = $('candidate-promotion');
    if (!section || !el) { return; }
    if (isEditingWithin(el)) { return; }

    if (!globalUsePromotionBranch) {
      section.style.display = 'none';
      return;
    }
    section.style.display = '';

    // The endpoint already excludes Resolved — "open" here covers both Open and Reconciling so a
    // conflict stays visible (with its actions disabled) while its reconciliation work unit runs.
    var openConflicts = conflicts || [];
    var conflictedProposalIds = {};
    for (var ci = 0; ci < openConflicts.length; ci++) { conflictedProposalIds[openConflicts[ci].proposalId] = openConflicts[ci]; }

    var html = '';

    if (!pending || !pending.length) {
      html += '<p class="empty">Nothing pending on "' + esc(globalCandidateBranchId) + '".</p>';
    } else {
      html += '<div class="row"><button class="add-btn" id="btn-promote-candidate" title="Write everything on ' +
        esc(globalCandidateBranchId) + ' to main (and disk, for any goal with a real repository attached)"' +
        (openConflicts.length ? ' disabled' : '') + '>↑ Promote to Main (' + pending.length + ')</button></div>';
      if (openConflicts.length) {
        html += '<p class="empty">Resolve the conflict(s) below before promoting.</p>';
      }
      for (var pi = 0; pi < pending.length; pi++) {
        var p = pending[pi];
        html += '<div class="card">';
        html += '<div class="row"><strong>' + esc(p.goal || p.summary) + '</strong></div>';
        html += '<div class="row"><span class="mono">' + esc(p.proposalId) + '</span>';
        if (p.workUnitId) { html += '<span class="mono">' + esc(p.workUnitId) + '</span>'; }
        html += '</div>';
        if (p.filesTouched && p.filesTouched.length) {
          html += '<div class="row mono" style="opacity:0.7">' + esc(p.filesTouched.join(', ')) + '</div>';
        }
        html += '</div>';
      }
    }

    for (var oi = 0; oi < openConflicts.length; oi++) {
      html += buildConflictCardHtml(openConflicts[oi], null);
    }

    el.innerHTML = html;

    var promoteBtn = $('btn-promote-candidate');
    if (promoteBtn) {
      promoteBtn.addEventListener('click', function() {
        vscode.postMessage({ type: 'promoteCandidate' });
      });
    }
    wireConflictCardActions(el);
  }

  // Shared by renderCandidatePromotion and renderTaskConflicts — every button carries a
  // data-parent attribute only when it belongs to a task conflict (set by buildConflictCardHtml),
  // which is what picks the task-scoped message type/fields over the candidate ones.
  function wireConflictCardActions(el) {
    el.querySelectorAll('[data-action="restartConflict"]').forEach(function(btn) {
      btn.addEventListener('click', function() {
        var conflictId = btn.getAttribute('data-cid');
        var notesEl = el.querySelector('[data-notes-for="' + conflictId + '"]');
        var steeringNotes = notesEl ? notesEl.value.trim() : '';
        // Restart just rejects the losing proposal — identical REST call regardless of source.
        vscode.postMessage({
          type: 'restartCandidateConflict', proposalId: btn.getAttribute('data-pid'),
          steeringNotes: steeringNotes || null,
        });
      });
    });
    el.querySelectorAll('[data-action="reconcileConflict"]').forEach(function(btn) {
      btn.addEventListener('click', function() {
        var conflictId = btn.getAttribute('data-cid');
        var parentWorkUnitId = btn.getAttribute('data-parent');
        var notesEl = el.querySelector('[data-notes-for="' + conflictId + '"]');
        var steeringNotes = notesEl ? notesEl.value.trim() : '';
        vscode.postMessage(parentWorkUnitId
          ? { type: 'reconcileTaskConflict', conflictId: conflictId, parentWorkUnitId: parentWorkUnitId, steeringNotes: steeringNotes || null }
          : { type: 'reconcileCandidateConflict', conflictId: conflictId, steeringNotes: steeringNotes || null });
      });
    });
    el.querySelectorAll('[data-action="viewConflictDiff"]').forEach(function(btn) {
      btn.addEventListener('click', function() {
        var paths = (btn.getAttribute('data-paths') || '').split('|').filter(Boolean);
        vscode.postMessage({
          type: 'viewConflictDiff',
          conflictId: btn.getAttribute('data-cid'),
          proposalId: btn.getAttribute('data-pid'),
          winningProposalId: btn.getAttribute('data-wpid') || null,
          conflictingPaths: paths,
        });
      });
    });
    el.querySelectorAll('[data-action="loadManualResolve"]').forEach(function(btn) {
      btn.addEventListener('click', function() {
        var paths = (btn.getAttribute('data-paths') || '').split('|').filter(Boolean);
        var parentWorkUnitId = btn.getAttribute('data-parent');
        vscode.postMessage({
          type: 'requestResolveManually',
          conflictId: btn.getAttribute('data-cid'),
          proposalId: btn.getAttribute('data-pid'),
          winningProposalId: btn.getAttribute('data-wpid') || null,
          conflictingPaths: paths,
          parentWorkUnitId: parentWorkUnitId || null,
        });
      });
    });
  }

  // Shared conflict-card markup for both candidate-branch and task-level conflicts. Pass
  // parentWorkUnitId only for the task case — its presence on the Reconcile/Resolve-Manually
  // buttons (as data-parent) is what wireConflictCardActions uses to route to the task-scoped
  // message types instead of the candidate ones.
  function buildConflictCardHtml(c, parentWorkUnitId) {
    var reconciling = c.status === 'Reconciling';
    var parentAttr = parentWorkUnitId ? ' data-parent="' + esc(parentWorkUnitId) + '"' : '';
    var html = '<div class="card" style="border-color:var(--nm-warn, #d9822b)">';
    html += '<div class="row"><span class="badge failed">⚠ Conflict' + (reconciling ? ' — reconciling…' : '') + '</span>' +
      '<span class="mono">' + esc(c.proposalId) + '</span></div>';
    if (parentWorkUnitId) {
      html += '<div class="row mono" style="opacity:0.6">goal ' + esc(parentWorkUnitId) + '</div>';
    }
    html += '<div class="row mono" style="opacity:0.8">' + esc((c.conflictingPaths || []).join(', ')) + '</div>';
    html += '<div class="row"><button class="ghost" data-action="viewConflictDiff" data-cid="' + esc(c.conflictId) +
      '" data-pid="' + esc(c.proposalId) + '" data-wpid="' + esc(c.winningProposalId || '') +
      '" data-paths="' + esc((c.conflictingPaths || []).join('|')) +
      '" title="Open a read-only diff for each conflicting file: what is on the shared target now vs. what the losing goal produced">👁 View Diff</button></div>';
    html += '<textarea class="steering-notes" data-notes-for="' + esc(c.conflictId) +
      '" placeholder="Steering notes (optional, used by both Reconcile and Restart below) — e.g. keep both sections, Brad first then Jake"' +
      (reconciling ? ' disabled' : '') + '></textarea>';
    html += '<div class="conflict-actions">';
    html += '<button class="primary" data-action="reconcileConflict" data-cid="' + esc(c.conflictId) + '"' + parentAttr +
      ' title="Create an agent to combine the changes from both goals into one result, instead of restarting from scratch"' +
      (reconciling ? ' disabled' : '') + '>⚭ Reconcile</button>';
    html += '<button class="danger" data-action="restartConflict" data-cid="' + esc(c.conflictId) +
      '" data-pid="' + esc(c.proposalId) + '"' +
      ' title="Revert the losing goal to its pre-attempt snapshot and restart it fresh, using the steering notes above"' +
      (reconciling ? ' disabled' : '') + '>↺ Revert &amp; Restart</button>';
    html += '</div>';
    html += '<div class="row"><button class="ghost" data-action="loadManualResolve" data-cid="' + esc(c.conflictId) +
      '" data-pid="' + esc(c.proposalId) + '" data-wpid="' + esc(c.winningProposalId || '') +
      '" data-paths="' + esc((c.conflictingPaths || []).join('|')) + '"' + parentAttr +
      (reconciling ? ' disabled' : '') + '>✎ Resolve Manually</button></div>';
    html += '<div class="manual-resolve" id="manual-resolve-' + esc(c.conflictId) + '" style="display:none"></div>';
    html += '</div>';
    return html;
  }

  // conflicts: TaskConflictRecord[] (flattened across every top-level goal's own
  // GET /studio/workunits/{id}/task-conflicts). Always rendered when non-empty — unlike candidate
  // promotion, task conflicts aren't gated behind a workspace-wide setting.
  function renderTaskConflicts(conflicts) {
    var el = $('task-conflicts');
    if (!el) { return; }
    if (isEditingWithin(el)) { return; }
    if (!conflicts || !conflicts.length) {
      el.innerHTML = '<p class="empty">No open task conflicts.</p>';
      return;
    }
    var html = '';
    for (var i = 0; i < conflicts.length; i++) {
      html += buildConflictCardHtml(conflicts[i], conflicts[i].parentWorkUnitId);
    }
    el.innerHTML = html;
    wireConflictCardActions(el);
  }

  // files: [{ path, winningContent, losingContent }], from the panel's requestResolveManually
  // round trip (fetches both sides' file-changes on demand — not fetched on every poll tick).
  // parentWorkUnitId is only set for a task conflict — its presence picks the task-scoped submit
  // message/endpoint over the candidate one.
  function renderManualResolveFiles(conflictId, files, parentWorkUnitId) {
    var container = $('manual-resolve-' + conflictId);
    if (!container) { return; }

    var target = parentWorkUnitId ? 'merge/' + parentWorkUnitId : 'candidate';
    var html = '<div class="row mono" style="opacity:0.6">Pre-filled with what is currently on ' + esc(target) + ' — edit to combine both sides, then submit.</div>';
    for (var i = 0; i < files.length; i++) {
      var f = files[i];
      html += '<div class="row mono" style="opacity:0.7">' + esc(f.path) + '</div>';
      html += '<div class="row mono" style="opacity:0.55;white-space:pre-wrap;max-height:80px;overflow:auto">Losing goal had: ' + esc(f.losingContent || '(no content)') + '</div>';
      html += '<textarea class="steering-notes" data-resolve-path="' + esc(f.path) + '" style="min-height:80px">' +
        esc(f.winningContent || '') + '</textarea>';
    }
    html += '<div class="row"><button class="primary" data-action="submitManualResolve" data-cid="' + esc(conflictId) + '">✓ Submit Resolution</button></div>';
    container.innerHTML = html;
    container.style.display = '';

    var submitBtn = container.querySelector('[data-action="submitManualResolve"]');
    if (submitBtn) {
      submitBtn.addEventListener('click', function() {
        var fileValues = {};
        container.querySelectorAll('[data-resolve-path]').forEach(function(ta) {
          fileValues[ta.getAttribute('data-resolve-path')] = ta.value;
        });
        vscode.postMessage(parentWorkUnitId
          ? { type: 'resolveTaskConflictManually', conflictId: conflictId, parentWorkUnitId: parentWorkUnitId, files: fileValues }
          : { type: 'resolveConflictManually', conflictId: conflictId, files: fileValues });
      });
    }
  }

  function renderSyncGraph(data) {
    var el = $('sync-graph');
    if (!el) { return; }
    var heads = (data && data.frontierHeads) ? data.frontierHeads : [];
    if (heads.length === 0) {
      el.innerHTML = '<div class="sync-graph-card"><div class="sg-label">CRDT Causal Graph</div>' +
        '<span class="sg-empty">No promoted checkpoints — frontier is empty.</span></div>';
      return;
    }
    var headsHtml = heads.map(function(h) {
      return '<span class="sg-badge">' + h.slice(0, 8) + '…' + h.slice(-4) + '</span>';
    }).join('');
    el.innerHTML = '<div class="sync-graph-card">' +
      '<div class="sg-label">CRDT Causal Graph</div>' +
      '<span class="sg-promoted">&#x25CF; ' + heads.length + ' frontier head' + (heads.length === 1 ? '' : 's') + '</span>' +
      '<div class="sg-heads">' + headsHtml + '</div>' +
      '</div>';
  }

}
