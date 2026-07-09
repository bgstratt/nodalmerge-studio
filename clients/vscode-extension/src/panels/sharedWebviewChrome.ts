// Slice 0 — Studio Shell. Every view embedded in the shell used to build its own standalone
// vscode.WebviewPanel with its own CSP nonce. Now there is exactly one WebviewPanel and one
// nonce for the whole document; this module holds the bits that used to be copy-pasted across
// MergeReviewPanel.ts, AgentConfigPanel.ts, and WorkspaceDashboardPanel.ts.

import * as vscode from 'vscode';

// ── Read-only diff viewer ────────────────────────────────────────────────────
//
// "Open Diff in Editor" used to open two vscode.workspace.openTextDocument({content})
// buffers — untitled, in-memory documents with no backing file. VS Code lets you type
// into those, which looked editable but silently went nowhere on save (no connection
// back to the branch). Routing both sides through a registered content-provider scheme
// instead makes them genuinely read-only, the same mechanism VS Code's own Git extension
// uses for historical revisions — there's no edit provider for this scheme, so VS Code
// refuses edits outright instead of accepting and discarding them.
//
// Originally private to MergeReviewPanel.ts; moved here (plans/pathways-workspace-history.md
// slice 2 fast-follow) so DagReplayPanel.ts can open the same read-only diff for a Pathways
// node without a second competing registerTextDocumentContentProvider call for the same
// scheme — VS Code only allows one registration per scheme per extension.
export const NM_DIFF_SCHEME = 'nodalmerge-diff';

class ReadOnlyDiffContentProvider implements vscode.TextDocumentContentProvider {
  private readonly contents = new Map<string, string>();

  set(uri: vscode.Uri, content: string): void {
    this.contents.set(uri.toString(), content);
  }

  provideTextDocumentContent(uri: vscode.Uri): string {
    return this.contents.get(uri.toString()) ?? '';
  }
}

let diffProvider: ReadOnlyDiffContentProvider | undefined;
export function getDiffProvider(): ReadOnlyDiffContentProvider {
  if (!diffProvider) {
    diffProvider = new ReadOnlyDiffContentProvider();
    vscode.workspace.registerTextDocumentContentProvider(NM_DIFF_SCHEME, diffProvider);
  }
  return diffProvider;
}

// Shared "open two read-only buffers as a diff" action — same nonce-per-call convention as the
// original MergeReviewPanel implementation: registerTextDocumentContentProvider content is
// looked up by URI, so reusing one across repeat "View Diff" clicks on the same path would show
// whatever the *last* set() call for that path wrote, not necessarily this one.
export async function openReadOnlyDiff(filePath: string, before: string, after: string): Promise<void> {
  const provider = getDiffProvider();
  const nonce = Date.now().toString(36);
  const leftUri = vscode.Uri.parse(`${NM_DIFF_SCHEME}:/${nonce}/base/${filePath}`);
  const rightUri = vscode.Uri.parse(`${NM_DIFF_SCHEME}:/${nonce}/proposed/${filePath}`);
  provider.set(leftUri, before);
  provider.set(rightUri, after);
  const title = filePath + ' (base ↔ proposed, read-only)';
  // Explicit, non-preview, beside the Studio panel's own column — a bare vscode.diff call
  // defaults to a preview tab, which a second "View Diff" click would silently reuse/replace
  // instead of opening its own tab (and could otherwise land in the Studio panel's own column).
  await vscode.commands.executeCommand('vscode.diff', leftUri, rightUri, title, {
    preview: false,
    viewColumn: vscode.ViewColumn.Beside,
  });
}

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
  //
  // Same problem, same fix, for `:root { --nm-*: ...; }`: every view defines its own `--nm-fg`/
  // `--nm-input-bdr`/etc. custom-property palette on :root, assuming it owned the document. :root
  // only ever matches <html>, which is never a descendant of the container div either — so every
  // one of these blocks was silently matching nothing once wrapped in @scope, meaning none of a
  // view's --nm-* custom properties were ever actually set (var() references with no fallback just
  // went invalid). Rewriting to :scope makes the container div itself carry the properties, which
  // then inherit normally to every descendant, exactly like :root does for the whole document.
  const scoped = css
    .replace(/(^|\}|\s)body(\s*\{)/g, '$1:scope$2')
    .replace(/(^|\}|\s):root(\s*\{)/g, '$1:scope$2')
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
    --nm-input-bg:   var(--vscode-input-background, #3c3c3c);
    --nm-input-fg:   var(--vscode-input-foreground, #ccc);
    /* Many themes leave --vscode-input-border unset (flat input design) or set it equal to the
       background, either of which reads as "no separation" against the surrounding UI. Derive a
       subtle border from the foreground color instead, so there's always some visible contrast
       regardless of what the active theme does with inputBorder. */
    --nm-input-bdr:  color-mix(in srgb, var(--nm-fg) 25%, transparent);
    --nm-font:       var(--vscode-font-family);
    --nm-mono:       var(--vscode-editor-font-family, monospace);
    --nm-size:       var(--vscode-font-size, 13px);
    --nm-success:    #4dac26;
    --nm-warn:       #cca700;
    --nm-error:      #f14c4c;
    --nm-info:       var(--vscode-textLink-foreground, #3794ff);
  }
  * { box-sizing: border-box; }
  html, body {
    background: var(--nm-bg); color: var(--nm-fg);
    font-family: var(--nm-font); font-size: var(--nm-size);
    margin: 0; padding: 0; height: 100%; overflow: hidden;
  }
  body { display: flex; flex-direction: column; }
  /* Shared, theme-correct base for every view's native form controls. A view can still override
     these locally (its own scoped rule wins over this unscoped one at equal specificity — see
     CSS scoping proximity), but views that don't bother get sane non-white, bordered defaults
     instead of raw browser UA styling. */
  select, textarea, input[type=text] {
    background: var(--nm-input-bg); color: var(--nm-input-fg); border: 1px solid var(--nm-input-bdr);
    border-radius: 3px; padding: 4px 6px; font-family: var(--nm-font); font-size: 0.9em;
  }
  button {
    background: var(--nm-btn); color: var(--nm-btn-fg); border: 1px solid var(--nm-input-bdr);
    border-radius: 3px; padding: 4px 10px; font-family: var(--nm-font); font-size: 0.9em; cursor: pointer;
  }
  button:hover { background: var(--nm-btn-hover); }
  button.ghost { background: transparent; color: var(--nm-fg); border: 1px solid var(--nm-border); }
  button.ghost:hover { background: color-mix(in srgb, var(--nm-border) 50%, transparent); }
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
