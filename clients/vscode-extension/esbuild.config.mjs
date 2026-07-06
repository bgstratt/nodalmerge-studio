import * as esbuild from 'esbuild';

const production = process.argv.includes('--production');
const watch = process.argv.includes('--watch');

/** @type {import('esbuild').BuildOptions} */
const extensionOptions = {
  entryPoints: ['src/extension.ts'],
  bundle: true,
  format: 'cjs',
  minify: production,
  sourcemap: !production,
  sourcesContent: false,
  platform: 'node',
  outfile: 'out/extension.js',
  // vscode is injected by the extension host at runtime — never bundle it
  external: ['vscode'],
  logLevel: 'info',
};

/** @type {import('esbuild').BuildOptions} */
const dagReplayOptions = {
  entryPoints: ['src/webviews/dag-replay/main.ts'],
  bundle: true,
  format: 'iife',   // browser WebView — not CommonJS
  minify: production,
  sourcemap: !production,
  sourcesContent: false,
  platform: 'browser',
  outfile: 'out/dag-replay.js',
  logLevel: 'info',
};

/** Studio Shell views extracted from the panels' former inline <script> template strings.
 * @type {import('esbuild').BuildOptions} */
const studioViewsOptions = {
  entryPoints: ['src/webviews/views/main.js'],
  bundle: true,
  format: 'iife',   // browser WebView — not CommonJS
  minify: production,
  sourcemap: !production,
  sourcesContent: false,
  platform: 'browser',
  outfile: 'out/studio-views.js',
  logLevel: 'info',
};

const allOptions = [extensionOptions, dagReplayOptions, studioViewsOptions];

if (watch) {
  const contexts = await Promise.all(allOptions.map(o => esbuild.context(o)));
  await Promise.all(contexts.map(c => c.watch()));
  console.log('[esbuild] watching...');
} else {
  await Promise.all(allOptions.map(o => esbuild.build(o)));
}
