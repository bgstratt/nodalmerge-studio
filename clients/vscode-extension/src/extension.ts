import * as vscode from 'vscode';
import { HostManager } from './HostManager';
import { StudioShellPanel } from './panels/StudioShellPanel';
import { DecisionConvergencePanel } from './panels/MergeReviewPanel';
import { InsightsPanel } from './panels/InsightsPanel';
import { NotificationManager } from './NotificationManager';
import { AgentConfigService } from './AgentConfigService';
import { LmApiProxy } from './LmApiProxy';
import { COMMANDS } from './constants';

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
    const actions = manager.isRemote
      ? ['Show Output']
      : ['Show Output', 'Retry'];
    const action = await vscode.window.showErrorMessage(
      `NodalMerge Studio failed to connect: ${String(err)}`,
      ...actions
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
