import * as cp from 'child_process';
import * as fs from 'fs';
import * as http from 'http';
import * as path from 'path';
import * as vscode from 'vscode';
import {
  COMMANDS,
  DEFAULT_HOST_PORT,
  DEFAULT_RUNTIME_URI,
  HOST_BINARY_NAME,
  HOST_HEALTH_POLL_INTERVAL_MS,
  HOST_STARTUP_TIMEOUT_MS,
  getRid,
  isLocalUri,
  toWebSocketUrl,
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

/** Reads the configured runtime URI from settings, honouring the legacy hostPort setting
 *  so existing configs keep working without any user action. */
function resolveConfiguredUri(): string {
  const cfg = vscode.workspace.getConfiguration('nodalmerge');
  const explicit = cfg.get<string>('runtimeUri', '').trim();
  if (explicit) { return explicit.replace(/\/$/, ''); }
  // Migrate legacy hostPort: if user changed it from the default, honour it.
  const legacyPort = cfg.get<number>('hostPort', DEFAULT_HOST_PORT);
  if (legacyPort !== DEFAULT_HOST_PORT) {
    return `http://127.0.0.1:${legacyPort}`;
  }
  return DEFAULT_RUNTIME_URI;
}

export class HostManager implements vscode.Disposable {
  private readonly output: vscode.OutputChannel;
  private readonly context: vscode.ExtensionContext;
  private readonly statusBar: vscode.StatusBarItem;
  private process: cp.ChildProcess | undefined;
  private uri: string;
  private _ready = false;

  constructor(output: vscode.OutputChannel, context: vscode.ExtensionContext) {
    this.output = output;
    this.context = context;
    this.uri = resolveConfiguredUri();

    this.statusBar = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Left, 100);
    this.statusBar.command = COMMANDS.SHOW_OUTPUT;
    this.statusBar.show();
    this.applyStatus('idle');
  }

  get isReady(): boolean { return this._ready; }
  get hostBaseUrl(): string { return this.uri; }
  get hostWsUrl(): string { return toWebSocketUrl(this.uri); }
  get isRemote(): boolean { return !isLocalUri(this.uri); }

  /** Start the runtime. If the configured URI is remote, just health-check and adopt it.
   *  If local, check first (adopt if already running), then spawn. */
  async start(): Promise<void> {
    const t0 = performance.now();
    const elapsed = () => `+${(performance.now() - t0).toFixed(0)}ms`;

    // Re-read config on every start so a settings change takes effect without reloading the window.
    this.uri = resolveConfiguredUri();
    this.output.appendLine(`[startup] extension start() at ${elapsed()}`);

    const wsRoot = vscode.workspace.workspaceFolders?.[0]?.uri?.fsPath;
    if (wsRoot && !this.isRemote) {
      const t1 = performance.now();
      await this.maybePromptLegacyMigration(wsRoot);
      this.output.appendLine(`[startup] legacy migration check: ${(performance.now() - t1).toFixed(0)}ms`);
    }

    const t2 = performance.now();
    if (await this.checkHealth()) {
      this.output.appendLine(`[startup] initial health check (already running): ${(performance.now() - t2).toFixed(0)}ms`);
      this._ready = true;
      this.applyStatus('ready');
      const label = this.isRemote ? this.uri : `port ${this.extractPort()}`;
      this.output.appendLine(`[NodalMerge] Connected to running runtime at ${label}.`);
      if (!this.isRemote) {
        this.output.appendLine(
          '[NodalMerge] Host logs will not appear here. Stop the process on that port, then run ' +
          '"NodalMerge: Restart Studio Host" so the extension owns the host and streams logs.',
        );
      }
      return;
    }
    this.output.appendLine(`[startup] initial health check (not running): ${(performance.now() - t2).toFixed(0)}ms`);

    if (this.isRemote) {
      this.applyStatus('error');
      throw new Error(
        `NodalMerge Studio: could not reach runtime at ${this.uri}. ` +
        'Make sure the remote runtime is running and accessible.'
      );
    }

    this.applyStatus('starting');
    this.output.appendLine(`[startup] spawning process at ${elapsed()}`);
    this.spawnProcess();
    await this.waitForHealth(t0);
    this.output.appendLine(`[startup] total extension-side startup: ${elapsed()}`);
  }

  /** Explicitly starts the local runtime regardless of the configured URI.
   *  Used by the "Start Local Runtime" command when the user wants a local instance
   *  alongside a remote URI they normally point at. */
  async startLocal(): Promise<void> {
    if (this.process) {
      this.output.appendLine('[NodalMerge] Local runtime already running.');
      return;
    }
    this.applyStatus('starting');
    const t0 = performance.now();
    this.spawnProcess();
    await this.waitForHealth(t0);
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
    // For a local spawn we always bind to 127.0.0.1. If the runtimeUri is also local, use its
    // port; otherwise fall back to the default port so the explicit local spawn has a stable address.
    const bindPort = isLocalUri(this.uri) ? this.extractPort() : DEFAULT_HOST_PORT;
    const bindAddr = `http://127.0.0.1:${bindPort}`;

    const hostEnv: Record<string, string> = {
      Studio__Urls:    bindAddr,
      ASPNETCORE_URLS: bindAddr,
    };

    const wsRoot = vscode.workspace.workspaceFolders?.[0]?.uri?.fsPath;
    if (wsRoot) {
      const dataRoot = this.resolveDataRoot(wsRoot);
      if (dataRoot) {
        hostEnv.Workspace__RootPath = path.join(dataRoot, 'workspace');
        hostEnv.NodalMerge__Storage__Sqlite__DbPath = path.join(dataRoot, 'data', 'nodalmerge-nodes.db');
        hostEnv.NodalMerge__Storage__FileBlobs__RootPath = path.join(dataRoot, 'data', 'blobs');
      }
    }

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

    const rid = getRid();
    const binaryName = HOST_BINARY_NAME[process.platform as keyof typeof HOST_BINARY_NAME]
      ?? 'NodalMerge.Studio.Host';
    const binaryPath = path.join(this.context.extensionPath, 'bin', rid, binaryName);
    return { cmd: binaryPath, args: [], env: hostEnv, cwd: wsRoot };
  }

  private resolveDataRoot(wsRoot: string): string | undefined {
    const override = vscode.workspace.getConfiguration('nodalmerge').get<string>('workspaceDataPath', '');
    if (override) {
      return path.isAbsolute(override) ? override : path.join(wsRoot, override);
    }
    return this.context.storageUri?.fsPath ?? this.context.globalStorageUri.fsPath;
  }

  private async maybePromptLegacyMigration(wsRoot: string): Promise<void> {
    const config = vscode.workspace.getConfiguration('nodalmerge');
    if (config.get<string>('workspaceDataPath', '')) {
      return;
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
    if (alreadyIgnored) { return; }

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

  private async waitForHealth(spawnT0?: number): Promise<void> {
    const deadline = Date.now() + HOST_STARTUP_TIMEOUT_MS;
    let pollCount = 0;
    while (Date.now() < deadline) {
      pollCount++;
      const pollStart = performance.now();
      if (await this.checkHealth()) {
        const sincePoll = (performance.now() - pollStart).toFixed(0);
        const sinceSpawn = spawnT0 !== undefined ? ` (${(performance.now() - spawnT0).toFixed(0)}ms since spawn)` : '';
        this._ready = true;
        this.applyStatus('ready');
        this.output.appendLine(`[startup] health ok on poll #${pollCount} (check took ${sincePoll}ms)${sinceSpawn}`);
        this.output.appendLine(`[NodalMerge] Host healthy at ${this.uri}.`);
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
      const url = `${this.uri}/studio/health`;
      const req = http.get(url, { timeout: 1000 }, res => resolve(res.statusCode === 200));
      req.on('error', () => resolve(false));
      req.on('timeout', () => { req.destroy(); resolve(false); });
    });
  }

  private extractPort(): number {
    try {
      return parseInt(new URL(this.uri).port || '5080', 10) || DEFAULT_HOST_PORT;
    } catch {
      return DEFAULT_HOST_PORT;
    }
  }

  private applyStatus(status: HostStatus): void {
    const uriLabel = this.isRemote ? this.uri : `:${this.extractPort()}`;
    switch (status) {
      case 'idle':
        this.statusBar.text    = '$(circle-outline) NodalMerge';
        this.statusBar.tooltip = 'NodalMerge Studio — idle';
        this.statusBar.color   = undefined;
        break;
      case 'starting':
        this.statusBar.text    = '$(loading~spin) NodalMerge';
        this.statusBar.tooltip = `NodalMerge Studio starting… (${this.uri})`;
        this.statusBar.color   = undefined;
        break;
      case 'ready':
        this.statusBar.text    = `$(check) NodalMerge ${uriLabel}`;
        this.statusBar.tooltip = `NodalMerge Studio connected — ${this.uri}`;
        this.statusBar.color   = new vscode.ThemeColor('statusBarItem.prominentForeground');
        break;
      case 'stopped':
        this.statusBar.text    = '$(debug-stop) NodalMerge';
        this.statusBar.tooltip = 'NodalMerge Studio stopped — click to see output';
        this.statusBar.color   = new vscode.ThemeColor('statusBarItem.warningForeground');
        break;
      case 'error':
        this.statusBar.text    = '$(error) NodalMerge';
        this.statusBar.tooltip = `NodalMerge Studio failed to connect (${this.uri}) — click to see output`;
        this.statusBar.color   = new vscode.ThemeColor('statusBarItem.errorForeground');
        break;
    }
  }

  private killProcess(): void {
    if (!this.process) { return; }
    this.output.appendLine('[NodalMerge] Stopping host…');
    this.process.kill('SIGTERM');
    const proc = this.process;
    setTimeout(() => {
      if (!proc.exitCode && !proc.killed) { proc.kill('SIGKILL'); }
    }, 3000);
    this.process = undefined;
  }

  dispose(): void {
    this.killProcess();
    this.statusBar.dispose();
  }
}
