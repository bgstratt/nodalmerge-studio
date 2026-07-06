// Webview smoke test: builds the exact production Studio Shell document (via the same
// composeShellHtml the extension uses), loads it in headless Chromium with CSP enforced,
// and fails on any load-time breakage: page errors, console errors, the shell's own
// NM-FATAL error banners, or nm-webview-error messages posted back to the (stubbed) host.
// Run: npm run webview-smoke   (requires `npm run compile` output in out/)

import * as esbuild from 'esbuild';
import { chromium } from 'playwright';
import { execFileSync } from 'child_process';
import * as fs from 'fs';
import * as path from 'path';
import { fileURLToPath, pathToFileURL } from 'url';

const extRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const testOut = path.join(extRoot, 'test-out');
process.chdir(extRoot);

// ── 1. bundle + run the harness entry (vscode aliased to the stub) ──────────
await esbuild.build({
  entryPoints: ['src/test/shellHarnessMain.ts'],
  bundle: true,
  format: 'cjs',
  platform: 'node',
  outfile: 'test-out/harness.cjs',
  alias: { vscode: path.join(extRoot, 'src', 'test', 'vscodeStub.ts') },
  logLevel: 'warning',
});
execFileSync(process.execPath, ['test-out/harness.cjs', testOut], { stdio: 'inherit' });

// ── 2. drive it in Chromium ──────────────────────────────────────────────────
const failures = [];
const browser = await chromium.launch();
const page = await (await browser.newContext()).newPage();

page.on('pageerror', err => failures.push(`pageerror: ${err.message}`));
page.on('console', msg => {
  if (msg.type() === 'error') { failures.push(`console.error: ${msg.text()}`); }
});

await page.goto(pathToFileURL(path.join(testOut, 'shell.html')).href);
await page.waitForTimeout(1000); // let init code settle

// The shell's own error trap renders NM-FATAL banners and posts nm-webview-error.
const fatalBanners = await page.evaluate(() =>
  Array.from(document.querySelectorAll('div'))
    .map(d => d.textContent || '')
    .filter(t => t.startsWith('NM-FATAL'))
    .map(t => t.slice(0, 300)));
for (const t of fatalBanners) { failures.push(`fatal banner: ${t}`); }

const errorMessages = await page.evaluate(() =>
  (window.__nmSent || []).filter(m => m && m.type === 'nm-webview-error'));
for (const m of errorMessages) { failures.push(`nm-webview-error: ${m.message}\n${m.stack || ''}`); }

// Structure: every tab button and pane present, initial active pane is the first tab.
const tabs = await page.$$eval('.nm-shell-tab', els => els.map(e => e.getAttribute('data-tab')));
const panes = await page.$$eval('#nm-shell-content > .nm-shell-pane', els => els.map(e => e.id));
if (tabs.length !== 7) { failures.push(`expected 7 tabs, got ${tabs.length}`); }
for (const t of tabs) {
  if (!panes.includes(t)) { failures.push(`tab ${t} has no matching pane (panes: ${panes.join(', ')})`); }
}

// Click through every tab; assert exactly that pane activates and becomes visible.
for (const t of tabs) {
  await page.click(`.nm-shell-tab[data-tab="${t}"]`);
  const active = await page.$$eval('#nm-shell-content > .nm-shell-pane.active', els => els.map(e => e.id));
  if (active.length !== 1 || active[0] !== t) {
    failures.push(`after clicking ${t}, active panes = [${active.join(', ')}]`);
  }
  const visible = await page.$eval(`#${t}`, el => el.offsetWidth > 0 && el.offsetHeight > 0);
  if (!visible) { failures.push(`pane ${t} not visible after activating its tab`); }
}
await page.waitForTimeout(500); // catch errors triggered by tab activation

// Tab activation must have been reported to the host for each click.
const activated = await page.evaluate(() =>
  (window.__nmSent || []).filter(m => m && m.type === 'studio.tabActivated').map(m => m.tab));
for (const t of tabs) {
  if (!activated.includes(t)) { failures.push(`no studio.tabActivated message for ${t}`); }
}

// ── 3. per-view message probes ───────────────────────────────────────────────
// Each migrated view gets one representative host→webview message; we assert the view
// renders it (exercising its render + escaping paths, not just load-time wiring).
// `expectText` strings must all appear somewhere under `selector` afterwards. Payloads
// deliberately include HTML-special characters to catch escaping regressions.
const XSS = `<img src=x onerror="window.__nmXss=1">'&"`;
const probes = [
  {
    name: 'insights renders analysis result',
    message: {
      type: 'insightsResult',
      data: {
        generatedAt: '2026-07-05T12:00:00Z', since: null,
        averageReworkCycles: 1.25,
        topFailureCause: { category: 'Build ' + XSS, totalCount: 4 },
        mostSuccessfulModel: null, mostSuccessfulStrategy: null,
        totalSessions: 3, totalWorkUnits: 5, overallSuccessRate: 0.8,
        workUnitsByStatus: { Active: 2 },
        modelPerformance: [{ model: 'claude-sonnet-5', provider: 'anthropic', proposalCount: 2, mergedCount: 1, rejectedCount: 1, acceptanceRate: 0.5, avgConfidence: 0.9 }],
        modelPerformanceByStage: [], forkWinRates: [], forkConstraintWinRates: [],
        failureCauses: [], reviewOutcomes: [],
      },
    },
    selector: '#shell-pane-insights',
    expectText: ['Retrospective Highlights', 'claude-sonnet-5', 'Build ' + XSS],
  },
  {
    name: 'projection snapshots renders list',
    message: {
      type: 'pcSnapshotList',
      snapshots: [{ snapshotId: 'snap-abcdef123456', workUnitId: 'wu-' + XSS, createdAt: '2026-07-05T12:00:00Z' }],
    },
    selector: '#shell-pane-projection-comparison',
    expectText: ['wu-' + XSS, 'Work Unit'],
  },
  {
    name: 'model & agent studio renders config',
    message: {
      type: 'config',
      profiles: [{ id: 'coder-1', label: 'Coder ' + XSS, domain: 'code', provider: 'anthropic', model: 'claude-sonnet-5' }],
      credentialStatus: {}, templates: [], defaultTopology: '', pipelineProfiles: [],
      domainAgents: [], enabledDomainAgents: [],
    },
    selector: '#shell-pane-model-agent-studio',
    expectText: ['coder-1', 'Coder ' + XSS, 'claude-sonnet-5'],
  },
  {
    name: 'activity center renders work units',
    message: {
      type: 'data',
      workUnits: [{ workUnitId: 'wu-1', goal: 'Fix parser ' + XSS, owner: 'agent-a', status: 'Active', branchId: 'b1' }],
      agents: [], merges: [], awaitingResume: [], clarifications: [], deadLetters: [], fileLeases: [],
    },
    selector: '#shell-pane-execution-timeline',
    expectText: ['Fix parser ' + XSS],
  },
  {
    name: 'review renders proposal',
    message: {
      type: 'proposal',
      proposal: {
        proposalId: 'p1', status: 'Pending', goal: 'Merge goal ' + XSS,
        sourceBranch: 'feature/x', targetBranch: 'main', confidence: 0.75,
        summary: 'summary text', changeDescription: 'change desc',
      },
      fileChanges: [],
    },
    selector: '#shell-pane-decision-convergence',
    expectText: ['Merge goal ' + XSS, 'feature/x', '75%'],
  },
];

for (const probe of probes) {
  await page.evaluate(m => window.postMessage(m, '*'), probe.message);
  await page.waitForTimeout(150);
  const text = await page.$eval(probe.selector, el => el.textContent || '');
  for (const expected of probe.expectText) {
    if (!text.includes(expected)) {
      failures.push(`probe "${probe.name}": expected text ${JSON.stringify(expected)} not found in ${probe.selector}`);
    }
  }
  const xssFired = await page.evaluate(() => window.__nmXss === 1);
  if (xssFired) { failures.push(`probe "${probe.name}": XSS payload executed — escaping regression`); }
}
await page.waitForTimeout(300); // let any probe-triggered async errors surface

const lateErrors = await page.evaluate(() =>
  (window.__nmSent || []).filter(m => m && m.type === 'nm-webview-error'));
for (const m of lateErrors.slice(errorMessages.length)) {
  failures.push(`nm-webview-error (probe phase): ${m.message}\n${m.stack || ''}`);
}

await browser.close();

if (failures.length > 0) {
  console.error(`\n[webview-smoke] FAIL — ${failures.length} problem(s):`);
  for (const f of failures) { console.error('  - ' + f); }
  process.exit(1);
}
console.log(`[webview-smoke] PASS — ${tabs.length} tabs, no page/console errors, no NM-FATAL.`);
