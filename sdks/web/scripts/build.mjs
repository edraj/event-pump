import { build } from 'esbuild';
import { execFileSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
import { gzipSync } from 'node:zlib';

const BUDGET_GZIP_BYTES = 8 * 1024; // SPEC: <= 8KB gzipped, number reported

// ESM build for bundlers / SvelteKit
await build({
  entryPoints: ['src/index.ts'],
  outfile: 'dist/index.js',
  bundle: true,
  format: 'esm',
  target: 'es2020',
  sourcemap: true,
});

await build({
  entryPoints: ['sveltekit/index.ts'],
  outfile: 'dist/sveltekit/index.js',
  bundle: true,
  format: 'esm',
  target: 'es2020',
});

// IIFE build for plain <script> injection (ep.js)
await build({
  entryPoints: ['src/iife.ts'],
  outfile: 'dist/ep.js',
  bundle: true,
  format: 'iife',
  target: 'es2020',
  minify: true,
});

execFileSync('npx', ['tsc', '--emitDeclarationOnly'], { stdio: 'inherit' });

const raw = readFileSync('dist/ep.js');
const gzipped = gzipSync(raw, { level: 9 }).length;
console.log(`ep.js: ${raw.length} bytes raw, ${gzipped} bytes gzipped (budget ${BUDGET_GZIP_BYTES})`);
if (gzipped > BUDGET_GZIP_BYTES) {
  console.error(`FAIL: ep.js exceeds the ${BUDGET_GZIP_BYTES}-byte gzip budget`);
  process.exit(1);
}
