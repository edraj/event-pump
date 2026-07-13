// Drives the IIFE build in a real headless page against a live local API:
// stub snippet -> init -> setUser -> track -> identify -> flush.
// Prints the session eventHeaders() JSON on stdout for the SQL producer step.
import { chromium } from '@playwright/test';
import { readFileSync } from 'node:fs';

const endpoint = process.env.EP_ENDPOINT ?? 'http://127.0.0.1:9701';
const token = process.env.EP_TOKEN ?? 'smoke-token';
const epJs = readFileSync(new URL('../dist/ep.js', import.meta.url), 'utf8');

const SNIPPET =
  `window.ep=window.ep||{q:[]};` +
  `["init","track","page","setUser","clearUser","identify","flush"]` +
  `.forEach(function(m){window.ep[m]=function(){window.ep.q.push([m,[].slice.call(arguments)])}});`;

const html = `<!doctype html><html><head>
<script>${SNIPPET}
ep.init({ endpoint: '${endpoint}', appToken: '${token}' });
ep.setUser('smoke-user');
ep.track('product_viewed', { sku: 'SMOKE1' });
</script>
<script>${epJs}</script>
</head><body>smoke</body></html>`;

// Chromium's Local Network Access enforcement wants an interactive permission
// for cross-origin loopback fetches — impossible headless, and irrelevant to
// production (same-site public HTTPS). Disabled for this mock-only environment.
const browser = await chromium.launch({
  args: ['--disable-features=LocalNetworkAccessChecks'],
});
const diagnostics = [];
try {
  const page = await browser.newPage();
  page.on('console', (msg) => diagnostics.push(`console.${msg.type()}: ${msg.text()}`));
  page.on('pageerror', (error) => diagnostics.push(`pageerror: ${error.message}`));
  page.on('requestfailed', (request) =>
    diagnostics.push(`requestfailed: ${request.url()} ${request.failure()?.errorText}`));
  page.on('response', (response) => {
    if (response.status() >= 400) diagnostics.push(`response ${response.status()}: ${response.url()}`);
  });
  await page.route('http://127.0.0.1:9704/**', (route) =>
    route.fulfill({ contentType: 'text/html', body: html }),
  );

  const identityDone = page.waitForResponse(
    (r) => r.url().includes('/v1/identity') && r.status() < 300,
    { timeout: 15_000 },
  );
  await page.goto('http://127.0.0.1:9704/');
  await identityDone;

  await page.evaluate(() =>
    window.ep.identify({ ga4_client_id: '555.666', ga4_session_id: '1700000001' }),
  );
  const eventsDone = page.waitForResponse(
    (r) => r.url().includes('/v1/events') && r.status() === 200,
  );
  await page.evaluate(() => window.ep.flush());
  await eventsDone;

  const headers = await page.evaluate(() => window.ep.eventHeaders());
  console.log(JSON.stringify(headers));
} catch (error) {
  console.error('smoke-web failed:', error.message);
  for (const line of diagnostics) console.error('  ' + line);
  process.exitCode = 1;
} finally {
  await browser.close();
}
