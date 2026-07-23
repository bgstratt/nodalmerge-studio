// Extracted from src/panels/ArtifactExplorerPanel.ts (GW_JS) by scripts/extract-view.mjs.
// Body is the exact runtime string of the former inline <script>, with the historical
// scopeViewScript() rewrite (getElementById -> $, querySelector(All) -> root.*) baked in.
// One deliberate change from the inline original: the local esc() had lost its HTML
// entities (a no-op replace chain), leaving markup injection open; it now uses the
// shared escaper.

import { esc } from './lib/esc.js';
import { hasSelectionWithin } from './lib/selection.js';

/** @param {{ root: HTMLElement, vscode: { postMessage(m: any): void }, $: (id: string) => HTMLElement | null }} ctx */
export function init(ctx) {
  var root = ctx.root;
  var vscode = ctx.vscode;
  var $ = ctx.$;

  // vscode supplied by ctx (was: acquireVsCodeApi())
  var state = {
    decisionNodes: [], selectedNodeId: null, timelineArtifacts: [], timelineEvents: [], selectedSessionId: '',
    selectedNodeConversation: null, conversationPollTimer: null,
    referenceFiles: [], reviewingWorkUnitIds: [], planningWorkUnitIds: [],
    // Which node the Decision-tab auto-jump has already fired for (so re-selecting or navigating
    // back to a node doesn't re-jump away from whatever tab the user is reading), and the cached
    // full detail for whichever proposal is currently shown in the Decision tab.
    autoJumpedForNodeId: null, selectedNodeProposalDetail: null,
  };

  // esc() imported from ./lib/esc.js (local copy had broken entity replacements)

  function badge(status) {
    var s = (status || '').toLowerCase().replace(/\s+/g, '');
    return '<span class="badge ' + s + '">' + esc(status || '—') + '</span>';
  }

  // Slice 14b — blockedReason is stale once the slice has moved past Created (it enqueued, so
  // the block was resolved), so only show it while still Created.
  function isBlocked(wu) {
    return !!(wu && wu.fanOutInfo && wu.fanOutInfo.blockedReason && (wu.status || '').toLowerCase() === 'created');
  }

  // wu.status doesn't change when a scheduled item is parked (AwaitingFileLease/AwaitingResume
  // just flag the scheduler queue item, not the WorkUnit) — surface a distinct "paused" badge so
  // a parked node doesn't just look like it's still running under its last-known status.
  function isPausedWu(wu) {
    return !!(wu && (wu.awaitingFileLease || wu.awaitingResume || wu.awaitingCredentials));
  }
  function pausedBadge(wu) {
    if (!isPausedWu(wu)) { return ''; }
    var label = wu.awaitingResume ? 'paused: awaiting resume'
      : wu.awaitingCredentials ? 'paused: awaiting credentials'
      : 'paused: file lease';
    var title = wu.awaitingResume
      ? 'A Host restart interrupted this mid-execution — click Resume to continue.'
      : wu.awaitingCredentials
      ? 'The Host restarted and lost its cached API key for this task — click Resume to resupply it.'
      : 'Waiting for another task to release a file it needs — usually resolves automatically. If this ' +
        'looks stuck, click Resume to force a retry (safe: it just re-checks the file and re-parks itself if the wait is still real).';
    return '<span class="badge paused" title="' + esc(title) + '">' + esc(label) + '</span>';
  }

  function stageBadge(stage) {
    if (!stage) { return '—'; }
    var s = stage.toLowerCase();
    return '<span class="badge stage ' + s + '">' + esc(stage) + '</span>';
  }

  function fmtTime(iso) {
    try { return new Date(iso).toLocaleTimeString(); } catch (e) { return ''; }
  }

  // ── Artifact classifcation for typed labels ─────────────────────────────

  function classifyArtifact(artifactType) {
    var map = {
      Goal:             { label: 'Goal',             icon: '🎯' },
      Plan:             { label: 'Plan Proposal',    icon: '📐' },
      Decision:         { label: 'Reasoning Step',   icon: '🧠' },
      Research:         { label: 'Research',         icon: '🔍' },
      Constraint:       { label: 'Constraint',       icon: '🔒' },
      Task:             { label: 'Task',             icon: '📋' },
      BranchChangeset:  { label: 'Code Change',      icon: '📁' },
      MergeProposal:    { label: 'Decision Candidate', icon: '📐' },
      MergeResult:      { label: 'Merged',           icon: '✅' },
      RevisionContext:  { label: 'Prior Attempt',    icon: '↩️' },
    };
    return map[artifactType] || { label: artifactType, icon: '' };
  }

  // ── Top bar ──────────────────────────────────────────────────────────────

  $('gw-session').addEventListener('change', function(ev) {
    state.selectedSessionId = ev.target.value;
    vscode.postMessage({ type: 'explorerSelectSession', sessionId: ev.target.value });
    $('gw-tree').innerHTML = '<p class="empty">Loading…</p>';
    updateSessionControls(ev.target.value, state.__sessions || []);
  });

  function updateSessionControls(sessionId, sessions) {
    var pauseBtn = $('gw-session-pause');
    var resumeBtn = $('gw-session-resume');
    var parkedBadge = $('gw-session-parked-badge');
    var resumeParkedBtn = $('gw-session-resume-parked');
    var stalledBadge = $('gw-session-stalled-badge');
    var reinvokeBtn = $('gw-session-reinvoke');
    if (!sessionId) {
      pauseBtn.style.display = 'none';
      resumeBtn.style.display = 'none';
      parkedBadge.style.display = 'none';
      resumeParkedBtn.style.display = 'none';
      stalledBadge.style.display = 'none';
      reinvokeBtn.style.display = 'none';
      return;
    }
    var session = (sessions || []).find(function(s) { return s.sessionId === sessionId; });
    var isPaused = session && session.status === 'Paused';
    pauseBtn.style.display = (!isPaused && session) ? '' : 'none';
    resumeBtn.style.display = isPaused ? '' : 'none';

    // ExecutionSessionStatus (isPaused above) is a human-initiated pause and is never touched by
    // a Host restart, so this can show "active" while the scheduler has actually parked work
    // underneath it — surface that separately rather than only showing a misleading plain Pause
    // button with no indication anything needs attention.
    var hasParkedWork = !!(session && session.hasParkedWork);
    if (hasParkedWork) {
      var reasonLabel = session.parkedReason === 'AwaitingCredentials' ? 'awaiting credentials'
        : session.parkedReason === 'AwaitingResume' ? 'awaiting resume'
        : 'file lease';
      parkedBadge.textContent = 'parked: ' + reasonLabel;
      parkedBadge.title = 'Work under this goal is parked and needs a human/extension to resume it — the Pause/Resume buttons above track something else entirely (a manual full-goal pause).';
      parkedBadge.style.display = '';
      resumeParkedBtn.style.display = '';
    } else {
      parkedBadge.style.display = 'none';
      resumeParkedBtn.style.display = 'none';
    }

    // No live agent, no queued work, and nothing parked — nothing is driving the goal forward.
    // Mutually exclusive with the parked badge above by construction (server only sets
    // orchestratorStalled when nothing is parked). The Reinvoke button runs the server's
    // credential-free convergence sweep (plans/orchestrator-pure-service.md M2).
    var orchestratorStalled = !!(session && session.orchestratorStalled);
    if (orchestratorStalled) {
      stalledBadge.title = 'Nothing is driving this goal forward — no running agent, no queued work, and nothing blocked. Click Reinvoke to run a convergence sweep (it re-enqueues the planner if the goal never got one).';
      stalledBadge.style.display = '';
      reinvokeBtn.style.display = '';
    } else {
      stalledBadge.style.display = 'none';
      reinvokeBtn.style.display = 'none';
    }
  }

  $('gw-session-pause').addEventListener('click', function() {
    var session = (state.__sessions || []).find(function(s) { return s.sessionId === state.selectedSessionId; });
    if (!session) { return; }
    vscode.postMessage({ type: 'explorerGoalPause', goalId: session.rootWorkUnitId });
  });

  $('gw-session-resume').addEventListener('click', function() {
    var session = (state.__sessions || []).find(function(s) { return s.sessionId === state.selectedSessionId; });
    if (!session) { return; }
    vscode.postMessage({ type: 'explorerGoalResume', goalId: session.rootWorkUnitId });
  });

  $('gw-session-resume-parked').addEventListener('click', function() {
    var session = (state.__sessions || []).find(function(s) { return s.sessionId === state.selectedSessionId; });
    if (!session || !session.parkedWorkUnitIds || !session.parkedWorkUnitIds.length) { return; }
    vscode.postMessage({ type: 'explorerResumeParkedWork', workUnitIds: session.parkedWorkUnitIds });
  });

  $('gw-session-reinvoke').addEventListener('click', function() {
    var session = (state.__sessions || []).find(function(s) { return s.sessionId === state.selectedSessionId; });
    if (!session) { return; }
    vscode.postMessage({
      type: 'explorerReinvokeOrchestrator',
      workUnitId: session.rootWorkUnitId,
      profileId: session.orchestratorProfileId || null,
    });
  });

  // ── Task/Workspace Review radios — reveal the hybrid-minutes textbox only when Hybrid is
  // selected for that group. Two independent groups since Task Review (worker -> session) and
  // Workspace Review (session -> your workspace on disk) are separate concerns.
  function bindHybridMinutesToggle(radioName, minutesId) {
    var minutesEl = $(minutesId);
    root.querySelectorAll('input[name=' + radioName + ']').forEach(function(radio) {
      radio.addEventListener('change', function() {
        if (minutesEl) { minutesEl.classList.toggle('hidden', this.value !== 'Hybrid'); }
      });
    });
  }
  bindHybridMinutesToggle('gw-task-review-policy', 'gw-task-review-hybrid-minutes');
  bindHybridMinutesToggle('gw-workspace-review-policy', 'gw-workspace-review-hybrid-minutes');

  // ── Seed Task Review / Workspace Review from the session defaults (Model & Agent Studio panel
  // / the nodalmerge.defaultTaskReviewPolicy / nodalmerge.defaultWorkspaceReviewPolicy settings)
  // at goal-creation time only — this is a one-time seed, not a live binding: once the user
  // changes a radio here it's this goal's own choice, and changing the session default later
  // must not retroactively change it.
  function seedReviewPolicyDefaults(taskPolicy, workspacePolicy) {
    if (taskPolicy) {
      var taskRadio = root.querySelector('input[name=gw-task-review-policy][value="' + taskPolicy + '"]');
      if (taskRadio) { taskRadio.checked = true; }
    }
    if (workspacePolicy) {
      var workspaceRadio = root.querySelector('input[name=gw-workspace-review-policy][value="' + workspacePolicy + '"]');
      if (workspaceRadio) { workspaceRadio.checked = true; }
    }
  }

  $('gw-run').addEventListener('click', function() {
    var goal = $('gw-goal').value.trim();
    var strategy = $('gw-strategy').value;
    if (!goal) { return; }
    var forkConfig = collectForkConfig();
    var taskReviewPolicyEl = root.querySelector('input[name=gw-task-review-policy]:checked');
    var workspaceReviewPolicyEl = root.querySelector('input[name=gw-workspace-review-policy]:checked');
    var taskMinutesEl = $('gw-task-review-hybrid-minutes');
    var workspaceMinutesEl = $('gw-workspace-review-hybrid-minutes');
    var targetEl = root.querySelector('input[name=gw-target]:checked');
    var btn = $('gw-run');
    btn.disabled = true;
    btn.textContent = 'Running…';
    var taskReviewPolicy = taskReviewPolicyEl ? taskReviewPolicyEl.value : 'HumanRequired';
    var workspaceReviewPolicy = workspaceReviewPolicyEl ? workspaceReviewPolicyEl.value : 'HumanRequired';
    var taskMinutes = (taskReviewPolicy === 'Hybrid' && taskMinutesEl && taskMinutesEl.value.trim())
      ? parseInt(taskMinutesEl.value.trim(), 10) : undefined;
    var workspaceMinutes = (workspaceReviewPolicy === 'Hybrid' && workspaceMinutesEl && workspaceMinutesEl.value.trim())
      ? parseInt(workspaceMinutesEl.value.trim(), 10) : undefined;
    var planDepthEl = $('gw-goal-plan-depth');
    var planDepthVal = planDepthEl ? parseInt(planDepthEl.value, 10) : NaN;
    var globalDepth = (state && state.globalMaxPlanDepth) || 1;
    // Send a per-goal override only when the user set it to something other than the global default;
    // otherwise the goal just follows the global setting.
    var maxPlanDepthOverride = (!isNaN(planDepthVal) && planDepthVal >= 1 && planDepthVal !== globalDepth) ? planDepthVal : undefined;
    vscode.postMessage({
      type: 'explorerRun', strategy: strategy, goal: goal, forkConfig: forkConfig,
      taskReviewPolicy: taskReviewPolicy,
      workspaceReviewPolicy: workspaceReviewPolicy,
      taskReviewHybridTimeoutMinutes: (taskMinutes && !isNaN(taskMinutes)) ? taskMinutes : undefined,
      workspaceReviewHybridTimeoutMinutes: (workspaceMinutes && !isNaN(workspaceMinutes)) ? workspaceMinutes : undefined,
      bypassPromotionBranch: targetEl ? targetEl.value === 'direct' : false,
      maxPlanDepthOverride: maxPlanDepthOverride,
      referenceFiles: (state.referenceFiles || []).map(function(r) { return { repositoryId: r.repositoryId, path: r.path }; }),
    });
  });

  // ── Cross-repo file reference chips ─────────────────────────────────────
  function renderReferenceChips() {
    var el = $('gw-reference-chips');
    if (!el) { return; }
    el.innerHTML = (state.referenceFiles || []).map(function(r, i) {
      return '<span class="gw-reference-chip" title="' + esc(r.repositoryLabel || r.repositoryId) + '">' +
        esc((r.repositoryLabel || r.repositoryId) + ' / ' + r.path) +
        '<span class="gw-reference-chip-remove" data-index="' + i + '">&times;</span></span>';
    }).join('');
    el.querySelectorAll('.gw-reference-chip-remove').forEach(function(btn) {
      btn.addEventListener('click', function() {
        state.referenceFiles.splice(parseInt(this.getAttribute('data-index'), 10), 1);
        renderReferenceChips();
      });
    });
  }

  $('gw-add-reference-btn').addEventListener('click', function() {
    vscode.postMessage({ type: 'explorerAddReference' });
  });

  // ── Slice 22c — Strategy dropdown change reveals fork config panel ─────
  $('gw-strategy').addEventListener('change', function() {
    var strategy = this.value;
    var panel = $('gw-fork-config');
    if (strategy === 'Multi-Model Comparison' || strategy === 'Architecture Fork' || strategy === 'Library Comparison' || strategy === 'Product Strategy Fork') {
      panel.classList.add('visible');
      if (!state.forkConfig || !state.forkConfig.length) {
        state.forkConfig = buildDefaultForkConfig(strategy);
      }
      renderForkConfigPanel(state.forkConfig);
    } else {
      panel.classList.remove('visible');
    }
  });

  // ── Slice 22c — Inline fork config panel helpers ──────────────────────
  function buildDefaultForkConfig(strategy) {
    var orchProfiles = (state.agentProfiles || []).filter(function(p) { return p.domain === 'orchestration' && p.model; });
    if (orchProfiles.length < 2) {
      orchProfiles = (state.agentProfiles || []).filter(function(p) { return p.domain === 'orchestration'; });
    }
    var allProfiles = state.agentProfiles || [];
    var numForks = strategy === 'Multi-Model Comparison' ? 2 : 2;
    var entries = [];
    for (var i = 0; i < numForks; i++) {
      entries.push({ profileId: orchProfiles[i] ? orchProfiles[i].id : (allProfiles[i] ? allProfiles[i].id : ''), constraintHint: '' });
    }
    return entries;
  }

  function collectForkConfig() {
    var entries = [];
    var panel = $('gw-fork-config');
    if (!panel || !panel.classList.contains('visible')) { return entries; }
    panel.querySelectorAll('.gw-fork-entry').forEach(function(entry) {
      var sel = entry.querySelector('select');
      var txt = entry.querySelector('input[type=text]');
      entries.push({ profileId: sel ? sel.value : '', constraintHint: txt ? txt.value : '' });
    });
    return entries;
  }

  function renderForkConfigPanel(entries) {
    state.forkConfig = entries || [];
    var el = $('gw-fork-entries');
    if (!el) { return; }
    var profiles = state.agentProfiles || [];
    var html = '';
    (entries || []).forEach(function(entry, i) {
      html += '<div class="gw-fork-entry">';
      html += '<div class="gw-fork-entry-title">Fork ' + (i + 1) + '</div>';
      html += '<div class="gw-field"><label>Profile</label><select>' + profiles.map(function(p) {
        return '<option value="' + esc(p.id) + '"' + (p.id === entry.profileId ? ' selected' : '') + '>' + esc(p.label) + (p.model ? ' (' + esc(p.model) + ')' : '') + '</option>';
      }).join('') + '</select></div>';
      html += '<div class="gw-field"><label>Constraint (optional)</label><input type="text" value="' + esc(entry.constraintHint || '') + '" placeholder="e.g. use gRPC instead of REST"/></div>';
      html += '</div>';
    });
    var addBtn = '<div class="gw-field" style="align-self:flex-end"><button id="gw-add-fork-btn" class="ghost" style="padding:3px 10px;font-size:0.78em">+ Add Fork</button></div>';
    el.innerHTML = html + addBtn;
    var addBtnEl = $('gw-add-fork-btn');
    if (addBtnEl) {
      addBtnEl.addEventListener('click', function() {
        if (!state.forkConfig) { state.forkConfig = []; }
        state.forkConfig.push({ profileId: (profiles[0] || {}).id || '', constraintHint: '' });
        renderForkConfigPanel(state.forkConfig);
      });
    }
  }

  $('gw-load-cas').addEventListener('click', function() {
    var btn = $('gw-load-cas');
    btn.disabled = true;
    btn.textContent = '☁ Loading…';
    vscode.postMessage({ type: 'explorerReconcileCasOrigin' });
  });

  // ── Exploration Settings ─────────────────────────────────────────────────

  $('gw-settings-btn').addEventListener('click', function() {
    var panel = $('gw-settings-panel');
    panel.style.display = panel.style.display === 'none' ? 'block' : 'none';
  });

  $('gw-repo-path-browse').addEventListener('click', function() {
    vscode.postMessage({ type: 'explorerBrowseRepositoryPath' });
  });

  $('gw-repo-path-clear').addEventListener('click', function() {
    vscode.postMessage({ type: 'explorerClearRepositoryPath' });
  });

  $('gw-repo-relink').addEventListener('click', function() {
    vscode.postMessage({ type: 'explorerRelinkRepository' });
  });

  $('gw-llm-profile-checkbox').addEventListener('change', function(ev) {
    vscode.postMessage({ type: 'explorerSetUseLlmProfileSelection', value: ev.target.checked });
  });

  $('gw-require-build-checkbox').addEventListener('change', function(ev) {
    vscode.postMessage({ type: 'explorerSetRequireBuildBeforeProposal', value: ev.target.checked });
  });

  $('gw-require-test-checkbox').addEventListener('change', function(ev) {
    vscode.postMessage({ type: 'explorerSetRequireTestBeforeProposal', value: ev.target.checked });
  });

  $('gw-enforce-output-kind-checkbox').addEventListener('change', function(ev) {
    vscode.postMessage({ type: 'explorerSetEnforceExpectedOutputKind', value: ev.target.checked });
  });

  $('gw-max-concurrent-workers').addEventListener('change', function(ev) {
    var value = parseInt(ev.target.value, 10);
    if (!value || value < 1) { return; }
    vscode.postMessage({ type: 'explorerSetMaxConcurrentWorkers', value: value });
  });

  $('gw-scheduler-poll-interval').addEventListener('change', function(ev) {
    var value = parseInt(ev.target.value, 10);
    if (!value || value < 100) { return; }
    vscode.postMessage({ type: 'explorerSetSchedulerPollIntervalMs', value: value });
  });

  $('gw-block-conflicting-ops-checkbox').addEventListener('change', function(ev) {
    vscode.postMessage({ type: 'explorerSetBlockConflictingOps', value: ev.target.checked });
  });

  $('gw-allow-auto-requeue-checkbox').addEventListener('change', function(ev) {
    vscode.postMessage({ type: 'explorerSetAllowAutoRequeue', value: ev.target.checked });
  });

  $('gw-use-promotion-branch-checkbox').addEventListener('change', function(ev) {
    vscode.postMessage({ type: 'explorerSetUsePromotionBranch', value: ev.target.checked });
  });

  $('gw-allow-agent-git-commits-checkbox').addEventListener('change', function(ev) {
    vscode.postMessage({ type: 'explorerSetAllowAgentGitCommits', value: ev.target.checked });
  });

  $('gw-allow-agent-git-push-checkbox').addEventListener('change', function(ev) {
    vscode.postMessage({ type: 'explorerSetAllowAgentGitPush', value: ev.target.checked });
  });

  $('gw-materializer-concurrency').addEventListener('change', function(ev) {
    var value = parseInt(ev.target.value, 10);
    if (!value || value < 1) { return; }
    vscode.postMessage({ type: 'explorerSetMaterializerConcurrency', value: value });
  });

  $('gw-max-plan-depth').addEventListener('change', function(ev) {
    var value = parseInt(ev.target.value, 10);
    if (!value || value < 1) { return; }
    vscode.postMessage({ type: 'explorerSetMaxPlanDepth', value: value });
  });

  $('gw-max-failure-attempts').addEventListener('change', function(ev) {
    var value = parseInt(ev.target.value, 10);
    if (!value || value < 1) { return; }
    vscode.postMessage({ type: 'explorerSetMaxFailureAttempts', value: value });
  });

  $('gw-clarification-timeout-seconds').addEventListener('change', function(ev) {
    var value = parseInt(ev.target.value, 10);
    vscode.postMessage({ type: 'explorerSetClarificationTimeoutSeconds', value: isNaN(value) || value < 0 ? 0 : value });
  });

  $('gw-clarification-timeout-behavior').addEventListener('change', function(ev) {
    vscode.postMessage({ type: 'explorerSetClarificationTimeoutBehavior', value: ev.target.value });
  });

  // Phase Y — Steer & Retry profile toggle
  function toggleSteerRetryProfile(workUnitId) {
    var checkbox = $('dl-use-new-profile-' + workUnitId);
    var select = $('dl-profile-select-' + workUnitId);
    if (checkbox && select) {
      select.style.display = checkbox.checked ? 'block' : 'none';
    }
  }

  // ── Resizable columns ─────────────────────────────────────────────────────

  (function setupColumnResizers() {
    var MIN_COL_WIDTH = 50;
    var MIN_TIMELINE_WIDTH = 50;
    var treeEl = $('gw-col-tree');
    var inspectorEl = $('gw-col-inspector');
    var bodyEl = root.querySelector('.gw-body');

    var saved = null;
    try { saved = JSON.parse(localStorage.getItem('nm-gw-column-widths') || 'null'); } catch (e) { saved = null; }
    if (saved && saved.tree) { treeEl.style.width = saved.tree + 'px'; }
    if (saved && saved.inspector) { inspectorEl.style.width = saved.inspector + 'px'; }

    function persistWidths() {
      try {
        localStorage.setItem('nm-gw-column-widths', JSON.stringify({
          tree: treeEl.getBoundingClientRect().width,
          inspector: inspectorEl.getBoundingClientRect().width,
        }));
      } catch (e) { /* localStorage unavailable — resizing still works, just won't persist */ }
    }

    function bindResizer(resizerEl, targetEl, otherEl, direction) {
      resizerEl.addEventListener('mousedown', function(downEv) {
        downEv.preventDefault();
        var startX = downEv.clientX;
        var startWidth = targetEl.getBoundingClientRect().width;
        // Recomputed per-drag (not just once) since the other fixed column may have been
        // resized since this resizer was bound, and the timeline needs to keep its own floor.
        var maxWidth = bodyEl.getBoundingClientRect().width - otherEl.getBoundingClientRect().width - MIN_TIMELINE_WIDTH - 10;
        resizerEl.classList.add('gw-resizing');
        document.body.style.cursor = 'col-resize';

        function onMove(moveEv) {
          var next = startWidth + (moveEv.clientX - startX) * direction;
          next = Math.max(MIN_COL_WIDTH, Math.min(next, maxWidth));
          targetEl.style.width = next + 'px';
        }
        function onUp() {
          document.removeEventListener('mousemove', onMove);
          document.removeEventListener('mouseup', onUp);
          resizerEl.classList.remove('gw-resizing');
          document.body.style.cursor = '';
          persistWidths();
        }
        document.addEventListener('mousemove', onMove);
        document.addEventListener('mouseup', onUp);
      });
    }

    // Dragging the tree|timeline resizer right grows the tree column; dragging the
    // timeline|inspector resizer right shrinks the inspector column (it's anchored to the right).
    bindResizer($('gw-resizer-tree'), treeEl, inspectorEl, 1);
    bindResizer($('gw-resizer-inspector'), inspectorEl, treeEl, -1);
  })();

  // ── Live stage updates ───────────────────────────────────────────────────

  function connectStageSocket(wsUrl) {
    var ws;
    try { ws = new WebSocket(wsUrl); } catch (e) { return; }
    ws.onopen = function() {
      ws.send(JSON.stringify({ type: 'hello', room: 'studio-main', pubkey: 'studio-explorer', frontier: [] }));
    };
    ws.onmessage = function(e) {
      var msg;
      try { msg = JSON.parse(e.data); } catch (err) { return; }
      if (msg && msg.type === 'work-unit-stage-changed') {
        applyStageChange(msg.workUnitId, msg.stage);
      }
    };
    ws.onclose = function() { setTimeout(function() { connectStageSocket(wsUrl); }, 2000); };
    ws.onerror = function() { ws.close(); };
  }

  function applyStageChange(workUnitId, stage) {
    var node = state.decisionNodes.find(function(w) { return w.workUnitId === workUnitId; });
    if (!node) { return; }
    node.currentStage = stage || null;
    renderDecisionTree(state.decisionNodes);
    if (state.selectedNodeId === workUnitId) {
      $('gw-inspector').innerHTML = renderDecisionInspector(node);
      bindDecisionInspectorTabs();
    }
  }

  // ── Decision Tree ────────────────────────────────────────────────────────

  function renderDecisionTree(decisionNodes) {
    state.decisionNodes = decisionNodes || [];
    var el = $('gw-tree');
    if (!decisionNodes || !decisionNodes.length) {
      el.innerHTML = '<p class="empty">No decision nodes in this exploration yet.</p>';
      return;
    }
    var byParent = {};
    var roots = [];
    decisionNodes.forEach(function(wu) {
      var p = wu.parentWorkUnitId || null;
      if (p && decisionNodes.some(function(w) { return w.workUnitId === p; })) {
        (byParent[p] = byParent[p] || []).push(wu);
      } else {
        roots.push(wu);
      }
    });
    var html = '';
    function renderNode(wu, depth) {
      var sel = wu.workUnitId === state.selectedNodeId ? ' selected' : '';
      html += '<div class="dn-node' + sel + '" style="margin-left:' + (depth * 14) + 'px" data-wu="' + esc(wu.workUnitId) + '">';
      html += '<div class="dn-title" title="' + esc(wu.goal) + '">' + esc(wu.goal) + '</div>';
      html += '<div class="dn-meta">' + badge(wu.status) + pausedBadge(wu);
      if (isBlocked(wu)) { html += '<span class="badge blocked" title="' + esc(wu.fanOutInfo.blockedReason) + '">blocked</span>'; }
      // Slice 18b — fork-type badge
      if (wu.forkType && (wu.forkType || '').toLowerCase() !== 'unknown') {
        html += '<span class="badge fork-type">' + esc(wu.forkType) + '</span>';
      }
      if (wu.currentStage) { html += stageBadge(wu.currentStage); }
      if (wu.proposalCount) { html += '<span class="mono">' + wu.proposalCount + ' candidate(s)</span>'; }
      html += '</div>';
      // Live "an inline reviewer agent is actively looking at this proposal right now" indicator —
      // distinct from the static .badge.reviewing class above (that one reflects a reconciled
      // fan-out parent's WorkUnitStatus.Reviewing, a different concept). Sourced from the same
      // /studio/agents poll Activity Center already uses; disappears once the review concludes.
      if ((state.reviewingWorkUnitIds || []).indexOf(wu.workUnitId) !== -1) {
        html += '<div class="dn-meta"><span class="pulse"></span><span class="mono">Agent reviewing…</span></div>';
      }
      // Same live-indicator mechanism, for the planner's spawn — makes it visible when a Plan
      // stage is actually running vs. a stalled goal sitting idle at the same "Plan" stage badge.
      if ((state.planningWorkUnitIds || []).indexOf(wu.workUnitId) !== -1) {
        html += '<div class="dn-meta"><span class="pulse"></span><span class="mono">Planning…</span></div>';
      }
      // Slice 22c — Experiment parent badges
      var children = (byParent[wu.workUnitId] || []);
      if (children.length >= 2) {
        var childForkTypes = children.map(function(c) { return c.forkType || ''; }).filter(function(t) { return t && t.toLowerCase() !== 'unknown'; });
        // Show "Compare Results" only for experiment forks (children with named fork types),
        // not for normal Decompose fan-outs where children are dividing work, not competing.
        if (childForkTypes.length >= 2) {
          html += '<div class="dn-exp-badges">';
          html += '<span class="badge forks">' + childForkTypes.length + ' forks</span>';
          html += '<span class="compare-link" data-exp-parent="' + esc(wu.workUnitId) + '">Compare Results</span>';
          html += '</div>';
        }
      }
      // Slice 25c — Counterfactual badge + comparison link
      var cfOriginalId = wu.metadata && wu.metadata.counterfactualFromWorkUnitId;
      if (cfOriginalId) {
        html += '<div class="dn-exp-badges">';
        html += '<span class="badge cf">Counterfactual</span>';
        html += '<span class="compare-link cf-compare-link" data-cf-original="' + esc(cfOriginalId) + '">Compare with Original</span>';
        html += '</div>';
      }
      html += '</div>';
      (byParent[wu.workUnitId] || []).forEach(function(child) { renderNode(child, depth + 1); });
    }
    roots.forEach(function(r) { renderNode(r, 0); });
    el.innerHTML = html;
    el.querySelectorAll('.dn-node').forEach(function(node) {
      node.addEventListener('click', function() {
        var id = node.getAttribute('data-wu');
        stopConversationPoll();
        state.selectedNodeId = id;
        state.selectedNodeConversation = null;
        state.selectedNodeProposalDetail = null;
        renderDecisionTree(state.decisionNodes);
        $('gw-timeline').innerHTML = '<p class="empty">Loading…</p>';
        // No Decision tab yet — this node's own timeline/artifacts haven't loaded, so the
        // previous node's candidate (in state.timelineArtifacts) doesn't apply here.
        $('gw-inspector').innerHTML = renderDecisionInspector(state.decisionNodes.find(function(w) { return w.workUnitId === id; }), { candidate: null });
        bindDecisionInspectorTabs();
        vscode.postMessage({ type: 'explorerSelectWorkUnit', workUnitId: id });
      });
    });
    // Slice 22c — Compare Results link handler
    el.querySelectorAll('.compare-link').forEach(function(link) {
      link.addEventListener('click', function(ev) {
        ev.stopPropagation();
        var parentId = link.getAttribute('data-exp-parent');
        if (!parentId) { return; }
        var children = byParent[parentId] || [];
        var proposalIds = [];
        children.forEach(function(c) {
          if (c.latestProposalId) { proposalIds.push(c.latestProposalId); }
        });
        // Fetch each child's timeline to find proposals
        state.__compareChildren = children;
        state.__compareParentId = parentId;
        $('gw-inspector').innerHTML = renderCompareResults(children, parentId);
        bindCompareResultsButtons();
      });
    });
    // Slice 25c — Compare with Original link handler
    el.querySelectorAll('.cf-compare-link').forEach(function(link) {
      link.addEventListener('click', function(ev) {
        ev.stopPropagation();
        var originalId = link.getAttribute('data-cf-original');
        if (!originalId) { return; }
        $('gw-inspector').innerHTML = '<p class="empty">Loading comparison…</p>';
        vscode.postMessage({ type: 'explorerLoadCounterfactualComparison', workUnitId: originalId });
      });
    });
    el.querySelectorAll('.dn-node').forEach(function(node) {
      node.addEventListener('contextmenu', function(ev) {
        ev.preventDefault();
        var id = node.getAttribute('data-wu');
        renderNodeActionMenu(id);
      });
    });
  }

  function renderNodeActionMenu(workUnitId) {
    var el = $('gw-inspector');
    var html = '<div class="meta-grid"><span class="meta-label">Decision node</span><span class="mono">' + esc(workUnitId) + '</span></div>';
    html += '<div class="inspector-actions">';
    html += '<button class="ghost" data-wu-action="spawnTask" data-wu="' + esc(workUnitId) + '">Spawn Task</button>';
    html += '<button class="ghost" data-wu-action="forkHypothesis" data-wu="' + esc(workUnitId) + '">Fork Hypothesis</button>';
    html += '<button class="ghost" data-wu-action="reexplore" data-wu="' + esc(workUnitId) + '">Re-explore</button>';
    html += '<button class="ghost" data-wu-action="forkLatest" data-wu="' + esc(workUnitId) + '">Fork from latest candidate</button>';
    html += '<button class="ghost" data-wu-action="forkKnownGood" data-wu="' + esc(workUnitId) + '">Fork from Known Good</button>';
    html += '</div>';
    el.innerHTML = html;
    bindDecisionInspectorTabs();
  }

  function bindWorkUnitActionButtons() {
    root.querySelectorAll('[data-wu-action]').forEach(function(btn) {
      btn.addEventListener('click', function() {
        var action = btn.getAttribute('data-wu-action');
        if (action === 'steerPause' || action === 'steerForkFromNode' || action === 'steerDeadLetterRetry' || action === 'continueDeadLetter') {
          vscode.postMessage({
            type: 'explorerSteeringAction',
            action: action,
            workUnitId: btn.getAttribute('data-wu'),
            agentId: btn.getAttribute('data-agent') || '',
          });
        } else if (action === 'steerDeadLetterRetrySend') {
          var wuId = btn.getAttribute('data-wu');
          var contextEl = $('dl-steer-context-' + wuId);
          var steeringContext = contextEl ? contextEl.value : '';
          var useNewProfile = $('dl-use-new-profile-' + wuId);
          var profileSelect = $('dl-profile-select-' + wuId);
          var overrideModel = '';
          var overrideBaseUrl = '';
          var overrideApiKey = '';
          var overrideProvider = '';
          var overrideProfileId = '';
          if (useNewProfile && useNewProfile.checked && profileSelect && profileSelect.value) {
            // Look up the selected profile's model/detail from agentProfiles
            var selId = profileSelect.value;
            var prof = (state.agentProfiles || []).find(function(p) { return p.id === selId; });
            if (prof) {
              overrideModel = prof.model || '';
              overrideBaseUrl = prof.baseUrl || '';
              overrideProvider = prof.provider || '';
              overrideProfileId = prof.id || '';
            }
          }
          vscode.postMessage({
            type: 'explorerSteeringAction',
            action: 'steerDeadLetterRetrySend',
            workUnitId: wuId,
            steeringContext: steeringContext.trim() || '',
            overrideModel: overrideModel || undefined,
            overrideBaseUrl: overrideBaseUrl || undefined,
            overrideApiKey: overrideApiKey || undefined,
            overrideProvider: overrideProvider || undefined,
            overrideProfileId: overrideProfileId || undefined,
          });
        } else {
          vscode.postMessage({
            type: 'explorerWorkUnitAction',
            action: action,
            workUnitId: btn.getAttribute('data-wu'),
          });
        }
      });
    });
    // Inline onchange attributes are blocked by the webview's CSP (script-src is nonce-only,
    // no unsafe-inline), so the profile checkbox is wired here instead of via onchange="".
    root.querySelectorAll('[id^="dl-use-new-profile-"]').forEach(function(checkbox) {
      checkbox.addEventListener('change', function() {
        var workUnitId = checkbox.id.slice('dl-use-new-profile-'.length);
        toggleSteerRetryProfile(workUnitId);
      });
    });
  }

  function renderDecisionInspector(wu, opts) {
    if (!wu) { return '<p class="empty">Select a decision node or timeline item to inspect.</p>'; }
    opts = opts || {};
    // A pending decision candidate gets its own tab alongside Metadata/Context/Conversation
    // (instead of replacing the whole inspector) so jumping to it never strands the user away
    // from the rest of the goal/task's info — they're one tab click apart either direction.
    var candidate = ('candidate' in opts) ? opts.candidate : findDefaultProposalCandidate(state.timelineArtifacts);
    var activeTab = opts.activeTab || 'metadata';

    // ── Slice 24b — Tab bar ────────────────────────────────────────────────
    var html = '<div class="gw-tab-bar">';
    html += '<button class="gw-tab-btn' + (activeTab === 'metadata' ? ' active' : '') + '" data-gw-tab="metadata">Metadata</button>';
    html += '<button class="gw-tab-btn' + (activeTab === 'context' ? ' active' : '') + '" data-gw-tab="context">Context</button>';
    html += '<button class="gw-tab-btn' + (activeTab === 'conversation' ? ' active' : '') + '" data-gw-tab="conversation">Conversation</button>';
    if (candidate) {
      html += '<button class="gw-tab-btn' + (activeTab === 'decision' ? ' active' : '') + '" data-gw-tab="decision" data-proposal-id="' + esc(candidate.artifactId) + '">Decision</button>';
    }
    html += '</div>';

    // ── Metadata panel ────────────────────────────────────────────────────
    html += '<div class="gw-tab-panel' + (activeTab === 'metadata' ? ' active' : '') + '" id="gw-panel-metadata">';
    html += '<div class="meta-grid">';
    html += '<span class="meta-label">Decision Status</span>' + badge(wu.status) + pausedBadge(wu);
    html += '<span class="meta-label">Phase</span><span>' + stageBadge(wu.currentStage) + '</span>';
    html += '<span class="meta-label">Initiator</span><span class="mono">' + esc(wu.owner) + '</span>';
    html += '<span class="meta-label">Executor</span><span class="mono">' + esc(wu.assignedAgent || '—') + '</span>';
    html += '<span class="meta-label">Hypothesis Fork</span><span class="mono">' + esc(wu.branchId) + '</span>';
    if (wu.forkType && wu.forkType.toLowerCase() !== 'unknown') {
      html += '<span class="meta-label">Fork Type</span><span class="badge fork-type">' + esc(wu.forkType) + '</span>';
    }
    html += '<span class="meta-label">File scope</span><span class="mono">' + esc((wu.fileScope || []).join(', ') || '—') + '</span>';
    html += '<span class="meta-label">Depends on</span><span class="mono">' + esc((wu.dependsOn || []).join(', ') || '—') + '</span>';
    if (isBlocked(wu)) {
      html += '<span class="meta-label">Blocked</span><span>' + esc(wu.fanOutInfo.blockedReason) + '</span>';
    }
    html += '</div>';
    html += '<p>' + esc(wu.goal) + '</p>';
    if (wu.successCriteria) { html += '<p style="opacity:0.75"><em>' + esc(wu.successCriteria) + '</em></p>'; }
    if (state.selectedNodeEvidence && state.selectedNodeEvidence.length) {
      html += '<h2 style="margin-top:12px">Evidence</h2>';
      state.selectedNodeEvidence.forEach(function(ev) {
        var icon = ev.success ? '✅' : '❌';
        var summary = ev.summary || (ev.kind === 'Build'
          ? (ev.buildSystem || 'build') + ': ' + (ev.success ? 'passed' : 'failed (exit ' + (ev.exitCode || '?') + ')')
          : ev.kind === 'Test'
            ? (ev.buildSystem || 'test') + ': ' + (ev.success ? (ev.passed || 0) + '/' + (ev.totalTests || 0) + ' passed' : (ev.failed || 0) + ' failed')
            : ev.kind + ': ' + (ev.success ? 'ok' : 'fail'));
        html += '<div style="font-size:0.85em;padding:2px 0">' + icon + ' ' + esc(summary) + '</div>';
      });
    }
    if (state.reasoningGraph) {
      html += renderReasoningChain(state.reasoningGraph);
    }
    var statusLower = (wu.status || '').toLowerCase();
    var isRunning = statusLower === 'running' || statusLower === 'executing' || statusLower === 'active' || statusLower === 'queued' || statusLower === 'retrying';
    html += '<div class="inspector-actions">';
    html += '<button class="ghost" data-wu-action="spawnTask" data-wu="' + esc(wu.workUnitId) + '">Spawn Task</button>';
    html += '<button class="ghost" data-wu-action="forkHypothesis" data-wu="' + esc(wu.workUnitId) + '">Fork Hypothesis</button>';
    html += '<button class="ghost" data-wu-action="reexplore" data-wu="' + esc(wu.workUnitId) + '">Re-explore</button>';
    html += '<button class="ghost" data-wu-action="forkLatest" data-wu="' + esc(wu.workUnitId) + '">Fork from latest candidate</button>';
    html += '<button class="ghost" data-wu-action="forkKnownGood" data-wu="' + esc(wu.workUnitId) + '">Fork from Known Good</button>';
    // Slice 25c — Counterfactual: "Run with different model" for completed work units
    var isCompleted = statusLower === 'completed' || statusLower === 'merged';
    if (isCompleted) {
      html += '<button class="ghost" data-wu-action="counterfactual" data-wu="' + esc(wu.workUnitId) + '">↺ Run with different model</button>';
    }
    if (isRunning) {
      html += '<button class="ghost" data-wu-action="steerPause" data-wu="' + esc(wu.workUnitId) + '" data-agent="' + esc(wu.assignedAgent || '') + '" style="color:var(--nm-warn);border-color:var(--nm-warn)">⏸ Pause & Redirect</button>';
      html += '<button class="ghost" data-wu-action="steerForkFromNode" data-wu="' + esc(wu.workUnitId) + '">↳ Fork from here</button>';
    }
    // A Host restart interrupted this mid-execution, wiped the server's in-memory credential
    // cache, or the task is waiting on a file another sibling holds — a human can explicitly
    // resume any of these (no silent auto-resume for the first two). AwaitingFileLease normally
    // clears itself once the holder releases the file, but the flag and the file lease's own
    // state are tracked independently and can drift out of sync (e.g. a scoping change, or the
    // holder getting force-released some other way) — Resume here force-clears the park flag
    // unconditionally; if the wait is still real, the worker just re-parks itself on retry.
    if (wu.awaitingResume || wu.awaitingCredentials || wu.awaitingFileLease) {
      html += '<button class="ghost" data-wu-action="resumeWorker" data-wu="' + esc(wu.workUnitId) + '" style="color:var(--nm-warn);border-color:var(--nm-warn)">↺ Resume</button>';
    }
    if (statusLower === 'deadlettered') {
      html += '<div class="steer-retry-section" style="margin-top:8px;padding:8px;border:1px solid var(--nm-error);border-radius:4px">';
      html += '<div style="font-size:0.85em;opacity:0.8;margin-bottom:6px">🛠 Steer & Retry — correct the agent and optionally swap the model:</div>';
      html += '<textarea id="dl-steer-context-' + esc(wu.workUnitId) + '" rows="2" placeholder="e.g. the file lives at repo root, not under src/ — start the search there" style="width:100%;margin-bottom:6px"></textarea>';
      html += '<label style="display:flex;align-items:center;gap:4px;font-size:0.82em;margin-bottom:6px">';
      html += '<input type="checkbox" id="dl-use-new-profile-' + esc(wu.workUnitId) + '"/> Use new agent profile';
      html += '</label>';
      html += '<select id="dl-profile-select-' + esc(wu.workUnitId) + '" style="display:none;width:100%;margin-bottom:6px">';
      (state.agentProfiles || []).forEach(function(p) {
        html += '<option value="' + esc(p.id) + '">' + esc(p.label) + (p.model ? ' (' + esc(p.model) + ')' : '') + '</option>';
      });
      html += '</select>';
      html += '<button class="ghost" data-wu-action="steerDeadLetterRetrySend" data-wu="' + esc(wu.workUnitId) + '" style="color:var(--nm-error);border-color:var(--nm-error)">🛠 Retry</button>';
      // Continue only makes sense for a MaxIterationsExceeded failure — it resumes the same
      // work unit with its reconstructed conversation and a fresh iteration budget, rather than
      // starting over (Retry) or spawning fresh sibling slices (Re-plan). Rendered unconditionally
      // here, same as Retry above — the backend returns NotApplicable/400 for any other failure
      // kind and the panel surfaces that as an error toast rather than pre-fetching the entry just
      // to gate visibility.
      html += '<button class="ghost" data-wu-action="continueDeadLetter" data-wu="' + esc(wu.workUnitId) + '" title="Only applies to a \'max iterations reached\' failure — resumes this same work unit with its prior conversation and a fresh iteration budget.">▶ Continue</button>';
      html += '</div>';
    }
    html += '</div>';
    html += '</div>'; // end Metadata panel

    // ── Context panel ─────────────────────────────────────────────────────
    html += '<div class="gw-tab-panel' + (activeTab === 'context' ? ' active' : '') + '" id="gw-panel-context">';
    if (state.selectedNodeContext) {
      html += renderContextTab(state.selectedNodeContext);
    } else {
      html += '<p class="empty">Context loading…</p>';
    }
    html += '</div>'; // end Context panel

    // ── Conversation panel (Phase 11) ──────────────────────────────────────
    html += '<div class="gw-tab-panel' + (activeTab === 'conversation' ? ' active' : '') + '" id="gw-panel-conversation">';
    if (state.selectedNodeConversation) {
      html += renderConversationTab(state.selectedNodeConversation);
    } else {
      html += '<p class="empty">Conversation loading…</p>';
    }
    html += '</div>'; // end Conversation panel

    // ── Decision panel — the auto-jump target (Slice: keep other tabs reachable) ───────────
    if (candidate) {
      html += '<div class="gw-tab-panel' + (activeTab === 'decision' ? ' active' : '') + '" id="gw-panel-decision">';
      if (state.selectedNodeProposalDetail && state.selectedNodeProposalDetail.proposalId === candidate.artifactId) {
        html += renderProposalInspector(state.selectedNodeProposalDetail);
      } else {
        html += '<p class="empty">Loading…</p>';
      }
      html += '</div>'; // end Decision panel
    }

    return html;
  }

  // Phase 11 — same running-status check used for the steering buttons above; reused so the
  // Conversation tab's poll knows when to stop without recomputing the status string twice.
  function isWuRunning(wu) {
    var statusLower = (wu.status || '').toLowerCase();
    return statusLower === 'running' || statusLower === 'executing' || statusLower === 'active' ||
      statusLower === 'queued' || statusLower === 'retrying';
  }

  // Phase 11 — one row per logged cycle, newest first within each agent so the most recent
  // reasoning is visible without scrolling; tool calls/results render as collapsible blocks since
  // they can be large (workspace.read of a big file, etc.).
  function renderConversationTab(entries) {
    if (!entries || !entries.length) {
      return '<p class="empty">No conversation recorded yet for this decision node.</p>';
    }
    var totalIn = 0, totalOut = 0, haveTokens = false, anyEstimated = false;
    var modelsByRole = {};
    entries.forEach(function(e) {
      if (e.inputTokens != null) { totalIn += e.inputTokens; haveTokens = true; }
      if (e.outputTokens != null) { totalOut += e.outputTokens; haveTokens = true; }
      if (e.tokensEstimated && (e.inputTokens != null || e.outputTokens != null)) { anyEstimated = true; }
      if (e.model) {
        var key = e.agentRole + '|' + e.model + '|' + (e.provider || '');
        modelsByRole[key] = { role: e.agentRole, model: e.model, provider: e.provider };
      }
    });
    var html = '';
    if (haveTokens) {
      var totalTilde = anyEstimated ? '~' : '';
      var totalTitle = anyEstimated
        ? ' title="Includes one or more estimated counts (vscode-lm models don’t report real token usage; estimated via VS Code’s tokenizer, not the provider’s exact count)"'
        : '';
      html += '<div class="conv-token-total"' + totalTitle + ' style="font-size:0.8em;opacity:0.7;margin-bottom:4px">'
        + 'Tokens this run — ' + totalTilde + '↑' + totalIn.toLocaleString() + ' in / ' + totalTilde + '↓' + totalOut.toLocaleString() + ' out</div>';
    }
    var modelKeys = Object.keys(modelsByRole);
    if (modelKeys.length > 0) {
      html += '<div class="conv-model-summary" style="font-size:0.8em;opacity:0.7;margin-bottom:8px">Models this run — '
        + modelKeys.map(function(k) {
            var m = modelsByRole[k];
            return esc(m.role) + ': ' + esc(m.model) + (m.provider ? ' (' + esc(m.provider) + ')' : '');
          }).join(', ')
        + '</div>';
    }
    html += '<div id="conv-list">';
    entries.slice().reverse().forEach(function(e) {
      html += '<div class="conv-entry">';
      html += '<div class="conv-entry-head">';
      html += '<span class="badge">' + esc(e.agentRole) + '</span>';
      html += '<span class="mono" style="font-size:0.78em;opacity:0.6">' + esc(e.agentId) + '</span>';
      if (e.model) {
        html += '<span class="mono" style="font-size:0.78em;opacity:0.7">' + esc(e.model)
          + (e.provider ? ' (' + esc(e.provider) + ')' : '') + '</span>';
      }
      html += '<span style="font-size:0.78em;opacity:0.55">cycle ' + e.cycleNumber + '</span>';
      html += '<span style="font-size:0.72em;opacity:0.45">' + fmtTime(e.occurredAt) + '</span>';
      if (e.inputTokens != null || e.outputTokens != null) {
        var tilde = e.tokensEstimated ? '~' : '';
        var tokenTitle = e.tokensEstimated
          ? ' title="Estimated via VS Code’s tokenizer — vscode-lm models don’t report real token usage, so this is not the provider’s exact count"'
          : '';
        html += '<span style="font-size:0.72em;opacity:0.5"' + tokenTitle + '>' + tilde + '↑' + (e.inputTokens != null ? e.inputTokens.toLocaleString() : '—')
          + ' ' + tilde + '↓' + (e.outputTokens != null ? e.outputTokens.toLocaleString() : '—') + '</span>';
      }
      html += '</div>';
      if (e.assistantText) {
        html += '<div class="conv-text">' + esc(e.assistantText) + '</div>';
      }
      (e.toolCalls || []).forEach(function(call) {
        var result = (e.toolResults || []).find(function(r) { return r.toolUseId === call.toolUseId; });
        html += '<details class="conv-tool" data-tool-use-id="' + esc(call.toolUseId || '') + '">';
        html += '<summary>🔧 ' + esc(call.name) + '</summary>';
        html += '<div class="conv-tool-label">Input</div>';
        html += '<pre class="conv-pre">' + esc(call.inputJson) + '</pre>';
        if (result) {
          html += '<div class="conv-tool-label">Result' + (result.truncated ? ' (truncated)' : '') + '</div>';
          html += '<pre class="conv-pre">' + esc(result.result) + '</pre>';
        }
        html += '</details>';
      });
      html += '</div>';
    });
    html += '</div>';
    return html;
  }

  // ── Slice 24b — Context tab ────────────────────────────────────────────

  function stopConversationPoll() {
    if (state.conversationPollTimer) { clearInterval(state.conversationPollTimer); state.conversationPollTimer = null; }
  }

  // Phase 11 — only polls while the Conversation tab is the one visible and its work unit is
  // still running; reuses the 2s cadence already used everywhere else in this panel (tree/session
  // polling) rather than introducing a faster or push-based mechanism.
  function startConversationPoll(workUnitId) {
    stopConversationPoll();
    state.conversationPollTimer = setInterval(function() {
      var wu = state.decisionNodes.find(function(w) { return w.workUnitId === workUnitId; });
      if (!wu || !isWuRunning(wu) || state.selectedNodeId !== workUnitId) { stopConversationPoll(); return; }
      vscode.postMessage({ type: 'explorerSelectConversationTab', workUnitId: workUnitId });
    }, 2000);
  }

  function bindTabBarClick() {
    root.querySelectorAll('.gw-tab-btn').forEach(function(btn) {
      btn.addEventListener('click', function() {
        var tab = btn.getAttribute('data-gw-tab');
        root.querySelectorAll('.gw-tab-btn').forEach(function(b) { b.classList.remove('active'); });
        root.querySelectorAll('.gw-tab-panel').forEach(function(p) { p.classList.remove('active'); });
        btn.classList.add('active');
        var panel = $('gw-panel-' + tab);
        if (panel) { panel.classList.add('active'); }

        // If Context tab is selected and we have no data yet, request it
        if (tab === 'context' && !state.selectedNodeContext && state.selectedNodeId) {
          $('gw-panel-context').innerHTML = '<p class="empty">Loading…</p>';
          vscode.postMessage({ type: 'explorerSelectContextTab', workUnitId: state.selectedNodeId });
        }

        // Phase 11 — Conversation tab: fetch on first view, then poll while the work unit runs.
        if (tab === 'conversation' && state.selectedNodeId) {
          if (!state.selectedNodeConversation) {
            $('gw-panel-conversation').innerHTML = '<p class="empty">Loading…</p>';
          }
          vscode.postMessage({ type: 'explorerSelectConversationTab', workUnitId: state.selectedNodeId });
          var wu = state.decisionNodes.find(function(w) { return w.workUnitId === state.selectedNodeId; });
          if (wu && isWuRunning(wu)) { startConversationPoll(state.selectedNodeId); }
        } else {
          stopConversationPoll();
        }

        // Decision tab: fetch the candidate's full detail on first view (or if a different
        // proposal is now the tab's target than what's cached).
        if (tab === 'decision') {
          var proposalId = btn.getAttribute('data-proposal-id');
          if (proposalId && (!state.selectedNodeProposalDetail || state.selectedNodeProposalDetail.proposalId !== proposalId)) {
            $('gw-panel-decision').innerHTML = '<p class="empty">Loading…</p>';
            vscode.postMessage({ type: 'explorerSelectProposal', proposalId: proposalId });
          }
        }
      });
    });
  }

  function renderContextTab(context) {
    if (!context) { return '<p class="empty">No context data available for this decision node.</p>'; }
    var html = '';

    // Goal
    html += '<div class="ctx-section">';
    html += '<h3>Goal</h3>';
    html += '<div class="ctx-item">' + esc(context.goal) + '</div>';
    html += '</div>';

    // Plan
    if (context.plan && context.plan.length) {
      html += '<div class="ctx-section">';
      html += '<h3>Plan</h3>';
      context.plan.forEach(function(slice) {
        html += '<div class="ctx-plan-entry">';
        html += '<div class="ctx-plan-slice mono">' + esc(slice.sliceId) + '</div>';
        html += '<div class="ctx-plan-goal">' + esc(slice.goal) + '</div>';
        if (slice.fileScope && slice.fileScope.length) {
          html += '<div class="ctx-item" style="opacity:0.5;font-size:0.76em">📁 ' + esc(slice.fileScope.join(', ')) + '</div>';
        }
        if (slice.steps && slice.steps.length) {
          html += '<ol class="ctx-plan-steps">';
          slice.steps.forEach(function(step) { html += '<li>' + esc(step) + '</li>'; });
          html += '</ol>';
        }
        html += '</div>';
      });
      html += '</div>';
    }

    // Assumptions
    if (context.assumptions && context.assumptions.length) {
      html += '<div class="ctx-section">';
      html += '<h3>Assumptions</h3>';
      context.assumptions.forEach(function(a) {
        html += '<div class="ctx-item">• ' + esc(a) + '</div>';
      });
      html += '</div>';
    }

    // Constraints
    if (context.constraints && context.constraints.length) {
      html += '<div class="ctx-section">';
      html += '<h3>Constraints</h3>';
      context.constraints.forEach(function(c) {
        html += '<div class="ctx-item">🔒 ' + esc(c) + '</div>';
      });
      html += '</div>';
    }

    // Evidence
    if (context.evidence && context.evidence.length) {
      html += '<div class="ctx-section">';
      html += '<h3>Evidence</h3>';
      context.evidence.forEach(function(ev) {
        var icon = ev.success ? '✅' : '❌';
        var cls = ev.success ? 'success' : 'fail';
        html += '<div class="ctx-evidence ' + cls + '">' + icon + ' ' + esc(ev.summary) + '</div>';
      });
      html += '</div>';
    }

    // Execution results
    if (context.execution) {
      html += '<div class="ctx-section">';
      html += '<h3>Execution Results</h3>';
      html += '<div class="ctx-item">' + (context.execution.allSucceeded ? '✅ All passed' : '❌ Some failed') + '</div>';
      if (context.execution.buildSystems && context.execution.buildSystems.length) {
        html += '<div class="ctx-item mono">Build systems: ' + esc(context.execution.buildSystems.join(', ')) + '</div>';
      }
      if (context.execution.testSummary) {
        html += '<div class="ctx-item mono">' + esc(context.execution.testSummary) + '</div>';
      }
      if (context.execution.executedAt) {
        html += '<div class="ctx-item" style="font-size:0.72em;opacity:0.5">' + esc(fmtTime(context.execution.executedAt)) + '</div>';
      }
      html += '</div>';
    }

    // Allowed Tools
    if (context.allowedTools && context.allowedTools.length) {
      html += '<div class="ctx-section">';
      html += '<h3>Allowed Tools</h3>';
      html += '<div class="ctx-item mono">' + esc(context.allowedTools.join(', ')) + '</div>';
      html += '</div>';
    }

    // Model info
    if (context.agentModel) {
      html += '<div class="ctx-section">';
      html += '<h3>Model</h3>';
      html += '<div class="ctx-item mono">' + esc(context.agentModel) + (context.agentProvider ? ' @ ' + esc(context.agentProvider) : '') + '</div>';
      html += '</div>';
    }

    // Steered-from indicator
    if (context.steeredFromDecisionId) {
      html += '<div class="ctx-section">';
      html += '<h3>Steering</h3>';
      html += '<div class="ctx-item mono">↳ Steered from decision ' + esc(context.steeredFromDecisionId) + '</div>';
      html += '</div>';
    }

    // Copy as Markdown button
    html += '<div class="inspector-actions">';
    html += '<button class="ghost ctx-copy-btn" id="ctx-copy-markdown">📋 Copy as Markdown</button>';
    html += '</div>';

    return html;
  }

  function bindContextCopyButton() {
    var btn = $('ctx-copy-markdown');
    if (!btn) { return; }
    btn.addEventListener('click', function() {
      var ctx = state.selectedNodeContext;
      if (!ctx) { return; }
      var md = '## Decision Context\n\n';
      md += '**Goal:** ' + ctx.goal + '\n\n';
      if (ctx.plan && ctx.plan.length) {
        md += '### Plan\n';
        ctx.plan.forEach(function(s) {
          md += '- **' + s.sliceId + ':** ' + s.goal + '\n';
          if (s.steps && s.steps.length) { s.steps.forEach(function(st) { md += '  1. ' + st + '\n'; }); }
        });
        md += '\n';
      }
      if (ctx.assumptions && ctx.assumptions.length) {
        md += '### Assumptions\n';
        ctx.assumptions.forEach(function(a) { md += '- ' + a + '\n'; });
        md += '\n';
      }
      if (ctx.constraints && ctx.constraints.length) {
        md += '### Constraints\n';
        ctx.constraints.forEach(function(c) { md += '- ' + c + '\n'; });
        md += '\n';
      }
      if (ctx.evidence && ctx.evidence.length) {
        md += '### Evidence\n';
        ctx.evidence.forEach(function(ev) { md += '- ' + (ev.success ? '✅' : '❌') + ' ' + ev.summary + '\n'; });
        md += '\n';
      }
      if (ctx.allowedTools && ctx.allowedTools.length) {
        md += '### Allowed Tools\n';
        md += ctx.allowedTools.join(', ') + '\n\n';
      }
      if (ctx.agentModel) {
        md += '**Model:** ' + ctx.agentModel + (ctx.agentProvider ? ' @ ' + ctx.agentProvider : '') + '\n\n';
      }
      if (ctx.steeredFromDecisionId) {
        md += '**Steered from:** ' + ctx.steeredFromDecisionId + '\n\n';
      }
      navigator.clipboard.writeText(md).then(function() {
        var btn = $('ctx-copy-markdown');
        if (btn) { btn.textContent = '✓ Copied!'; setTimeout(function() { if (btn) { btn.textContent = '📋 Copy as Markdown'; } }, 1500); }
      }).catch(function() {
        var btn = $('ctx-copy-markdown');
        if (btn) { btn.textContent = '⚠ Copy failed'; }
      });
    });
  }

  function bindDecisionInspectorTabs() {
    bindTabBarClick();
    bindWorkUnitActionButtons();
    bindContextCopyButton();
    // The Decision tab panel may already have cached proposal detail rendered into it (e.g. on
    // re-selecting a node whose candidate was already fetched) — wire its buttons too.
    bindProposalActionButtons();
  }

  // ── Timeline ─────────────────────────────────────────────────────────────

  // Picks which Decision Candidate (MergeProposal artifact) a newly-selected Decision Tree node
  // should jump straight to, instead of making the user scroll the timeline, click the candidate,
  // then click "Open in Review" as three separate steps. Prefers whichever candidate still needs a
  // decision (not yet Merged/Rejected/Superseded); if several are pending, the most recent one. If
  // none are pending, falls through to null so the caller keeps today's default node-level view.
  function findDefaultProposalCandidate(artifacts) {
    var proposals = (artifacts || []).filter(function(a) { return a.type === 'MergeProposal'; });
    if (!proposals.length) { return null; }
    var pending = proposals.filter(function(a) {
      return a.status !== 'Merged' && a.status !== 'Rejected' && a.status !== 'Superseded';
    });
    var pool = pending.length ? pending : [];
    if (!pool.length) { return null; }
    pool.sort(function(a, b) { return new Date(a.createdAt) - new Date(b.createdAt); });
    return pool[pool.length - 1];
  }

  function renderTimeline(artifacts, events) {
    state.timelineArtifacts = artifacts || [];
    state.timelineEvents = events || [];
    var el = $('gw-timeline');
    var rows = [];
    (artifacts || []).forEach(function(a) {
      rows.push({ sortKey: a.createdAt, kind: 'artifact', data: a });
    });
    (events || []).forEach(function(e) {
      rows.push({ sortKey: e.occurredAt, kind: 'event', data: e });
    });
    rows.sort(function(a, b) { return new Date(a.sortKey) - new Date(b.sortKey); });
    if (!rows.length) {
      el.innerHTML = '<p class="empty">No artifacts yet for this decision node.</p>';
      return;
    }
    var html = '';
    rows.forEach(function(row) {
      if (row.kind === 'artifact') {
        var a = row.data;
        var classified = classifyArtifact(a.type);
        var clickable = a.type === 'MergeProposal';
        html += '<div class="tl-item' + (clickable ? ' clickable' : '') + '"' +
          (clickable ? ' data-proposal="' + esc(a.artifactId) + '"' : '') + '>';
        html += '<span class="tl-time">' + fmtTime(a.createdAt) + '</span>';
        html += '<div class="tl-kind">' + classified.icon + ' ' + classified.label + '</div>';
        html += '<div class="tl-title">' + esc(a.title || a.artifactId) + ' ' + badge(a.status) + '</div>';
        if (a.body) { html += '<details><summary style="cursor:pointer;opacity:0.7;font-size:0.85em">details</summary><pre class="snapshot">' + esc(a.body) + '</pre></details>'; }
        html += '</div>';
      } else {
        var e = row.data;
        html += '<div class="tl-item clickable" data-event="' + esc(e.eventId) + '">';
        html += '<span class="tl-time">' + fmtTime(e.occurredAt) + '</span>';
        html += '<div class="tl-kind">🤖 Agent Action</div>';
        html += '<div class="tl-title">' + esc(e.inputStage) + ' &rarr; ' + esc(e.action) + '</div>';
        html += '</div>';
      }
    });
    el.innerHTML = html;
    el.querySelectorAll('[data-proposal]').forEach(function(node) {
      node.addEventListener('click', function() {
        var id = node.getAttribute('data-proposal');
        el.querySelectorAll('.tl-selected').forEach(function(n) { n.classList.remove('tl-selected'); });
        node.classList.add('tl-selected');
        var clickedArtifact = (state.timelineArtifacts || []).find(function(a) { return a.artifactId === id; });
        var wu = state.decisionNodes.find(function(w) { return w.workUnitId === state.selectedNodeId; });
        if (wu) {
          // Route through the tabbed inspector (Decision tab) rather than replacing it outright,
          // so Metadata/Context/Conversation stay reachable for whichever proposal is being viewed.
          if (!state.selectedNodeProposalDetail || state.selectedNodeProposalDetail.proposalId !== id) {
            state.selectedNodeProposalDetail = null;
          }
          $('gw-inspector').innerHTML = renderDecisionInspector(wu, { candidate: clickedArtifact || { artifactId: id }, activeTab: 'decision' });
          bindDecisionInspectorTabs();
        } else {
          $('gw-inspector').innerHTML = '<p class="empty">Loading…</p>';
        }
        if (!state.selectedNodeProposalDetail || state.selectedNodeProposalDetail.proposalId !== id) {
          vscode.postMessage({ type: 'explorerSelectProposal', proposalId: id });
        }
      });
    });
    el.querySelectorAll('[data-event]').forEach(function(node) {
      node.addEventListener('click', function() {
        var id = node.getAttribute('data-event');
        var e = state.timelineEvents.find(function(x) { return x.eventId === id; });
        if (e) { $('gw-inspector').innerHTML = renderEventInspector(e); }
      });
    });
  }

  function renderEventInspector(e) {
    var html = '<div class="meta-grid">';
    html += '<span class="meta-label">Stage</span><span>' + esc(e.inputStage) + '</span>';
    html += '<span class="meta-label">Action</span><span>' + esc(e.action) + '</span>';
    html += '<span class="meta-label">Orchestrator</span><span class="mono">' + esc(e.orchestratorAgentId) + '</span>';
    html += '<span class="meta-label">Spawned</span><span class="mono">' + esc((e.spawnedIds || []).join(', ') || '—') + '</span>';
    html += '</div>';
    if (e.reason) { html += '<p>' + esc(e.reason) + '</p>'; }
    html += '<h2 style="margin-top:14px">Input projection snapshot</h2>';
    var pretty = e.inputProjectionSnapshot;
    try { pretty = JSON.stringify(JSON.parse(e.inputProjectionSnapshot), null, 2); } catch (err) {}
    html += '<pre class="snapshot">' + esc(pretty) + '</pre>';
    return html;
  }

  // ── Proposal inspector ───────────────────────────────────────────────────

  function renderProposalInspector(proposal) {
    var html = '<div class="meta-grid">';
    html += '<span class="meta-label">Decision Status</span>' + badge(proposal.status);
    html += '<span class="meta-label">Source</span><span class="mono">' + esc(proposal.sourceBranch) + '</span>';
    html += '<span class="meta-label">Confidence</span><span>' + (proposal.confidence != null ? Math.round(proposal.confidence * 100) + '%' : '—') + '</span>';
    html += '<span class="meta-label">Files touched</span><span>' + ((proposal.filesTouched || []).length) + '</span>';
    html += '</div>';
    html += '<p>' + esc(proposal.goal) + '</p>';

    var others = state.timelineArtifacts
      .filter(function(a) { return a.type === 'MergeProposal' && a.artifactId !== proposal.proposalId; })
      .map(function(a) { return { proposalId: a.artifactId, title: a.title }; });
    window.__nmCandidates = others;
    window.__nmProposalId = proposal.proposalId;

    html += '<div class="inspector-actions">';
    html += '<button data-p-action="openReview">Open in Review &rarr;</button>';
    html += '<button class="ghost" data-p-action="forkHypothesis">Fork Hypothesis from here</button>';
    html += '<button class="ghost" data-p-action="restore">Restore workspace</button>';
    html += '<button class="ghost" data-p-action="compare">Compare with…</button>';
    html += '</div>';
    html += '<div id="gw-compare-result"></div>';
    return html;
  }

  function bindProposalActionButtons() {
    root.querySelectorAll('[data-p-action]').forEach(function(btn) {
      btn.addEventListener('click', function() {
        vscode.postMessage({
          type: 'explorerProposalAction',
          action: btn.getAttribute('data-p-action'),
          proposalId: window.__nmProposalId,
          candidates: window.__nmCandidates || [],
        });
      });
    });
  }

  function renderDiffText(text) {
    return String(text || '').split('\n').map(function(line) {
      var cls = line.startsWith('+') ? 'diff-add' : line.startsWith('-') ? 'diff-del' : '';
      return cls ? '<span class="' + cls + '">' + esc(line) + '</span>' : esc(line);
    }).join('\n');
  }

  function renderCompareResult(result) {
    var el = $('gw-compare-result');
    if (!el) { return; }
    var html = '<h2 style="margin-top:14px">Compare</h2>';
    html += '<p class="mono">overlapping files: ' + ((result.overlappingFiles || []).join(', ') || 'none') + '</p>';
    html += '<div style="display:grid;grid-template-columns:1fr 1fr;gap:8px">';
    html += '<pre class="diff-pre">' + renderDiffText(result.diffA) + '</pre>';
    html += '<pre class="diff-pre">' + renderDiffText(result.diffB) + '</pre>';
    html += '</div>';
    el.innerHTML = html;
  }

  // ── Slice 22c — Compare Results side-by-side view ──────────────────────

  function renderCompareResults(children, parentId) {
    var profiles = state.agentProfiles || [];
    // Comparison engine — deterministic evidence/score per sibling, fetched async via the
    // HypothesisComparison projection. May not have arrived yet on first render; that's fine,
    // the raw side-by-side view above still works without it.
    var comparison = (state.__comparisonParentId === parentId) ? state.__comparisonData : null;
    var siblingsByWu = {};
    if (comparison && comparison.siblings) {
      comparison.siblings.forEach(function(s) { siblingsByWu[s.workUnitId] = s; });
    } else {
      vscode.postMessage({ type: 'explorerLoadComparison', parentId: parentId });
    }
    var html = '<div class="cmp-results"><div class="cmp-header">';
    html += '<h2>Fork Comparison</h2>';
    html += '<span class="mono" style="font-size:0.72em">' + children.length + ' forks</span>';
    html += '</div>';
    if (comparison && comparison.recommendedWorkUnitId) {
      html += '<p style="font-size:0.78em;opacity:0.75">Evidence-based recommendation: <strong>' +
        esc(comparison.recommendedWorkUnitId) + '</strong> (deterministic score, not a decision — pick the winner yourself below)</p>';
    }
    html += '<div class="cmp-fork-cards">';
    children.forEach(function(child, i) {
      var profile = profiles.find(function(p) { return p.id === child.owner; }) || {};
      var modelLabel = profile.model || profile.label || child.owner || 'Fork ' + (i + 1);
      var won = state.__compareWinner === child.workUnitId;
      var lost = state.__compareLosers && state.__compareLosers.indexOf(child.workUnitId) >= 0;
      var cls = won ? ' selected' : (lost ? ' rejected' : '');
      var sibling = siblingsByWu[child.workUnitId];
      html += '<div class="cmp-fork-card' + cls + '" data-cmp-wu="' + esc(child.workUnitId) + '">';
      html += '<div class="cmp-fk-model">🔀 ' + esc(modelLabel) + '</div>';
      html += '<div class="cmp-fk-goal">' + esc((child.goal || '').substring(0, 120)) + '</div>';
      html += '<div class="cmp-fk-meta">';
      html += badge(child.status);
      if (child.forkType && child.forkType.toLowerCase() !== 'unknown') {
        html += '<span class="badge fork-type">' + esc(child.forkType) + '</span>';
      }
      html += '<span class="mono">' + (child.proposalCount || 0) + ' proposals</span>';
      if (sibling) {
        html += '<span class="mono" title="' + esc((sibling.evidenceSummaries || []).join(' | ')) + '">score: ' +
          sibling.score.toFixed(1) + ' (' + sibling.evidenceCount + ' evidence)</span>';
      }
      if (won) { html += '<span class="badge completed">★ Winner</span>'; }
      html += '</div></div>';
    });
    html += '</div>';
    if (!state.__compareWinner) {
      html += '<div class="cmp-pick-bar">';
      html += '<span style="font-size:0.78em;opacity:0.6">Select a fork then click Pick Winner:</span>';
      html += '<button class="pick-winner" id="gw-pick-winner" disabled>Pick Winner</button>';
      html += '</div>';
    } else {
      html += '<div class="cmp-pick-bar">';
      html += '<span style="font-size:0.78em;color:var(--nm-success)">✔ Winner selected: ' + esc(state.__compareWinnerLabel || state.__compareWinner) + '</span>';
      html += '<button class="ghost" id="gw-reset-compare" style="font-size:0.74em">Reset</button>';
      html += '</div>';
    }
    html += '<div class="inspector-actions" style="margin-top:8px">';
    html += '<button class="ghost" id="gw-compare-open-latest" style="font-size:0.8em">📋 View proposals</button>';
    html += '</div>';
    html += '</div>';
    return html;
  }

  // ── Slice 25c — Counterfactual: original vs. counterfactual comparison ─
  function renderCounterfactualComparison(comparison) {
    if (!comparison) { return '<p class="empty">No comparison data available for this counterfactual.</p>'; }

    function renderSide(label, model, provider, proposals) {
      var html = '<div class="cmp-fork-card">';
      html += '<div class="cmp-fk-model">🔀 ' + esc(label) + ': ' + esc(model || provider || 'unknown') + '</div>';
      (proposals || []).forEach(function(p) {
        html += '<div class="cmp-fk-goal">' + esc((p.goal || '').substring(0, 120)) + '</div>';
        html += '<div class="cmp-fk-meta">';
        html += badge(p.status);
        if (typeof p.confidence === 'number') {
          html += '<span class="mono">confidence: ' + Math.round(p.confidence * 100) + '%</span>';
        }
        html += '<span class="mono">' + (p.filesTouched || []).length + ' files</span>';
        html += '</div>';
        if (p.diffSummary) {
          html += '<div class="cmp-fk-goal" style="opacity:0.6">' + esc(p.diffSummary.substring(0, 200)) + '</div>';
        }
      });
      html += '</div>';
      return html;
    }

    var htmlOut = '<div class="cmp-results"><div class="cmp-header">';
    htmlOut += '<h2>Counterfactual Comparison</h2>';
    htmlOut += '</div>';
    htmlOut += '<div class="cmp-fork-cards">';
    htmlOut += renderSide('Original', comparison.originalModel, comparison.originalProvider, comparison.originals);
    htmlOut += renderSide('Counterfactual', comparison.counterfactualModel, comparison.counterfactualProvider, comparison.counterfactuals);
    htmlOut += '</div>';
    if (comparison.whichWasBetter) {
      htmlOut += '<div class="cmp-pick-bar"><span style="font-size:0.78em;color:var(--nm-success)">Which was better: ' + esc(comparison.whichWasBetter) + '</span></div>';
    }
    htmlOut += '</div>';
    return htmlOut;
  }

  function bindCompareResultsButtons() {
    var children = state.__compareChildren || [];
    var pickBtn = $('gw-pick-winner');
    var openBtn = $('gw-compare-open-latest');
    var resetBtn = $('gw-reset-compare');

    // Card click to select
    root.querySelectorAll('.cmp-fork-card').forEach(function(card) {
      card.addEventListener('click', function() {
        var wuId = card.getAttribute('data-cmp-wu');
        if (!wuId) { return; }
        state.__comparePendingPick = wuId;
        root.querySelectorAll('.cmp-fork-card').forEach(function(c) { c.classList.remove('selected'); });
        card.classList.add('selected');
        if (pickBtn) {
          pickBtn.disabled = false;
          pickBtn.textContent = 'Pick Winner: ' + esc(state.__comparePendingPickLabel || wuId);
        }
      });
    });

    if (pickBtn) {
      pickBtn.addEventListener('click', function() {
        var winnerId = state.__comparePendingPick;
        if (!winnerId) { return; }
        var winnerWU = children.find(function(c) { return c.workUnitId === winnerId; });
        state.__compareWinner = winnerId;
        state.__compareWinnerLabel = winnerWU ? (winnerWU.owner || winnerWU.goal || winnerId) : winnerId;
        state.__compareLosers = children.filter(function(c) { return c.workUnitId !== winnerId; }).map(function(c) { return c.workUnitId; });
        // Send pick winner action to extension host
        vscode.postMessage({
          type: 'explorerPickWinner',
          winnerId: winnerId,
          parentId: state.__compareParentId || '',
        });
        // Re-render
        $('gw-inspector').innerHTML = renderCompareResults(children, state.__compareParentId);
        bindCompareResultsButtons();
      });
    }

    if (resetBtn) {
      resetBtn.addEventListener('click', function() {
        state.__compareWinner = null;
        state.__compareWinnerLabel = null;
        state.__compareLosers = null;
        state.__comparePendingPick = null;
        $('gw-inspector').innerHTML = renderCompareResults(children, state.__compareParentId);
        bindCompareResultsButtons();
      });
    }

    if (openBtn) {
      openBtn.addEventListener('click', function() {
        var firstChild = children[0];
        if (firstChild) {
          state.selectedNodeId = firstChild.workUnitId;
          renderDecisionTree(state.decisionNodes);
          vscode.postMessage({ type: 'explorerSelectWorkUnit', workUnitId: firstChild.workUnitId });
        }
      });
    }
  }

  // ── Slice 18f — Reasoning Chain vertical timeline ───────────────────────

  function renderReasoningChain(graph) {
    if (!graph || !graph.nodes || !graph.nodes.length) { return ''; }
    var nodes = graph.nodes;
    var edges = graph.edges || [];
    // Build lookup: commitId → list of edge labels for that node
    var edgeLabelsByNode = {};
    edges.forEach(function(e) {
      var labels = edgeLabelsByNode[e.fromCommitId] || [];
      if (labels.indexOf(e.edgeType) === -1) { labels.push(e.edgeType); }
      edgeLabelsByNode[e.fromCommitId] = labels;
      // Also tag the target with an incoming marker
      var toLabels = edgeLabelsByNode[e.toCommitId] || [];
      var incoming = '←' + e.edgeType;
      if (toLabels.indexOf(incoming) === -1) { toLabels.push(incoming); }
      edgeLabelsByNode[e.toCommitId] = toLabels;
    });

    // Only show nodes for the currently selected work unit
    var filtered = nodes.filter(function(n) { return n.workUnitId === (state.selectedNodeId || ''); });
    if (!filtered.length) { return ''; }
    filtered.sort(function(a, b) { return new Date(a.occurredAt) - new Date(b.occurredAt); });

    var html = '<div class="rc-chain"><h2>Reasoning Chain</h2>';
    filtered.forEach(function(node) {
      var labels = edgeLabelsByNode[node.commitId] || [];
      var labelHtml = labels.map(function(l) {
        var cls = l.toLowerCase().replace(/[^a-z]/g, '');
        return '<span class="rc-edge-badge ' + cls + '">' + esc(l) + '</span>';
      }).join('');

      var modelStr = node.agentModel || node.agentProvider || '';
      if (modelStr && node.agentModel && node.agentProvider) { modelStr = node.agentModel + ' @ ' + node.agentProvider; }

      var reasoningExcerpt = node.reasoning || '';
      if (reasoningExcerpt.length > 100) { reasoningExcerpt = reasoningExcerpt.substring(0, 100) + '…'; }

      html += '<div class="rc-node" data-rc-commit="' + esc(node.commitId) + '">';
      html += '<div class="rc-dot"></div>';
      html += '<div class="rc-card">';
      html += '<div class="rc-header">';
      html += stageBadge(node.stage);
      html += '<span class="badge">' + esc(node.action) + '</span>';
      html += labelHtml;
      html += '</div>';
      if (reasoningExcerpt) { html += '<div class="rc-body">' + esc(reasoningExcerpt) + '</div>'; }
      html += '<div class="rc-footer">';
      html += '<span>' + fmtTime(node.occurredAt) + '</span>';
      html += '<span class="mono">' + esc(node.agentId || '') + (modelStr ? ' · ' + esc(modelStr) : '') + '</span>';
      html += '</div>';
      html += '</div></div>';
    });
    html += '</div>';
    return html;
  }

  // ── Messages from extension host ────────────────────────────────────────

  window.addEventListener('message', function(event) {
    var msg = event.data;
    if (msg.type === 'explorerWsInit') {
      connectStageSocket(msg.wsUrl);
      return;
    }
    if (msg.type === 'explorerReferenceAdded') {
      state.referenceFiles = state.referenceFiles || [];
      state.referenceFiles.push({ repositoryId: msg.repositoryId, repositoryLabel: msg.repositoryLabel, path: msg.path });
      renderReferenceChips();
      return;
    }
    if (msg.type === 'strategies') {
      // Slice 22c — store profiles for fork config
      if (msg.profiles) { state.agentProfiles = msg.profiles || []; }
      var sel = $('gw-strategy');
      sel.innerHTML = (msg.strategies || []).map(function(t) {
        var disabled = t.disabled ? ' disabled' : '';
        var title = t.tooltip ? ' title="' + esc(t.tooltip) + '"' : '';
        return '<option value="' + esc(t.name) + '"' + disabled + title + '>' + esc(t.name) + '</option>';
      }).join('');
      // Trigger fork config panel visibility if current selection is experiment
      var currentVal = sel.value;
      var panel = $('gw-fork-config');
      if (currentVal === 'Multi-Model Comparison' || currentVal === 'Architecture Fork' || currentVal === 'Library Comparison' || currentVal === 'Product Strategy Fork') {
        panel.classList.add('visible');
        if ((!state.forkConfig || !state.forkConfig.length) && state.agentProfiles) {
          state.forkConfig = buildDefaultForkConfig(currentVal);
        }
        renderForkConfigPanel(state.forkConfig || buildDefaultForkConfig(currentVal));
      } else {
        panel.classList.remove('visible');
      }
      // Seed the Task/Workspace Review radios from the session defaults — one-time seed for the
      // "new goal" form, not a live binding to already-created goals (this panel only ever holds
      // form state for the goal about to be created via gw-run).
      seedReviewPolicyDefaults(msg.defaultTaskReviewPolicy, msg.defaultWorkspaceReviewPolicy);
      return;
    }
    if (msg.type === 'sessions') {
      var sel2 = $('gw-session');
      state.__sessions = msg.sessions || [];
      var options = '<option value="">(no exploration)</option>' + state.__sessions.map(function(s) {
        var paused = s.status === 'Paused' ? ' ⏸' : '';
        return '<option value="' + esc(s.sessionId) + '">' + esc(s.sessionId) + ' — ' + esc(s.status) + paused + '</option>';
      }).join('');
      sel2.innerHTML = options;
      sel2.value = msg.selectedSessionId || '';
      state.selectedSessionId = msg.selectedSessionId || '';
      updateSessionControls(state.selectedSessionId, state.__sessions);
      return;
    }
    if (msg.type === 'comparisonData') {
      state.__comparisonData = msg.payload;
      state.__comparisonParentId = msg.parentId;
      if (state.__compareParentId === msg.parentId) {
        $('gw-inspector').innerHTML = renderCompareResults(state.__compareChildren, msg.parentId);
        bindCompareResultsButtons();
      }
      return;
    }
    if (msg.type === 'tree') {
      // A poll for the previous session can still be in flight when the user starts a fresh
      // one; without this check whichever response lands last wins, regardless of which
      // session is actually selected now, so a stale poll can clobber the new tree with the
      // old (possibly still-in-progress) session's work units.
      if ((msg.sessionId || '') !== state.selectedSessionId) { return; }
      // Poll-driven innerHTML rebuild — don't destroy a selection the user is copying out
      // of the tree. Selections elsewhere (inspector, conversation) are unaffected by this
      // rebuild, so only the tree's own subtree is checked.
      if (hasSelectionWithin($('gw-tree'))) { return; }
      state.reviewingWorkUnitIds = msg.reviewingWorkUnitIds || [];
      state.planningWorkUnitIds = msg.planningWorkUnitIds || [];
      renderDecisionTree(msg.workUnits);
      return;
    }
    if (msg.type === 'timeline') {
      // Same stale-response race as the tree message: clicking a second node before the
      // first one's fetch resolves means an older, slower response can land after the
      // newer one and overwrite it — or land for a node that's no longer selected at all,
      // which used to render anyway since this had no workUnitId check.
      if (msg.workUnitId !== state.selectedNodeId) { return; }
      // Same selection guard as the tree — this handler rewrites both the timeline column
      // and (below) the inspector, so protect a selection anchored in either.
      if (hasSelectionWithin($('gw-timeline')) || hasSelectionWithin($('gw-inspector'))) { return; }
      renderTimeline(msg.artifacts, msg.events);
      // Slice 18c — store evidence and re-render inspector if node is still selected
      if (msg.evidence) {
        state.selectedNodeEvidence = msg.evidence || [];
      }
      // Slice 18f — store reasoning graph and re-render inspector
      if (msg.reasoningGraph) {
        state.reasoningGraph = msg.reasoningGraph;
      }
      var wu = state.decisionNodes.find(function(w) { return w.workUnitId === state.selectedNodeId; });
      var defaultCandidate = findDefaultProposalCandidate(msg.artifacts);
      // Jump straight to the Decision tab the first time this node shows a pending candidate —
      // but only once per selection, and as a tab within the normal inspector rather than a
      // full replacement, so Metadata/Context/Conversation stay one click away afterwards
      // instead of getting stranded behind the auto-jump (see goal workspace decision-lens fix).
      var autoSelectDecision = !!defaultCandidate && state.autoJumpedForNodeId !== state.selectedNodeId;
      if (defaultCandidate) {
        var candidateNode = root.querySelector('[data-proposal="' + defaultCandidate.artifactId + '"]');
        if (candidateNode) { candidateNode.classList.add('tl-selected'); }
      }
      if (wu) {
        $('gw-inspector').innerHTML = renderDecisionInspector(wu, {
          candidate: defaultCandidate,
          activeTab: autoSelectDecision ? 'decision' : 'metadata',
        });
        bindDecisionInspectorTabs();
      }
      if (autoSelectDecision) {
        state.autoJumpedForNodeId = state.selectedNodeId;
        vscode.postMessage({ type: 'explorerSelectProposal', proposalId: defaultCandidate.artifactId });
      }
      return;
    }
    if (msg.type === 'proposal') {
      state.selectedNodeProposalDetail = msg.proposal;
      var decisionPanel = $('gw-panel-decision');
      if (decisionPanel) {
        decisionPanel.innerHTML = renderProposalInspector(msg.proposal);
        bindProposalActionButtons();
      } else {
        // Fallback for any flow that doesn't have the tabbed inspector mounted.
        $('gw-inspector').innerHTML = renderProposalInspector(msg.proposal);
        bindProposalActionButtons();
      }
      return;
    }
    if (msg.type === 'compareResult') {
      renderCompareResult(msg.result);
      return;
    }
    // Items 1+2 (R6) — the replication room this repository is bound to. Three distinct states, and
    // the difference matters when work is not showing up: not registered at all, registered but not
    // bound to any room yet, or bound (show which).
    if (msg.type === 'explorerRepositoryRoom') {
      var roomEl = $('gw-repo-room');
      if (roomEl) {
        var unbound = false;
        if (!msg.registered) {
          roomEl.textContent = 'not registered';
          roomEl.title = 'This folder is not a registered repository yet, so it has no replication room.';
          unbound = true;
        } else if (!msg.workgroupRepoId) {
          roomEl.textContent = 'no room';
          roomEl.title = 'Registered, but not yet bound to a replication room — nothing here replicates to peers.';
          unbound = true;
        } else {
          roomEl.textContent = 'room ' + msg.workgroupRepoId;
          roomEl.title = 'Replication room: repo/' + msg.workgroupRepoId
            + '\nPeers must be in this same room to see the work in this repository.';
        }
        roomEl.classList.toggle('gw-repo-room-unbound', unbound);
      }
      // The same flow handles both, but "Re-link" does not read as "this is how you register an
      // unregistered folder" — which is the very first state a new user is in.
      var relinkBtn = $('gw-repo-relink');
      if (relinkBtn) {
        relinkBtn.textContent = msg.registered ? 'Re-link…' : 'Register…';
        relinkBtn.title = msg.registered
          ? 'Re-link this repository to a different room, or split it into its own'
          : 'Register this folder with NodalMerge so it gets a replication room';
      }
      return;
    }
    if (msg.type === 'explorerSettings') {
      if (msg.effectiveRepositoryPath !== undefined) {
        var repoDisplay = $('gw-repo-path-display');
        repoDisplay.value = msg.effectiveRepositoryPath || '(no folder open)';
        repoDisplay.title = msg.repositoryPathOverride
          ? 'Override: ' + msg.effectiveRepositoryPath
          : 'Auto-detected from the open VS Code folder: ' + msg.effectiveRepositoryPath;
      }
      $('gw-llm-profile-checkbox').checked = !!msg.useLlmProfileSelection;
      $('gw-max-concurrent-workers').value = msg.maxConcurrentWorkers;
      $('gw-scheduler-poll-interval').value = msg.schedulerPollIntervalMs;
      $('gw-require-build-checkbox').checked = !!msg.requireBuildBeforeProposal;
      $('gw-require-test-checkbox').checked = !!msg.requireTestBeforeProposal;
      $('gw-enforce-output-kind-checkbox').checked = !!msg.enforceExpectedOutputKind;
      $('gw-block-conflicting-ops-checkbox').checked = !!msg.blockConflictingOps;
      $('gw-allow-auto-requeue-checkbox').checked = !!msg.allowAutoRequeue;
      $('gw-use-promotion-branch-checkbox').checked = !!msg.usePromotionBranch;
      $('gw-allow-agent-git-commits-checkbox').checked = !!msg.allowAgentGitCommits;
      $('gw-allow-agent-git-push-checkbox').checked = !!msg.allowAgentGitPush;
      if (msg.materializerConcurrency !== undefined) {
        $('gw-materializer-concurrency').value = msg.materializerConcurrency;
      }
      if (msg.maxPlanDepth !== undefined) {
        $('gw-max-plan-depth').value = msg.maxPlanDepth;
        // Per-goal input on the Goal Workspace: remember the global default and pre-fill it (unless
        // the user is mid-edit or has already typed an override).
        state.globalMaxPlanDepth = msg.maxPlanDepth;
        var goalDepthEl = $('gw-goal-plan-depth');
        if (goalDepthEl && document.activeElement !== goalDepthEl && !goalDepthEl.value) {
          goalDepthEl.value = msg.maxPlanDepth;
        }
      }
      if (msg.maxFailureAttempts !== undefined) {
        $('gw-max-failure-attempts').value = msg.maxFailureAttempts;
      }
      var timeoutSecondsEl = $('gw-clarification-timeout-seconds');
      var timeoutBehaviorEl = $('gw-clarification-timeout-behavior');
      if (timeoutSecondsEl) {
        timeoutSecondsEl.value = msg.defaultClarificationTimeoutSeconds > 0 ? msg.defaultClarificationTimeoutSeconds : 0;
      }
      if (timeoutBehaviorEl) {
        timeoutBehaviorEl.value = msg.defaultClarificationTimeoutBehavior || 'auto_continue';
      }
      // Slice 21c — Target (Direct/Candidate) only makes sense when promotion branch is on.
      $('gw-target-row').classList.toggle('visible', !!msg.usePromotionBranch);
      return;
    }
    if (msg.type === 'decisionContext') {
      if (msg.workUnitId === state.selectedNodeId) {
        state.selectedNodeContext = msg.context || null;
        // Update the Context panel in place — a full inspector rebuild here used to reset the
        // tab bar to Metadata (hardcoded 'active'), silently kicking the user off the Context
        // tab the moment its data arrived.
        var contextPanel = $('gw-panel-context');
        if (contextPanel) {
          contextPanel.innerHTML = state.selectedNodeContext ? renderContextTab(state.selectedNodeContext) : '<p class="empty">No context recorded.</p>';
          bindContextCopyButton();
        }
      }
      return;
    }
    if (msg.type === 'gwOpenConversationStandalone') {
      var wu = msg.workUnit;
      if (!wu) { return; }
      stopConversationPoll();
      state.selectedNodeId = wu.workUnitId;
      state.selectedNodeConversation = null;
      state.selectedNodeContext = null;
      // Not necessarily part of the currently selected session's tree — cache it anyway so the
      // existing decisionNodes-driven helpers (poll lookup, tab re-render) keep working. A later
      // 'tree' poll for the active session will overwrite this array and may drop it again; that
      // only affects the Metadata tab's re-render, not the Conversation tab already on screen.
      state.decisionNodes = (state.decisionNodes || []).filter(function(w) { return w.workUnitId !== wu.workUnitId; });
      state.decisionNodes.push(wu);
      state.selectedNodeProposalDetail = null;
      // Opened standalone (not via the timeline), so any cached timelineArtifacts belong to a
      // different node — no Decision tab to show here.
      $('gw-inspector').innerHTML = renderDecisionInspector(wu, { candidate: null });
      bindDecisionInspectorTabs();
      root.querySelectorAll('.gw-tab-btn').forEach(function(b) {
        b.classList.toggle('active', b.getAttribute('data-gw-tab') === 'conversation');
      });
      root.querySelectorAll('.gw-tab-panel').forEach(function(p) {
        p.classList.toggle('active', p.id === 'gw-panel-conversation');
      });
      if (isWuRunning(wu)) { startConversationPoll(wu.workUnitId); }
      return;
    }
    if (msg.type === 'conversationLog') {
      if (msg.workUnitId === state.selectedNodeId) {
        state.selectedNodeConversation = msg.entries || [];
        var convPanel = $('gw-panel-conversation');
        // This is the copy/paste hot spot — the conversation poll rebuilds the whole tab
        // every 2s, which used to wipe any selection the moment the user tried to copy a
        // comment or agent message out. Keep the stale render until the selection clears.
        if (convPanel && hasSelectionWithin(convPanel)) { return; }
        if (convPanel) {
          // Polling re-renders this tab by full innerHTML replacement (entries can change shape
          // mid-run), which would otherwise re-collapse every <details> the user had opened and
          // reset their scroll position on every 2s tick. Snapshot by toolUseId (stable across
          // polls) and the scrollable inspector column, then restore after the swap.
          var openIds = Array.prototype.map.call(
            convPanel.querySelectorAll('.conv-tool[open]'),
            function(d) { return d.getAttribute('data-tool-use-id'); },
          );
          var scrollEl = $('gw-col-inspector');
          var scrollTop = scrollEl ? scrollEl.scrollTop : 0;
          convPanel.innerHTML = renderConversationTab(state.selectedNodeConversation);
          openIds.forEach(function(id) {
            if (!id) { return; }
            var d = convPanel.querySelector('.conv-tool[data-tool-use-id="' + CSS.escape(id) + '"]');
            if (d) { d.setAttribute('open', ''); }
          });
          if (scrollEl) { scrollEl.scrollTop = scrollTop; }
        }
      }
      return;
    }
    if (msg.type === 'counterfactualComparison') {
      $('gw-inspector').innerHTML = renderCounterfactualComparison(msg.comparison);
      return;
    }
    if (msg.type === 'runResult') {
      var btn = $('gw-run');
      btn.disabled = false;
      btn.textContent = '\u25B6 Run';
      if (msg.success) {
        $('gw-goal').value = '';
      }
      return;
    }
    if (msg.type === 'casReconcileDone') {
      var casBtn = $('gw-load-cas');
      casBtn.disabled = false;
      casBtn.textContent = '\u2601 Load to CAS';
      return;
    }
  });

}
