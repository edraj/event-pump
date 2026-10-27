import { expect, test } from '@playwright/test';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = fileURLToPath(new URL('.', import.meta.url));
const EP_JS = readFileSync(join(here, '..', 'dist', 'ep.js'), 'utf8');

// The copy-paste async stub snippet from the README, verbatim.
const SNIPPET =
  `window.ep=window.ep||{q:[]};` +
  `["init","track","page","setUser","clearUser","identify","reportError","flush"]` +
  `.forEach(function(m){window.ep[m]=function(){window.ep.q.push([m,[].slice.call(arguments)])}});`;

const PAGE_HTML = `<!doctype html><html><head>
<script>${SNIPPET}
ep.init({ endpoint: 'https://collect.test', tenantApiKey: 'test-key' });
ep.setUser('u-7');
ep.track('product_viewed', { sku: 'A1' });
</script>
<script async src="/ep.js"></script>
</head><body>smoke</body></html>`;

test('IIFE against a mock server: identity-before-events and sendBeacon on hidden', async ({ page }) => {
  const requests: { url: string; type: string; body: unknown }[] = [];

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
      type: route.request().resourceType(),
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
  // Pick the beacon by type, not position: if the earlier batch's response is
  // still in flight, its ack re-flushes B2 as a plain fetch that can land after
  // the beacon (the server dedupes on event_id, SPEC §7).
  const beaconOf = () => requests.slice(beaconCount).find((r) => r.type === 'ping');
  await expect.poll(beaconOf).toBeTruthy();
  const beacon = beaconOf()!;
  // SPEC §9.1: sendBeacon carries app_id as a query param (headers unavailable).
  expect(beacon.url).toBe('https://collect.test/v1/events?tenant_api_key=test-key');
  expect((beacon.body as any).events.map((e: any) => e.properties?.sku)).toContain('B2');
});
