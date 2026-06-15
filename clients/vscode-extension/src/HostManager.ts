import * as cp from 'child_process';
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
    // If the port is already healthy (e.g. manually started host), just adopt it.
    if (await this.checkHealth()) {
      this._ready = true;
      this.applyStatus('ready');
      this.output.appendLine(`[NodalMerge] Adopted running host on port ${this.port}.`);
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
    const { cmd, args, env } = this.resolveHostCommand();
    this.output.appendLine(`[NodalMerge] Spawning: ${cmd} ${args.join(' ')}`);

    this.process = cp.spawn(cmd, args, {
      env: { ...process.env, ...env },
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

  private resolveHostCommand(): { cmd: string; args: string[]; env: Record<string, string> } {
    const hostEnv: Record<string, string> = {
      Studio__Urls: `http://127.0.0.1:${this.port}`,
      ASPNETCORE_URLS: `http://127.0.0.1:${this.port}`,
    };

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
      };
    }

    // Production: use the self-contained binary bundled under bin/{rid}/
    const rid = getRid();
    const binaryName = HOST_BINARY_NAME[process.platform as keyof typeof HOST_BINARY_NAME]
      ?? 'NodalMerge.Studio.Host';
    const binaryPath = path.join(this.context.extensionPath, 'bin', rid, binaryName);
    return { cmd: binaryPath, args: [], env: hostEnv };
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
