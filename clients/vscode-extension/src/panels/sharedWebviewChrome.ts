// Slice 0 — Studio Shell. Every view embedded in the shell used to build its own standalone
// vscode.WebviewPanel with its own CSP nonce. Now there is exactly one WebviewPanel and one
// nonce for the whole document; this module holds the bits that used to be copy-pasted across
// MergeReviewPanel.ts, AgentConfigPanel.ts, and WorkspaceDashboardPanel.ts.

export function buildNonce(): string {
  let text = '';
  const chars = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789';
  for (let i = 0; i < 32; i++) { text += chars[Math.floor(Math.random() * chars.length)]; }
  return text;
}

/**
 * Each view's CSS/HTML/JS was written assuming it owned the whole webview document: bare
 * element selectors like `button { ... }` and a single top-level `acquireVsCodeApi()` /
 * `document.getElementById(...)` calls. Embedding four of those in one document means bare
 * selectors would bleed across views (e.g. every panel defines its own `button { padding: ... }`)
 * and `document.getElementById` calls would collide on duplicate ids that were never meant to
 * coexist (both AgentConfigPanel and WorkspaceDashboardPanel use `id="btn-spawn"`).
 *
 * Rather than hand-renaming every id and selector in the views' CSS,
 * this wraps each view's CSS in a native CSS `@scope` block (limits every selector — including
 * bare `button`/`section`/`table` — to descendants of the view's own container, unmodified) and
 * rewrites each view's JS so `document.getElementById('x')` becomes a lookup scoped to that same
 * container, and the single shared `acquireVsCodeApi()` result is reused instead of called again
 * (calling it more than once per webview throws).
 */
export function scopeViewCss(css: string, containerId: string): string {
  // Each view's CSS was written assuming it owned <body> (e.g. `body { display: flex; height:
  // 100vh; }` to size its own layout). @scope only matches descendants of the scope root, never
  // ancestors — body is an ancestor of the view's container div, so an unmodified `body { ... }`
  // rule would silently match nothing once scoped. `:scope` inside an @scope block refers to the
  // scope root itself (the container div), which is what each view actually needs to size.
  // `height: 100vh` is dropped, not translated to `:scope`'s height — the container is already
  // sized to fill its tab pane via the shell's own `position: absolute; inset: 0` (StudioShellPanel
  // CSS), and an explicit 100vh here would override that and overflow past the tab bar.
  const scoped = css
    .replace(/(^|\}|\s)body(\s*\{)/g, '$1:scope$2')
    .replace(/height:\s*100vh;?/g, '');
  return `@scope (#${containerId}) {\n${scoped}\n}`;
}

// Historical note: view JS used to be inline template strings, rewritten at build-html time
// by scopeViewScript()/wrapViewScript() here (getElementById -> container-scoped $, shared
// acquireVsCodeApi handle, NM-FATAL error trap). Views are now real modules in
// src/webviews/views/ bundled to out/studio-views.js; the same scoping/error semantics live
// in src/webviews/views/runtime.js (runView).

export const SHELL_CSS_VARS = `
  :root {
    --nm-bg:         var(--vscode-editor-background);
    --nm-fg:         var(--vscode-editor-foreground);
    --nm-border:     var(--vscode-widget-border, #444);
    --nm-section-bg: var(--vscode-sideBar-background, var(--vscode-editor-background));
    --nm-btn:        var(--vscode-button-background);
    --nm-btn-fg:     var(--vscode-button-foreground);
    --nm-btn-hover:  var(--vscode-button-hoverBackground);
    --nm-font:       var(--vscode-font-family);
    --nm-mono:       var(--vscode-editor-font-family, monospace);
    --nm-size:       var(--vscode-font-size, 13px);
    --nm-success:    #4dac26;
    --nm-warn:       #cca700;
    --nm-error:      #f14c4c;
  }
  * { box-sizing: border-box; }
  html, body {
    background: var(--nm-bg); color: var(--nm-fg);
    font-family: var(--nm-font); font-size: var(--nm-size);
    margin: 0; padding: 0; height: 100%; overflow: hidden;
  }
  body { display: flex; flex-direction: column; }
  #nm-shell-tabbar {
    display: flex; flex-shrink: 0;
    border-bottom: 1px solid var(--nm-border);
    background: var(--nm-section-bg);
  }
  .nm-shell-tab {
    background: transparent; color: var(--nm-fg); border: none;
    border-bottom: 2px solid transparent;
    padding: 9px 18px; font-size: 0.9em;
    cursor: pointer; font-family: var(--nm-font); opacity: 0.62;
  }
  .nm-shell-tab:hover { opacity: 0.9; }
  .nm-shell-tab.active { opacity: 1; border-bottom-color: var(--nm-btn); font-weight: 600; }
  #nm-shell-content { flex: 1; overflow: hidden; position: relative; }
  .nm-shell-pane {
    position: absolute; inset: 0;
    overflow-y: auto;
  }
  /* !important: a view's own scoped CSS (e.g. AgentConfigPanel/DagReplayPanel's ":scope { display:
     flex }", needed for their own internal layout) has equal specificity to ".nm-shell-pane" and
     comes later in the document, so it would otherwise win the cascade and show every pane at
     once regardless of which tab is active. */
  .nm-shell-pane:not(.active) { display: none !important; }
`;
