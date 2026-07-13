import { expect, test } from '@playwright/test';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = fileURLToPath(new URL('.', import.meta.url));
const EP_JS = readFileSync(join(here, '..', 'dist', 'ep.js'), 'utf8');

// The copy-paste async stub snippet from the README, verbatim.
const SNIPPET =
  `window.ep=window.ep||{q:[]};` +
  `["init","track","page","setUser","clearUser","identify","flush"]` +
  `.forEach(function(m){window.ep[m]=function(){window.ep.q.push([m,[].slice.call(arguments)])}});`;

const PAGE_HTML = `<!doctype html><html><head>
<script>${SNIPPET}
ep.init({ endpoint: 'https://collect.test', appToken: 'tok-web' });
ep.setUser('u-7');
ep.track('product_viewed', { sku: 'A1' });
</script>
<script async src="/ep.js"></script>
</head><body>smoke</body></html>`;

test('IIFE against a mock server: identity-before-events and sendBeacon on hidden', async ({ page }) => {
  const requests: { url: string; body: unknown }[] = [];

  await page.route('https://app.test/**', async (route) => {
    const url = route.request().url();
    if (url.endsWith('/ep.js')) {
      await route.fulfill({ contentType: 'application/javascript', body: EP_JS });
    } else {
      await route.fulfill({ contentType: 'text/html', body: PAGE_HTML });
    }
  });

  await page.route('https://collect.test/**', async (route) => {
    requests.push({
      url: route.request().url(),
      body: route.request().postData() ? JSON.parse(route.request().postData()!) : null,
    });
    if (route.request().url().includes('/v1/identity')) {
      await route.fulfill({ status: 204, body: '' });
    } else {
      await route.fulfill({
        contentType: 'application/json',
        body: '{"accepted":1,"rejected":[]}',
      });
    }
  });

  await page.goto('https://app.test/');
  await page.waitForFunction(() => typeof (window as any).ep?.eventHeaders === 'function');

  // stub drained: identity registered first, with the pre-load setUser applied
  await expect.poll(() => requests.some((r) => r.url.includes('/v1/identity'))).toBe(true);
  const firstCollect = requests[0]!;
  expect(firstCollect.url).toContain('/v1/identity');
  expect((firstCollect.body as any).user_id).toBe('u-7');

  // flush the buffered pre-load event and verify it arrives after identity
  await page.evaluate(() => (window as any).ep.flush());
  await expect
    .poll(() =>
      requests
        .filter((r) => r.url.includes('/v1/events'))
        .flatMap((r) => (r.body as any).events)
        .map((e: any) => e.event_name),
    )
    .toContain('product_viewed');

  // hidden => sendBeacon flush with the token as a query param
  await page.evaluate(() => (window as any).ep.track('product_viewed', { sku: 'B2' }));
  const beaconCount = requests.length;
  await page.evaluate(() => {
    Object.defineProperty(document, 'visibilityState', { value: 'hidden', configurable: true });
    document.dispatchEvent(new Event('visibilitychange'));
  });
  await expect.poll(() => requests.length).toBeGreaterThan(beaconCount);
  const beacon = requests.at(-1)!;
  expect(beacon.url).toBe('https://collect.test/v1/events?token=tok-web');
  expect((beacon.body as any).events.map((e: any) => e.properties?.sku)).toContain('B2');
});
