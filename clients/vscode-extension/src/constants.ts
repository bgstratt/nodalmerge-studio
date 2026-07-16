export const DEFAULT_RUNTIME_URI = 'http://127.0.0.1:5080';
/** @deprecated Use DEFAULT_RUNTIME_URI. Kept so existing hostPort config can be migrated. */
export const DEFAULT_HOST_PORT = 5080;
export const HOST_STARTUP_TIMEOUT_MS = 15_000;
export const HOST_HEALTH_POLL_INTERVAL_MS = 500;

export const LAUNCHER_VIEW_ID = 'nodalmerge.launcher';

export const COMMANDS = {
  RESTART_HOST:         'nodalmerge.restartHost',
  SHOW_OUTPUT:          'nodalmerge.showOutput',
  OPEN_STUDIO:          'nodalmerge.openStudio',
  OPEN_MERGE_REVIEW:    'nodalmerge.openMergeReview',
  OPEN_MERGE_REVIEW_CONFLICT: 'nodalmerge.openMergeReviewConflict',
  OPEN_INSIGHTS:        'nodalmerge.openInsights',
  START_LOCAL_RUNTIME:  'nodalmerge.startLocalRuntime',
  RECONCILE_BLOB_ORIGIN: 'nodalmerge.reconcileBlobOrigin',
} as const;

export const HOST_BINARY_NAME = {
  win32:  'NodalMerge.Studio.Host.exe',
  linux:  'NodalMerge.Studio.Host',
  darwin: 'NodalMerge.Studio.Host',
} as const;

/** Maps process.platform + process.arch to the .NET self-contained publish RID */
export function getRid(): string {
  const { platform, arch } = process;
  if (platform === 'win32'  && arch === 'x64')   { return 'win-x64'; }
  if (platform === 'win32'  && arch === 'arm64')  { return 'win-arm64'; }
  if (platform === 'linux'  && arch === 'x64')    { return 'linux-x64'; }
  if (platform === 'linux'  && arch === 'arm64')  { return 'linux-arm64'; }
  if (platform === 'darwin' && arch === 'x64')    { return 'osx-x64'; }
  if (platform === 'darwin' && arch === 'arm64')  { return 'osx-arm64'; }
  throw new Error(`Unsupported platform: ${platform}/${arch}`);
}

/** Converts an http(s) base URL to its ws(s) equivalent for WebSocket connections. */
export function toWebSocketUrl(httpUri: string): string {
  return httpUri.replace(/^https:/, 'wss:').replace(/^http:/, 'ws:');
}
