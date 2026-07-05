// Minimal stand-in for the 'vscode' module so panel modules can be imported outside an
// extension host (webview smoke-test harness). Only module-load-time surface matters here:
// the harness never constructs panels, it only calls their static getFragment functions,
// which touch no vscode API (DagReplayPanel uses getFragmentForScriptSrc instead).
// Anything unexpected is a loud throw rather than a silent undefined.

function unavailable(name: string): never {
  throw new Error(`vscode.${name} is not available in the webview smoke-test harness`);
}

export const window = new Proxy({}, { get: (_t, p) => unavailable(`window.${String(p)}`) });
export const workspace = new Proxy({}, { get: (_t, p) => unavailable(`workspace.${String(p)}`) });
export const commands = new Proxy({}, { get: (_t, p) => unavailable(`commands.${String(p)}`) });
export const env = new Proxy({}, { get: (_t, p) => unavailable(`env.${String(p)}`) });
export const Uri = new Proxy({}, { get: (_t, p) => unavailable(`Uri.${String(p)}`) });
export const lm = new Proxy({}, { get: (_t, p) => unavailable(`lm.${String(p)}`) });
export const ConfigurationTarget = { Global: 1, Workspace: 2, WorkspaceFolder: 3 };
export const ViewColumn = { One: 1, Two: 2, Three: 3 };
export const EventEmitter = class { event = () => ({ dispose() {} }); fire() {} dispose() {} };
