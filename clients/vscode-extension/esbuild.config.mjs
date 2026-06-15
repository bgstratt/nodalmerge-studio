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

if (watch) {
  const [extCtx, dagCtx] = await Promise.all([
    esbuild.context(extensionOptions),
    esbuild.context(dagReplayOptions),
  ]);
  await Promise.all([extCtx.watch(), dagCtx.watch()]);
  console.log('[esbuild] watching...');
} else {
  await Promise.all([
    esbuild.build(extensionOptions),
    esbuild.build(dagReplayOptions),
  ]);
}
