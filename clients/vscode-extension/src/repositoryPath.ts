import * as vscode from 'vscode';

/** Empty (default) = auto-detect from the open VS Code folder. A non-empty
 *  "nodalmerge.repositoryPath" override lets Studio operate against a different checkout. */
export function resolveRepositoryPath(): string | undefined {
  const override = vscode.workspace.getConfiguration('nodalmerge').get<string>('repositoryPath', '');
  if (override) { return override; }
  return vscode.workspace.workspaceFolders?.[0]?.uri?.fsPath;
}
