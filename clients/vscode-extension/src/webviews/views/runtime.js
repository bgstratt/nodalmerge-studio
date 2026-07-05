// Studio Shell view runtime. Replaces the wrapViewScript() string-munging in
// sharedWebviewChrome.ts with the same semantics as real code: each view runs against a
// `root` container element, a shared vscode API handle (acquired once by the shell
// bootstrap as window.__nmVscode), and a `$` helper scoped to that container so duplicate
// ids across views can't collide. Failures render the same NM-FATAL banner and post the
// same nm-webview-error message the extension side already logs.

/**
 * @param {string} containerId
 * @param {(ctx: { root: HTMLElement, vscode: { postMessage(m: any): void }, $: (id: string) => HTMLElement | null }) => void} initFn
 */
export function runView(containerId, initFn) {
  try {
    var root = document.getElementById(containerId);
    if (!root) { throw new Error('container not found: ' + containerId); }
    var $ = function (id) { return root.querySelector('#' + id); };
    initFn({ root: root, vscode: window.__nmVscode, $: $ });
  } catch (nmWrapErr) {
    var nmErrRoot = document.getElementById(containerId);
    var nmErrDiv = document.createElement('div');
    nmErrDiv.style.cssText = 'color:#f14c4c;font-family:monospace;white-space:pre-wrap;padding:12px;';
    nmErrDiv.textContent = 'NM-FATAL[' + containerId + ']: ' + (nmWrapErr && nmWrapErr.stack || nmWrapErr);
    if (nmErrRoot) { nmErrRoot.prepend(nmErrDiv); } else { document.body.prepend(nmErrDiv); }
    if (window.__nmVscode) {
      window.__nmVscode.postMessage({
        type: 'nm-webview-error',
        containerId: containerId,
        message: String(nmWrapErr),
        stack: (nmWrapErr && nmWrapErr.stack) || '',
      });
    }
  }
}
