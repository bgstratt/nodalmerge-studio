# Slice 7a — VS Code Extension Scaffold

Status: **Complete**

## Problem

The Studio backend is complete and running as an ASP.NET Core host on `http://127.0.0.1:5080`. There is no VS Code extension that spawns it, connects to it, or exposes any UI. This slice creates the extension skeleton — host lifecycle, output channel, status bar — with nothing rendered in panels yet.

## Architecture context

```
VS Code Extension (TypeScript)
  ├─ extension.ts        — activate/deactivate, sidecar lifecycle
  ├─ HostManager         — spawn, health-poll, kill child process
  └─ panels/             — (empty stubs, populated in 7b–7d)

Studio Host (sidecar, .NET)            already exists
  ├─ /health             — HTTP health
  ├─ /studio/health      — Studio layer health
  ├─ /mcp                — SSE endpoint for AI agents
  └─ /ws/runtime         — NodalMerge room WebSocket (for DAG panel)
```

The extension host (TypeScript) is the ONLY process that spawns and owns the Studio Host process. The WebView panels communicate with the Studio Host directly via HTTP and WebSocket from the WebView context.

## Project structure

```
extension/
  package.json           — VS Code extension manifest
  tsconfig.json
  esbuild.config.mjs     — bundle to out/extension.js
  .vscodeignore
  src/
    extension.ts         — activate() / deactivate()
    HostManager.ts       — spawn, health poll, port resolution, kill
    constants.ts         — default port, command IDs
  bin/                   — gitignored; populated by publish script
    win-x64/             — self-contained Studio Host binary
    linux-x64/
    darwin-arm64/
  scripts/
    publish-host.ps1     — dotnet publish for all three targets into bin/
```

## Files touched

### New: `extension/package.json`

```json
{
  "name": "nodalmerge-studio",
  "displayName": "NodalMerge Studio",
  "publisher": "nodalmerge",
  "version": "0.1.0",
  "engines": { "vscode": "^1.90.0" },
  "categories": ["Other"],
  "activationEvents": ["onStartupFinished"],
  "main": "./out/extension.js",
  "contributes": {
    "commands": [
      { "command": "nodalmerge.restartHost", "title": "NodalMerge: Restart Studio Host" },
      { "command": "nodalmerge.showOutput",  "title": "NodalMerge: Show Output" }
    ]
  }
}
```

### New: `extension/src/constants.ts`

```ts
export const DEFAULT_HOST_PORT = 5080;
export const HOST_HEALTH_PATH = '/studio/health';
export const COMMANDS = {
  RESTART_HOST: 'nodalmerge.restartHost',
  SHOW_OUTPUT:  'nodalmerge.showOutput',
} as const;
```

### New: `extension/src/HostManager.ts`

Responsibilities:
- Resolve the Studio Host binary path for the current platform (`process.platform + process.arch`)
- Spawn as `child_process.spawn(binaryPath, [], { env: { Studio__Urls: 'http://127.0.0.1:PORT' } })`
- Pipe stdout/stderr to the VS Code output channel
- Poll `GET /studio/health` every 2 seconds until 200 (up to 15s timeout)
- Emit `onReady` event with confirmed port
- `dispose()` — sends SIGTERM, waits 3s, then SIGKILL

### New: `extension/src/extension.ts`

```ts
export async function activate(context: ExtensionContext) {
  const output = window.createOutputChannel('NodalMerge Studio');
  const manager = new HostManager(output);

  context.subscriptions.push(
    commands.registerCommand(COMMANDS.RESTART_HOST, () => manager.restart()),
    commands.registerCommand(COMMANDS.SHOW_OUTPUT,  () => output.show()),
    manager
  );

  await manager.start();
}

export function deactivate() { /* HostManager.dispose() via subscription */ }
```

Status bar item shows:
- `$(loading~spin) NodalMerge` while starting
- `$(check) NodalMerge :5080` when healthy
- `$(error) NodalMerge (stopped)` on failure

### New: `extension/scripts/publish-host.ps1`

Runs `dotnet publish` with `-r win-x64 --self-contained`, `-r linux-x64 --self-contained`, and `-r osx-arm64 --self-contained`, outputting to `bin/{rid}/`. Called once per release, not on every build.

### Updated: `plans/README.md`

Mark slice 7a in progress / complete.

## Bundling decision

Self-contained publish. The Studio Host is published with `--self-contained true` for each platform RID. Output drops into `extension/bin/{rid}/`. The extension reads the RID from `process.platform` + `process.arch` at runtime to select the right binary.

`.NET 10 runtime is NOT required on the user's machine.` The self-contained binary carries its own runtime. This adds ~100MB to the `.vsix` per platform (use VSIX per-platform packaging once stable).

## Out of scope

- Any panel UI (7b–7d)
- Authentication / token minting
- Multi-root workspace support
- Extension settings (7f)

## Success criteria

- [ ] `npm run compile` in `extension/` produces `out/extension.js`
- [ ] Extension activates in VS Code (F5 launch) without error
- [ ] Studio Host process spawns and health check passes within 15s
- [ ] Status bar shows confirmed port when healthy
- [ ] `NodalMerge: Restart Host` command kills and respawns the process
- [ ] `deactivate()` cleanly kills the child process
- [ ] Output channel shows host stdout/stderr

## Next slice

**Slice 7b — Workspace Dashboard:** First real panel. Polls `nm.v1.workspace.summary` and renders active WorkUnits, agents, pending merges, and failures.
