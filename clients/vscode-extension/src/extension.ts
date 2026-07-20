import * as vscode from 'vscode';
import { HostManager } from './HostManager';
import { StudioShellPanel } from './panels/StudioShellPanel';
import { setPathwaysScratchRoot } from './panels/DagReplayPanel';
import { DecisionConvergencePanel } from './panels/MergeReviewPanel';
import { InsightsPanel } from './panels/InsightsPanel';
import { NotificationManager } from './NotificationManager';
import { AgentConfigService } from './AgentConfigService';
import { LmApiProxy } from './LmApiProxy';
import { LauncherViewProvider } from './panels/LauncherViewProvider';
import { COMMANDS, LAUNCHER_VIEW_ID } from './constants';

export async function activate(context: vscode.ExtensionContext): Promise<void> {
  const output = vscode.window.createOutputChannel('NodalMerge Studio');
  context.subscriptions.push(output);

  // Dev-mode auto-reload: when running via F5 against a live `npm run watch` esbuild rebuild,
  // reload the window automatically once the bundle changes instead of requiring a manual
  // "Developer: Reload Window" after every edit. Gated to Development so a normally-installed
  // extension never does this.
  if (context.extensionMode === vscode.ExtensionMode.Development) {
    const bundleWatcher = vscode.workspace.createFileSystemWatcher(
      new vscode.RelativePattern(context.extensionUri, 'out/*.js'),
    );
    let reloading = false;
    const reload = () => {
      if (reloading) { return; }
      reloading = true;
      vscode.commands.executeCommand('workbench.action.reloadWindow')
        .then(undefined, err => output.appendLine(`[NodalMerge] reloadWindow failed: ${String(err)}`));
    };
    context.subscriptions.push(
      bundleWatcher,
      bundleWatcher.onDidChange(reload),
      bundleWatcher.onDidCreate(reload),
    );
  }

  const manager     = new HostManager(output, context);
  const agentConfig = new AgentConfigService();
  const lmProxy     = new LmApiProxy();
  context.subscriptions.push(manager, lmProxy);

  // Pathways "Materialize to scratch workspace" output root — workspace-scoped storage when a
  // folder is open, global storage otherwise, matching HostManager's own storage convention.
  setPathwaysScratchRoot(context.storageUri?.fsPath ?? context.globalStorageUri.fsPath);

  context.subscriptions.push(
    vscode.window.registerWebviewViewProvider(LAUNCHER_VIEW_ID, new LauncherViewProvider()),
  );

  // Start the LM proxy in the background — non-fatal if VS Code LM is unavailable.
  try {
    await lmProxy.start();
    output.appendLine(`[NodalMerge] LM proxy listening at ${lmProxy.baseUrl}`);
  } catch (err) {
    output.appendLine(`[NodalMerge] LM proxy failed to start (vscode-lm provider unavailable): ${String(err)}`);
  }

  context.subscriptions.push(
    vscode.commands.registerCommand(COMMANDS.RESTART_HOST, async () => {
      output.show();
      try {
        await manager.restart();
        StudioShellPanel.current?.refresh();
        vscode.window.showInformationMessage('NodalMerge Studio Host restarted.');
      } catch (err) {
        vscode.window.showErrorMessage(`Failed to restart host: ${String(err)}`);
      }
    }),

    vscode.commands.registerCommand(COMMANDS.SHOW_OUTPUT, () => {
      output.show();
    }),

    vscode.commands.registerCommand(COMMANDS.OPEN_SETTINGS, () => {
      // Opens the Settings UI pre-filtered to the extension's own settings (runtime URI, room,
      // blob origin, model profiles, etc.).
      void vscode.commands.executeCommand('workbench.action.openSettings', '@ext:nodalmerge-studio.nodalmerge-studio');
    }),

    vscode.commands.registerCommand(COMMANDS.OPEN_STUDIO, () => {
      StudioShellPanel.createOrShow(
        manager.hostBaseUrl, context.extensionUri, agentConfig, context.secrets, lmProxy.baseUrl, output, notificationManager,
      );
    }),

    // Notification click-through and dead-letter "review" actions open the shell (creating it
    // if needed) and switch it to the Review tab, instead of a standalone panel.
    vscode.commands.registerCommand(COMMANDS.OPEN_MERGE_REVIEW, (proposalId: string) => {
      const shell = StudioShellPanel.createOrShow(
        manager.hostBaseUrl, context.extensionUri, agentConfig, context.secrets, lmProxy.baseUrl, output, notificationManager,
      );
      shell.showTab(DecisionConvergencePanel.containerId);
      shell.reviewPanel.loadProposal(proposalId);
    }),

    vscode.commands.registerCommand(COMMANDS.OPEN_MERGE_REVIEW_CONFLICT, (workUnitId: string) => {
      const shell = StudioShellPanel.createOrShow(
        manager.hostBaseUrl, context.extensionUri, agentConfig, context.secrets, lmProxy.baseUrl, output, notificationManager,
      );
      shell.showTab(DecisionConvergencePanel.containerId);
      shell.reviewPanel.loadConflict(workUnitId);
    }),

    vscode.commands.registerCommand(COMMANDS.OPEN_INSIGHTS, () => {
      const shell = StudioShellPanel.createOrShow(
        manager.hostBaseUrl, context.extensionUri, agentConfig, context.secrets, lmProxy.baseUrl, output, notificationManager,
      );
      shell.showTab(InsightsPanel.containerId);
    }),

    vscode.commands.registerCommand(COMMANDS.START_LOCAL_RUNTIME, async () => {
      output.show();
      try {
        await manager.startLocal();
        StudioShellPanel.current?.refresh();
        vscode.window.showInformationMessage('NodalMerge Studio local runtime started.');
      } catch (err) {
        vscode.window.showErrorMessage(`Failed to start local runtime: ${String(err)}`);
      }
    }),

    // Slice 2.3 (plans/cas-distribution-and-storage.md Phase 2) — manual trigger for the CAS
    // reconcile sweep: pushes whatever the configured blob origin is missing from the local live
    // blob set. A true no-op (all-zero counts) when nodalmerge.blobOrigin.uri isn't set — the host
    // endpoint itself handles that case, this command just surfaces whatever it reports.
    vscode.commands.registerCommand(COMMANDS.RECONCILE_BLOB_ORIGIN, async () => {
      output.show();
      try {
        const res = await fetch(`${manager.hostBaseUrl}/studio/cas/reconcile`, { method: 'POST' });
        if (!res.ok) {
          const text = await res.text();
          throw new Error(`POST /studio/cas/reconcile → ${res.status}: ${text}`);
        }
        const result = await res.json() as {
          scanned: number;
          alreadyPresent: number;
          pushed: number;
          failed: number;
          missingLocally: number;
        };
        output.appendLine(
          `[NodalMerge] CAS reconcile: scanned=${result.scanned} alreadyPresent=${result.alreadyPresent} ` +
          `pushed=${result.pushed} failed=${result.failed} missingLocally=${result.missingLocally}`,
        );
        const extras: string[] = [];
        if (result.failed > 0) { extras.push(`${result.failed} failed`); }
        if (result.missingLocally > 0) { extras.push(`${result.missingLocally} missing locally`); }
        const suffix = extras.length > 0 ? ` (${extras.join(', ')})` : '';
        vscode.window.showInformationMessage(
          `NodalMerge: CAS reconcile complete — pushed ${result.pushed}, already present ${result.alreadyPresent}${suffix}.`,
        );
      } catch (err) {
        vscode.window.showErrorMessage(`NodalMerge: CAS reconcile failed — ${String(err)}`);
      }
    }),

    // plans/vision-punchlist-remediation.md (Items 1+2) — user-initiated repository re-link. The
    // backend has exposed identity/disambiguation for a while but nothing ever called it, so a
    // repository bound to the wrong room (two clones that diverged before deterministic ids, or a
    // shallow clone that minted a guid) could only be fixed by hand-editing. Deliberately a command
    // + quickpick rather than a webview: the whole flow is list → pick → confirm, which showQuickPick
    // does natively, and it mirrors handleAddReference's existing register-then-continue pattern.
    // Re-pointing takes effect live — the membership loop joins the new room within ~5s, no restart.
    vscode.commands.registerCommand(COMMANDS.RELINK_REPOSITORY, async () => {
      const base = manager.hostBaseUrl;
      const getJson = async (path: string) => {
        const res = await fetch(`${base}${path}`);
        if (!res.ok) { throw new Error(`GET ${path} → ${res.status}: ${await res.text()}`); }
        return res.json() as Promise<any>;
      };
      const postJson = async (path: string, body: unknown) => {
        const res = await fetch(`${base}${path}`, {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify(body),
        });
        if (!res.ok) { throw new Error(`POST ${path} → ${res.status}: ${await res.text()}`); }
        return res.json() as Promise<any>;
      };

      try {
        const repos = await getJson('/studio/repositories') as
          { repositoryId: string; path: string; label?: string | null; workgroupRepoId?: string | null }[];
        if (!repos.length) {
          vscode.window.showInformationMessage('NodalMerge: no repositories are registered yet.');
          return;
        }

        const activeFolder = vscode.workspace.workspaceFolders?.[0]?.uri.fsPath;
        const picked = await vscode.window.showQuickPick(
          repos.map(r => ({
            label: r.label || r.path,
            description: r.path === activeFolder ? '(this workspace)' : r.path,
            detail: r.workgroupRepoId ? `Currently in room repo/${r.workgroupRepoId}` : 'Not bound to a room yet',
            repositoryId: r.repositoryId,
          })),
          { title: 'Re-link repository', placeHolder: 'Which repository?' },
        );
        if (!picked) { return; }

        // Preview first — same evaluation the commit would do, but no writes. This is what lets the
        // confirmation below state exactly what changes and what stops appearing.
        const preview = await postJson(
          `/studio/repositories/${encodeURIComponent(picked.repositoryId)}/identity/relink`,
          { mode: 'auto', commit: false }) as {
            currentWorkgroupRepoId?: string | null;
            proposedWorkgroupRepoId?: string | null;
            explanation: string;
            candidates: { repoId: string; label?: string | null }[];
            impact?: { roomId: string; totalNodes: number; countsByKind: Record<string, number> } | null;
          };

        const describeImpact = (impact: typeof preview.impact) => {
          if (!impact || impact.totalNodes <= 0) { return ''; }
          // Name the room being left and how much is in it. Nothing is deleted — it just stops
          // being read — and the wording has to say that precisely.
          return `\n\nThe room it is leaving (${impact.roomId}) holds ${impact.totalNodes} record(s). `
            + 'They are not deleted, but they will no longer appear in this workspace.';
        };

        const choices: vscode.QuickPickItem[] = [];
        if (preview.proposedWorkgroupRepoId) {
          choices.push({
            label: '$(check) Auto re-link',
            description: `→ repo/${preview.proposedWorkgroupRepoId}`,
            detail: preview.explanation,
          });
        }
        choices.push({
          label: '$(list-selection) Choose a repository manually…',
          detail: preview.candidates.length
            ? `${preview.candidates.length} candidate(s) offered`
            : 'Pick from every repository registered in this workgroup',
        });
        choices.push({
          label: '$(repo-forked) Register as its own repository',
          detail: 'Splits this repository into a new room of its own. Use when it only looks like another repository.',
        });

        if (!preview.proposedWorkgroupRepoId) {
          vscode.window.showInformationMessage(`NodalMerge: ${preview.explanation}`);
        }

        const action = await vscode.window.showQuickPick(choices, {
          title: `Re-link ${picked.label}`,
          placeHolder: preview.currentWorkgroupRepoId
            ? `Currently repo/${preview.currentWorkgroupRepoId}`
            : 'Not bound to a room yet',
        });
        if (!action) { return; }

        let body: Record<string, unknown>;
        let summary: string;

        if (action.label.includes('Auto re-link')) {
          body = { mode: 'auto', commit: true };
          summary = `Re-link to repo/${preview.proposedWorkgroupRepoId}?${describeImpact(preview.impact)}`;
        } else if (action.label.includes('own repository')) {
          body = { mode: 'manual', chosenRepoId: 'register-new', commit: true };
          summary = `Register ${picked.label} as its own separate repository?${describeImpact(preview.impact)}`;
        } else {
          // Offer the matcher's candidates when it produced any; otherwise every other registered
          // repository, which is what makes the already-diverged case reachable at all.
          const options = preview.candidates.length
            ? preview.candidates.map(c => ({ label: c.repoId, description: c.label || undefined }))
            : repos
              .filter(r => r.workgroupRepoId && r.workgroupRepoId !== preview.currentWorkgroupRepoId)
              .map(r => ({ label: r.workgroupRepoId!, description: r.label || r.path }));

          if (!options.length) {
            vscode.window.showInformationMessage('NodalMerge: no other repository is available to link to.');
            return;
          }

          const chosen = await vscode.window.showQuickPick(options, {
            title: 'Link to which repository?',
            placeHolder: 'The room this repository should join',
          });
          if (!chosen) { return; }

          body = { mode: 'manual', chosenRepoId: chosen.label, commit: true };
          summary = `Link ${picked.label} to repo/${chosen.label}?${describeImpact(preview.impact)}`;
        }

        // Modal, because this is the point of no easy return: the old room's content stops being
        // visible and nothing migrates it back.
        const confirm = await vscode.window.showWarningMessage(
          summary, { modal: true }, 'Re-link');
        if (confirm !== 'Re-link') { return; }

        const result = await postJson(
          `/studio/repositories/${encodeURIComponent(picked.repositoryId)}/identity/relink`, body) as {
            committed: boolean; proposedWorkgroupRepoId?: string | null; explanation: string;
          };

        if (result.committed) {
          vscode.window.showInformationMessage(
            `NodalMerge: re-linked to repo/${result.proposedWorkgroupRepoId}. `
            + 'Joining the room takes a few seconds.');
          StudioShellPanel.current?.refresh();
        } else {
          vscode.window.showInformationMessage(`NodalMerge: nothing changed — ${result.explanation}`);
        }
      } catch (err) {
        vscode.window.showErrorMessage(`NodalMerge: re-link failed — ${String(err)}`);
      }
    }),
  );

  const notificationManager = new NotificationManager(
    (proposalId) => {
      vscode.commands.executeCommand(COMMANDS.OPEN_MERGE_REVIEW, proposalId)
        .then(undefined, err => output.appendLine(`[NodalMerge] OPEN_MERGE_REVIEW failed: ${String(err)}`));
    },
    () => {
      vscode.commands.executeCommand(COMMANDS.OPEN_INSIGHTS)
        .then(undefined, err => output.appendLine(`[NodalMerge] OPEN_INSIGHTS failed: ${String(err)}`));
    },
  );

  try {
    await manager.start();
  } catch (err) {
    // The extension always spawns/adopts its own local peer (D4 — no remote-only mode), so a
    // start() failure is always retryable (e.g. a stale process holding the port).
    const action = await vscode.window.showErrorMessage(
      `NodalMerge Studio failed to connect: ${String(err)}`,
      'Show Output', 'Retry'
    );
    if (action === 'Show Output') {
      output.show();
    } else if (action === 'Retry') {
      vscode.commands.executeCommand(COMMANDS.RESTART_HOST)
        .then(undefined, err => output.appendLine(`[NodalMerge] RESTART_HOST failed: ${String(err)}`));
    }
  }
}

export function deactivate(): void {
  // HostManager and LmApiProxy are disposed automatically via context.subscriptions
}
