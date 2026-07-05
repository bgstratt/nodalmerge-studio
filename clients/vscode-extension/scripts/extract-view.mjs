// One-time migration tool: extracts a view's inline-JS template constant (e.g. IN_JS in
// InsightsPanel.ts) into a real module under src/webviews/views/.
//
// The constants are static template literals, so their bodies carry template-literal
// escaping (\` and \\ — the historical source of double-escaping bugs like /\\s+/ meaning
// /\s+/ at runtime). Rather than hand-unescaping, this EVALUATES the literal with Node to
// recover the exact runtime string, then applies the same textual rewrite the old runtime
// wrapViewScript()/scopeViewScript() applied on every load (getElementById -> $ etc.), so
// the committed module is byte-equivalent to what actually executed in the webview before.
//
// Usage: node scripts/extract-view.mjs <panelFile.ts> <CONST_NAME> <outFile.js>

import * as fs from 'fs';
import * as vm from 'vm';

const [panelFile, constName, outFile] = process.argv.slice(2);
if (!panelFile || !constName || !outFile) {
  console.error('usage: node scripts/extract-view.mjs <panelFile.ts> <CONST_NAME> <outFile.js>');
  process.exit(1);
}

const src = fs.readFileSync(panelFile, 'utf8');

// Match `const NAME = ` + backtick ... backtick + `;` where the closing backtick starts a line.
const startRe = new RegExp(String.raw`const ${constName} = \``);
const startMatch = startRe.exec(src);
if (!startMatch) { throw new Error(`const ${constName} = \` not found in ${panelFile}`); }
const bodyStart = startMatch.index + startMatch[0].length;
const endMarker = '\n`;';
const bodyEnd = src.indexOf(endMarker, bodyStart);
if (bodyEnd === -1) { throw new Error(`closing \n\`; for ${constName} not found`); }
const literalBody = src.slice(bodyStart, bodyEnd);
if (literalBody.includes('${')) { throw new Error(`${constName} contains \${} interpolation — not extractable as-is`); }

// Recover the runtime string exactly as TS/esbuild would have produced it.
const runtime = vm.runInNewContext('`' + literalBody + '\n`');

// Same rewrite scopeViewScript() applied at runtime, plus dropping the acquireVsCodeApi
// line entirely — the module wrapper supplies `vscode` from ctx instead.
const scoped = runtime
  .replace(/(?:var|const|let)\s+vscode\s*=\s*acquireVsCodeApi\(\)\s*;/, '// vscode supplied by ctx (was: acquireVsCodeApi())')
  .replace(/document\.getElementById\(/g, '$(')
  .replace(/document\.querySelectorAll\(/g, 'root.querySelectorAll(')
  .replace(/document\.querySelector\(/g, 'root.querySelector(');

const moduleText = `// Extracted from ${panelFile.replace(/\\/g, '/')} (${constName}) by scripts/extract-view.mjs.
// Body is the exact runtime string of the former inline <script>, with the historical
// scopeViewScript() rewrite (getElementById -> $, querySelector(All) -> root.*) baked in.

/** @param {{ root: HTMLElement, vscode: { postMessage(m: any): void }, $: (id: string) => HTMLElement | null }} ctx */
export function init(ctx) {
  var root = ctx.root;
  var vscode = ctx.vscode;
  var $ = ctx.$;
${scoped}
}
`;

fs.writeFileSync(outFile, moduleText);
console.log(`[extract-view] wrote ${outFile} (${scoped.length} chars of view code)`);
