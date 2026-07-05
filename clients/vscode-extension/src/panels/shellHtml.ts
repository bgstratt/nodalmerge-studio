// Pure composition of the Studio Shell webview document. No vscode imports — this module is
// shared between StudioShellPanel (production) and the webview smoke-test harness, which needs
// to produce the exact production document outside a VS Code extension host.

import { SHELL_CSS_VARS } from './sharedWebviewChrome';

// Views ship no inline script: all view JS lives in the out/studio-views.js bundle
// (src/webviews/views/), loaded via viewsScriptTag below.
export interface ViewFragment { css: string; html: string }
export interface ScriptSrcFragment { css: string; html: string; scriptTag: string }

export interface ShellComposition {
  nonce: string;
  wsOrigin: string;
  /** webview.cspSource — lets DevTools fetch the bundles' source maps in dev (connect-src).
   * Packaged builds ship no .map files, so this only matters under F5. */
  resourceCspSource?: string;
  goalWorkspace: ViewFragment;
  modelAgentStudio: ViewFragment;
  executionTimeline: ViewFragment;
  decisionConvergence: ViewFragment;
  trajectory: ScriptSrcFragment;
  insights: ViewFragment;
  projectionComparison: ViewFragment;
  /** Nonced <script src> tag for out/studio-views.js — the extracted view modules. */
  viewsScriptTag: string;
  /** [containerId, label] pairs, in tab order; first tab starts active. */
  tabs: Array<[string, string]>;
}

// style-src below intentionally omits a nonce: VS Code's webview host injects the current
// theme's CSS custom properties via inline style attributes on load, and pairing 'unsafe-inline'
// with a nonce/hash on the same directive makes browsers disregard 'unsafe-inline' entirely
// (CSP3 backwards-compat rule) — that blocked the injection and a host-side fallback path threw
// a document.write() SyntaxError that aborted parsing the rest of the page, silently preventing
// every script tag after the failure point (including Decision Convergence's) from ever running.
export function composeShellHtml(c: ShellComposition): string {
  const tabButtonsHtml = c.tabs
    .map(([id, label], i) => `<button class="nm-shell-tab${i === 0 ? ' active' : ''}" data-tab="${id}">${label}</button>`)
    .join('\n');

  return `<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="UTF-8">
  <meta http-equiv="Content-Security-Policy"
        content="default-src 'none'; style-src 'unsafe-inline'; script-src 'nonce-${c.nonce}'; connect-src ${c.wsOrigin} ${c.wsOrigin}/*${c.resourceCspSource ? ' ' + c.resourceCspSource : ''};">
  <meta name="viewport" content="width=device-width, initial-scale=1.0">
   <title>NodalMerge Studio</title>
  <style nonce="${c.nonce}">
${SHELL_CSS_VARS}
${c.goalWorkspace.css}
${c.modelAgentStudio.css}
${c.executionTimeline.css}
${c.decisionConvergence.css}
${c.trajectory.css}
${c.insights.css}
${c.projectionComparison.css}
  </style>
</head>
<body>
  <div id="nm-shell-tabbar">${tabButtonsHtml}</div>
  <div id="nm-shell-content">
${c.goalWorkspace.html}
${c.modelAgentStudio.html}
${c.executionTimeline.html}
${c.decisionConvergence.html}
${c.trajectory.html}
${c.insights.html}
${c.projectionComparison.html}
  </div>
${c.viewsScriptTag}
${c.trajectory.scriptTag}
</body>
</html>`;
}
