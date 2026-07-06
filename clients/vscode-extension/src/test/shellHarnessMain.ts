// Webview smoke-test harness entry. Bundled by scripts/webview-smoke.mjs with 'vscode'
// aliased to vscodeStub.ts, then run under Node to emit test-out/shell.html — the exact
// document StudioShellPanel puts in the webview, except:
//   1. the dag-replay bundle is referenced by relative path instead of a vscode-webview URI;
//   2. a stub acquireVsCodeApi() is injected as the first script in <body>, standing in for
//      the function the VS Code webview host provides. Posted messages are recorded on
//      window.__nmSent so the smoke test can assert on them.

import * as fs from 'fs';
import * as path from 'path';
import { composeShellHtml } from '../panels/shellHtml';
import { STUDIO_SHELL_TABS } from '../panels/StudioShellPanel';
import { GoalWorkspacePanel } from '../panels/ArtifactExplorerPanel';
import { ModelAgentStudioPanel } from '../panels/AgentConfigPanel';
import { ExecutionTimelinePanel } from '../panels/WorkspaceDashboardPanel';
import { DecisionConvergencePanel } from '../panels/MergeReviewPanel';
import { TrajectoryReplayPanel } from '../panels/DagReplayPanel';
import { InsightsPanel } from '../panels/InsightsPanel';
import { ProjectionComparisonPanel } from '../panels/ProjectionComparisonPanel';

const outDir = process.argv[2];
if (!outDir) { throw new Error('usage: node harness.cjs <outDir>'); }

const nonce = 'smoketestnonce0000000000000000aa';

const html = composeShellHtml({
  nonce,
  wsOrigin: 'ws://127.0.0.1:5080',
  viewsScriptTag: `<script nonce="${nonce}" src="studio-views.js"></script>`,
  goalWorkspace: GoalWorkspacePanel.getFragment(),
  modelAgentStudio: ModelAgentStudioPanel.getFragment(),
  executionTimeline: ExecutionTimelinePanel.getFragment(),
  decisionConvergence: DecisionConvergencePanel.getFragment(),
  trajectory: TrajectoryReplayPanel.getFragmentForScriptSrc('dag-replay.js', nonce),
  insights: InsightsPanel.getFragment(),
  projectionComparison: ProjectionComparisonPanel.getFragment(),
  tabs: STUDIO_SHELL_TABS,
});

const stubScript = `<script nonce="${nonce}">
  window.__nmSent = [];
  window.acquireVsCodeApi = function() {
    return {
      postMessage: function(m) { window.__nmSent.push(m); },
      getState: function() { return undefined; },
      setState: function() {},
    };
  };
</script>`;

const marker = '<body>';
if (!html.includes(marker)) { throw new Error('harness: <body> marker not found in shell html'); }
const testHtml = html.replace(marker, marker + '\n' + stubScript);

fs.mkdirSync(outDir, { recursive: true });
fs.writeFileSync(path.join(outDir, 'shell.html'), testHtml);
// scripts/webview-smoke.mjs runs this from the extension root, where out/ lives
for (const bundle of ['dag-replay.js', 'studio-views.js']) {
  fs.copyFileSync(
    path.resolve(process.cwd(), 'out', bundle),
    path.join(outDir, bundle),
  );
}
console.log('[harness] wrote', path.join(outDir, 'shell.html'));
