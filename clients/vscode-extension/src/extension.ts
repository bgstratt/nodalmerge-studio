import * as vscode from 'vscode';
import { HostManager } from './HostManager';
import { WorkspaceDashboardPanel } from './panels/WorkspaceDashboardPanel';
import { MergeReviewPanel } from './panels/MergeReviewPanel';
import { DagReplayPanel } from './panels/DagReplayPanel';
import { AgentConfigPanel } from './panels/AgentConfigPanel';
import { NotificationManager } from './NotificationManager';
import { AgentConfigService } from './AgentConfigService';
import { LmApiProxy } from './LmApiProxy';
import { COMMANDS } from './constants';

export async function activate(context: vscode.ExtensionContext): Promise<void> {
  const output = vscode.window.createOutputChannel('NodalMerge Studio');
  context.subscriptions.push(output);

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
        vscode.window.showInformationMessage('NodalMerge Studio Host restarted.');
      } catch (err) {
        vscode.window.showErrorMessage(`Failed to restart host: ${String(err)}`);
      }
    }),

    vscode.commands.registerCommand(COMMANDS.SHOW_OUTPUT, () => {
      output.show();
    }),

    vscode.commands.registerCommand(COMMANDS.OPEN_DASHBOARD, () => {
      WorkspaceDashboardPanel.createOrShow(
        manager.hostBaseUrl, notificationManager, agentConfig, context.secrets, lmProxy.baseUrl,
      );
    }),

    vscode.commands.registerCommand(COMMANDS.OPEN_MERGE_REVIEW, (proposalId: string) => {
      MergeReviewPanel.createOrShow(manager.hostBaseUrl, proposalId);
    }),

    vscode.commands.registerCommand(COMMANDS.OPEN_DAG_REPLAY, () => {
      DagReplayPanel.createOrShow(manager.hostBaseUrl, context.extensionUri);
    }),

    vscode.commands.registerCommand(COMMANDS.OPEN_AGENT_CONFIG, () => {
      AgentConfigPanel.createOrShow(manager.hostBaseUrl, agentConfig, context.secrets, lmProxy.baseUrl);
    }),
  );

  const notificationManager = new NotificationManager((proposalId) => {
    void vscode.commands.executeCommand(COMMANDS.OPEN_MERGE_REVIEW, proposalId);
  });

  try {
    await manager.start();
  } catch (err) {
    const action = await vscode.window.showErrorMessage(
      `NodalMerge Studio Host failed to start: ${String(err)}`,
      'Show Output',
      'Retry'
    );
    if (action === 'Show Output') {
      output.show();
    } else if (action === 'Retry') {
      vscode.commands.executeCommand(COMMANDS.RESTART_HOST);
    }
  }
}

export function deactivate(): void {
  // HostManager and LmApiProxy are disposed automatically via context.subscriptions
}
