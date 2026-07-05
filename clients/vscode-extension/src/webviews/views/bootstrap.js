// Shell bootstrap — formerly the first inline <script> in the Studio Shell document.
// Must run before any view init: acquires the vscode API exactly once (calling
// acquireVsCodeApi twice in a webview throws) and installs the global error relays the
// extension side logs via the nm-webview-error message.

export function bootstrapShell() {
  window.__nmVscode = acquireVsCodeApi();
  window.onerror = function (msg, src, line, col, err) {
    var stack = (err && err.stack) || (src + ':' + line + ':' + col);
    window.__nmVscode.postMessage({ type: 'nm-webview-error', message: String(msg), stack: stack });
    return false;
  };
  window.onunhandledrejection = function (event) {
    var r = event.reason;
    window.__nmVscode.postMessage({ type: 'nm-webview-error', message: String(r), stack: (r && r.stack) || '' });
  };

  var tabButtons = document.querySelectorAll('.nm-shell-tab');
  var panes = document.querySelectorAll('#nm-shell-content > .nm-shell-pane');
  function showTab(tabId) {
    tabButtons.forEach(function (b) { b.classList.toggle('active', b.getAttribute('data-tab') === tabId); });
    panes.forEach(function (p) { p.classList.toggle('active', p.id === tabId); });
    window.__nmVscode.postMessage({ type: 'studio.tabActivated', tab: tabId });
  }
  tabButtons.forEach(function (b) {
    b.addEventListener('click', function () { showTab(b.getAttribute('data-tab')); });
  });
  window.addEventListener('message', function (event) {
    var msg = event.data;
    if (msg && msg.type === 'studio.showTab') { showTab(msg.tab); }
  });
}
