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
