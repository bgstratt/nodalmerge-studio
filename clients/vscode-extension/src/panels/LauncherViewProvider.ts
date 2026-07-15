import * as vscode from 'vscode';
import { buildNonce } from './sharedWebviewChrome';
import { COMMANDS } from '../constants';

/** Sidebar entry point: a thin webview view of launch buttons for the full-tab panels
 * (StudioShellPanel etc). Keeps the activity-bar icon as a quick-launcher rather than
 * trying to cram the dashboard UI into the narrow sidebar. */
export class LauncherViewProvider implements vscode.WebviewViewProvider {
  resolveWebviewView(webviewView: vscode.WebviewView): void {
    webviewView.webview.options = { enableScripts: true };
    webviewView.webview.html = this.getHtml();

    webviewView.webview.onDidReceiveMessage((message: { type?: string; command?: string }) => {
      if (message?.type === 'run-command' && message.command) {
        vscode.commands.executeCommand(message.command)
          .then(undefined, err => vscode.window.showErrorMessage(`NodalMerge: ${String(err)}`));
      }
    });
  }

  private getHtml(): string {
    const nonce = buildNonce();
    const buttons: Array<[string, string]> = [
      ['Open Studio', COMMANDS.OPEN_STUDIO],
      ['Open Insights', COMMANDS.OPEN_INSIGHTS],
      ['Start Local Runtime', COMMANDS.START_LOCAL_RUNTIME],
      ['Restart Studio Host', COMMANDS.RESTART_HOST],
      ['Show Output', COMMANDS.SHOW_OUTPUT],
      ['Load Repo to CAS', COMMANDS.RECONCILE_BLOB_ORIGIN],
    ];

    const buttonHtml = buttons
      .map(([label, command]) => `<button data-command="${command}">${label}</button>`)
      .join('\n');

    return `<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8">
<meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline'; script-src 'nonce-${nonce}';">
<style>
  body {
    font-family: var(--vscode-font-family);
    font-size: var(--vscode-font-size, 13px);
    color: var(--vscode-foreground);
    padding: 8px;
  }
  p.nm-desc {
    margin: 0 0 12px;
    color: var(--vscode-descriptionForeground);
    font-size: 0.9em;
    line-height: 1.4;
  }
  .nm-buttons {
    display: flex;
    flex-direction: column;
    align-items: center;
  }
  button {
    display: block;
    width: 80%;
    margin-bottom: 6px;
    padding: 6px 8px;
    background: var(--vscode-button-background);
    color: var(--vscode-button-foreground);
    border: none;
    border-radius: 2px;
    cursor: pointer;
    text-align: center;
    font-family: inherit;
    font-size: inherit;
  }
  button:hover { background: var(--vscode-button-hoverBackground); }
</style>
</head>
<body>
<p class="nm-desc">Agent orchestration, task management, and human review — backed by a persistent, branchable artifact graph.</p>
<div class="nm-buttons">
${buttonHtml}
</div>
<script nonce="${nonce}">
  const vscode = acquireVsCodeApi();
  document.querySelectorAll('button[data-command]').forEach(btn => {
    btn.addEventListener('click', () => {
      vscode.postMessage({ type: 'run-command', command: btn.dataset.command });
    });
  });
</script>
</body>
</html>`;
  }
}
