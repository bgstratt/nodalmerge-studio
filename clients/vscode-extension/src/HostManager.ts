import * as cp from 'child_process';
import * as fs from 'fs';
import * as http from 'http';
import * as path from 'path';
import * as vscode from 'vscode';
import {
  COMMANDS,
  DEFAULT_HOST_PORT,
  HOST_BINARY_NAME,
  HOST_HEALTH_POLL_INTERVAL_MS,
  HOST_STARTUP_TIMEOUT_MS,
  getRid,
} from './constants';

type HostStatus = 'idle' | 'starting' | 'ready' | 'stopped' | 'error';

function sleep(ms: number): Promise<void> {
  return new Promise(resolve => setTimeout(resolve, ms));
}

// Name Studio used to (and, if a user opts in via workspaceDataPath, still can) store its data
// under, directly inside the opened repo.
const LEGACY_DATA_DIRNAME = '.nodalmerge';
const MIGRATION_SUPPRESS_KEY = 'nodalmerge.suppressMigrationPromptThisSession';

function directoryHasContent(dir: string): boolean {
  try {
    return fs.readdirSync(dir).length > 0;
  } catch {
    return false;
  }
}

export class HostManager implements vscode.Disposable {
  private readonly output: vscode.OutputChannel;
  private readonly context: vscode.ExtensionContext;
  private readonly statusBar: vscode.StatusBarItem;
  private process: cp.ChildProcess | undefined;
  private port: number;
  private _ready = false;

  constructor(output: vscode.OutputChannel, context: vscode.ExtensionContext) {
    this.output = output;
    this.context = context;
    this.port = vscode.workspace.getConfiguration('nodalmerge').get<number>('hostPort', DEFAULT_HOST_PORT);

    this.statusBar = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Left, 100);
    this.statusBar.command = COMMANDS.SHOW_OUTPUT;
    this.statusBar.show();
    this.applyStatus('idle');
  }

  get isReady(): boolean { return this._ready; }
  get hostPort(): number { return this.port; }
  get hostBaseUrl(): string { return `http://127.0.0.1:${this.port}`; }

  async start(): Promise<void> {
    const wsRoot = vscode.workspace.workspaceFolders?.[0]?.uri?.fsPath;
    if (wsRoot) {
      await this.maybePromptLegacyMigration(wsRoot);
    }

    // If the port is already healthy (e.g. manually started host), just adopt it.
    if (await this.checkHealth()) {
      this._ready = true;
      this.applyStatus('ready');
      this.output.appendLine(`[NodalMerge] Adopted running host on port ${this.port}.`);
      this.output.appendLine(
        '[NodalMerge] Host logs will not appear here. Stop the process on that port, then run ' +
        '"NodalMerge: Restart Studio Host" so the extension owns the host and streams logs.',
      );
      return;
    }

    this.applyStatus('starting');
    this.spawnProcess();
    await this.waitForHealth();
  }

  async restart(): Promise<void> {
    this.killProcess();
    this._ready = false;
    await this.start();
  }

  private spawnProcess(): void {
    const { cmd, args, env, cwd } = this.resolveHostCommand();
    this.output.appendLine(`[NodalMerge] Spawning: ${cmd} ${args.join(' ')}`);

    this.process = cp.spawn(cmd, args, {
      env: { ...process.env, ...env },
      cwd,
      stdio: ['ignore', 'pipe', 'pipe'],
      // On Windows, spawn without a window
      windowsHide: true,
    });

    this.process.stdout?.on('data', (chunk: Buffer) => {
      this.output.append(chunk.toString());
    });

    this.process.stderr?.on('data', (chunk: Buffer) => {
      this.output.append(chunk.toString());
    });

    this.process.on('error', (err) => {
      this.output.appendLine(`[NodalMerge] Spawn error: ${err.message}`);
      this.applyStatus('error');
    });

    this.process.on('exit', (code, signal) => {
      this.output.appendLine(`[NodalMerge] Host exited — code=${code ?? 'null'} signal=${signal ?? 'none'}`);
      this._ready = false;
      this.process = undefined;
      this.applyStatus('stopped');
    });
  }

  private resolveHostCommand(): { cmd: string; args: string[]; env: Record<string, string>; cwd?: string } {
    const hostEnv: Record<string, string> = {
      Studio__Urls: `http://127.0.0.1:${this.port}`,
      ASPNETCORE_URLS: `http://127.0.0.1:${this.port}`,
    };

    // Anchor durable storage (DAG node store, file blobs, branch workspace files) somewhere that
    // survives a restart, instead of the Host's process CWD / OS temp dir — but never inside the
    // opened repo by default; that's the user's working tree, not Studio's scratch space. No
    // folder open (single-file mode) — leave the Host's own defaults (temp dir) alone, there's no
    // workspace to anchor to either way.
    const wsRoot = vscode.workspace.workspaceFolders?.[0]?.uri?.fsPath;
    if (wsRoot) {
      const dataRoot = this.resolveDataRoot(wsRoot);
      if (dataRoot) {
        hostEnv.Workspace__RootPath = path.join(dataRoot, 'workspace');
        hostEnv.NodalMerge__Storage__Sqlite__DbPath = path.join(dataRoot, 'data', 'nodalmerge-nodes.db');
        hostEnv.NodalMerge__Storage__FileBlobs__RootPath = path.join(dataRoot, 'data', 'blobs');
      }
    }

    // In extension development mode use `dotnet run` so there's no need to
    // pre-publish a binary. The extension path is clients/vscode-extension/ so
    // the repo root is two levels up.
    if (this.context.extensionMode === vscode.ExtensionMode.Development) {
      const repoRoot = path.join(this.context.extensionPath, '..', '..');
      const hostProject = path.join(
        repoRoot, 'src', 'NodalMerge.Studio.Host', 'NodalMerge.Studio.Host.csproj'
      );
      this.output.appendLine(`[NodalMerge] Dev mode — dotnet run --project ${hostProject}`);
      return {
        cmd: 'dotnet',
        args: ['run', '--project', hostProject, '--no-launch-profile'],
        env: hostEnv,
        cwd: wsRoot,
      };
    }

    // Production: use the self-contained binary bundled under bin/{rid}/
    const rid = getRid();
    const binaryName = HOST_BINARY_NAME[process.platform as keyof typeof HOST_BINARY_NAME]
      ?? 'NodalMerge.Studio.Host';
    const binaryPath = path.join(this.context.extensionPath, 'bin', rid, binaryName);
    return { cmd: binaryPath, args: [], env: hostEnv, cwd: wsRoot };
  }

  // Empty (default) = VS Code's own per-workspace extension storage, which already lives outside
  // any repo. A non-empty override is the user's explicit choice — relative paths resolve against
  // the workspace folder (e.g. ".nodalmerge", if they actually want it versioned), absolute paths
  // let them point anywhere (e.g. another drive).
  private resolveDataRoot(wsRoot: string): string | undefined {
    const override = vscode.workspace.getConfiguration('nodalmerge').get<string>('workspaceDataPath', '');
    if (override) {
      return path.isAbsolute(override) ? override : path.join(wsRoot, override);
    }
    return this.context.storageUri?.fsPath ?? this.context.globalStorageUri.fsPath;
  }

  // Repos that ran under the old default already have real branch history sitting in
  // <repo>/.nodalmerge. Don't switch the default out from under them silently, and don't leave
  // it there silently either — ask once per workspace (re-asking next time it's opened if the
  // user picks "ask me later", but not on every restart within the same session).
  private async maybePromptLegacyMigration(wsRoot: string): Promise<void> {
    const config = vscode.workspace.getConfiguration('nodalmerge');
    if (config.get<string>('workspaceDataPath', '')) {
      return; // already an explicit choice on record
    }

    const legacyDir = path.join(wsRoot, LEGACY_DATA_DIRNAME);
    if (!directoryHasContent(legacyDir)) {
      return;
    }

    if (this.context.workspaceState.get<boolean>(MIGRATION_SUPPRESS_KEY)) {
      return;
    }

    const choice = await vscode.window.showWarningMessage(
      `NodalMerge Studio found existing data in "${LEGACY_DATA_DIRNAME}" inside this repository. ` +
      'Studio no longer stores data inside your repo by default — choose how to proceed.',
      'Move it outside the repo',
      'Keep it in this repo',
      'Ask me later',
    );

    if (choice === 'Move it outside the repo') {
      await this.migrateLegacyData(legacyDir);
    } else if (choice === 'Keep it in this repo') {
      await config.update('workspaceDataPath', LEGACY_DATA_DIRNAME, vscode.ConfigurationTarget.Workspace);
      await this.offerGitignoreEntry(wsRoot);
    } else {
      await this.context.workspaceState.update(MIGRATION_SUPPRESS_KEY, true);
    }
  }

  private async migrateLegacyData(legacyDir: string): Promise<void> {
    const target = this.context.storageUri?.fsPath ?? this.context.globalStorageUri.fsPath;

    // Make sure nothing has the old directory open before moving it out from under the process.
    this.killProcess();

    try {
      if (directoryHasContent(target)) {
        vscode.window.showWarningMessage(
          `NodalMerge's default data location (${target}) already has data — leaving ` +
          `"${LEGACY_DATA_DIRNAME}" in place. Set "nodalmerge.workspaceDataPath" manually if you ` +
          'want to merge them yourself.',
        );
        return;
      }
      await fs.promises.mkdir(path.dirname(target), { recursive: true });
      await fs.promises.cp(legacyDir, target, { recursive: true });
      await fs.promises.rm(legacyDir, { recursive: true, force: true });
      vscode.window.showInformationMessage(`NodalMerge Studio data moved to ${target}.`);
    } catch (err) {
      vscode.window.showErrorMessage(`Failed to migrate NodalMerge Studio data: ${String(err)}`);
    }
  }

  private async offerGitignoreEntry(wsRoot: string): Promise<void> {
    const gitignorePath = path.join(wsRoot, '.gitignore');
    const entry = `${LEGACY_DATA_DIRNAME}/`;

    let existing = '';
    try {
      existing = await fs.promises.readFile(gitignorePath, 'utf8');
    } catch {
      // No .gitignore yet — still worth offering to create the entry below.
    }
    const alreadyIgnored = existing
      .split(/\r?\n/)
      .some(line => line.trim() === entry || line.trim() === LEGACY_DATA_DIRNAME);
    if (alreadyIgnored) {
      return;
    }

    const choice = await vscode.window.showInformationMessage(
      `Add "${entry}" to .gitignore so Studio's data directory isn't committed?`,
      'Add to .gitignore',
      'No thanks',
    );
    if (choice === 'Add to .gitignore') {
      const separator = existing.length > 0 && !existing.endsWith('\n') ? '\n' : '';
      await fs.promises.appendFile(gitignorePath, `${separator}${entry}\n`);
    }
  }

  private async waitForHealth(): Promise<void> {
    const deadline = Date.now() + HOST_STARTUP_TIMEOUT_MS;
    while (Date.now() < deadline) {
      if (await this.checkHealth()) {
        this._ready = true;
        this.applyStatus('ready');
        this.output.appendLine(`[NodalMerge] Host healthy on port ${this.port}.`);
        return;
      }
      await sleep(HOST_HEALTH_POLL_INTERVAL_MS);
    }
    this.applyStatus('error');
    throw new Error(
      `NodalMerge Studio Host did not become healthy within ${HOST_STARTUP_TIMEOUT_MS / 1000}s. ` +
      `Check the NodalMerge output channel for details.`
    );
  }

  private checkHealth(): Promise<boolean> {
    return new Promise(resolve => {
      const req = http.get(
        `http://127.0.0.1:${this.port}/studio/health`,
        { timeout: 1000 },
        res => resolve(res.statusCode === 200)
      );
      req.on('error', () => resolve(false));
      req.on('timeout', () => { req.destroy(); resolve(false); });
    });
  }

  private applyStatus(status: HostStatus): void {
    switch (status) {
      case 'idle':
        this.statusBar.text    = '$(circle-outline) NodalMerge';
        this.statusBar.tooltip = 'NodalMerge Studio — idle';
        this.statusBar.color   = undefined;
        break;
      case 'starting':
        this.statusBar.text    = '$(loading~spin) NodalMerge';
        this.statusBar.tooltip = 'NodalMerge Studio Host starting…';
        this.statusBar.color   = undefined;
        break;
      case 'ready':
        this.statusBar.text    = `$(check) NodalMerge :${this.port}`;
        this.statusBar.tooltip = `NodalMerge Studio Host running on port ${this.port}`;
        this.statusBar.color   = new vscode.ThemeColor('statusBarItem.prominentForeground');
        break;
      case 'stopped':
        this.statusBar.text    = '$(debug-stop) NodalMerge';
        this.statusBar.tooltip = 'NodalMerge Studio Host stopped — click to see output';
        this.statusBar.color   = new vscode.ThemeColor('statusBarItem.warningForeground');
        break;
      case 'error':
        this.statusBar.text    = '$(error) NodalMerge';
        this.statusBar.tooltip = 'NodalMerge Studio Host failed to start — click to see output';
        this.statusBar.color   = new vscode.ThemeColor('statusBarItem.errorForeground');
        break;
    }
  }

  private killProcess(): void {
    if (!this.process) { return; }
    this.output.appendLine('[NodalMerge] Stopping host…');
    this.process.kill('SIGTERM');
    // Give it 3s to exit gracefully, then SIGKILL
    const proc = this.process;
    setTimeout(() => {
      if (!proc.exitCode && !proc.killed) {
        proc.kill('SIGKILL');
      }
    }, 3000);
    this.process = undefined;
  }

  dispose(): void {
    this.killProcess();
    this.statusBar.dispose();
  }
}
