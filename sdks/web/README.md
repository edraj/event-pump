# event-pump-web

Zero-dependency browser SDK for Event Pump. ~3.7 KB gzipped (budget 8 KB;
`npm run build` reports the current number).

## Script-tag install (IIFE)

Paste this snippet in `<head>` — calls made before `ep.js` loads (including
`setUser`) are queued and drained in order through the S0–S4 init sequence:

```html
<script>
window.ep=window.ep||{q:[]};["init","track","page","setUser","clearUser","identify","flush"].forEach(function(m){window.ep[m]=function(){window.ep.q.push([m,[].slice.call(arguments)])}});
ep.init({ endpoint: 'https://collect.example.com', appToken: 'YOUR_APP_TOKEN' });
</script>
<script async src="https://collect.example.com/static/ep.js"></script>
```

> Serve `ep.js` from your own collect subdomain as shown (first-party, same
> operator as the API). If you must serve it from infrastructure you don't
> control, add `integrity="sha384-..."` (from `openssl dgst -sha384 -binary
> dist/ep.js | openssl base64 -A`) and `crossorigin="anonymous"` — and re-pin
> the hash on every SDK release.

## Bundler / SvelteKit install (ESM)

```ts
import { ep } from 'event-pump-web';

ep.init({ endpoint: 'https://collect.example.com', appToken: 'YOUR_APP_TOKEN' });
ep.track('product_viewed', { sku: 'A1' });
ep.setUser('user-123');   // login only — never rotates anonymous_id
ep.clearUser();           // logout — rotates the session only
```

SvelteKit page tracking (in `+layout.svelte`):

```ts
import { afterNavigate } from '$app/navigation';
import { ep } from 'event-pump-web';
import { trackPages } from 'event-pump-web/sveltekit';

ep.init({ endpoint, appToken });
trackPages(ep, afterNavigate);
```

## Tier-2 X-Event pattern

```ts
fetch('/api/cart/items', {
  method: 'POST',
  headers: { ...ep.eventHeaders('add_to_cart'), 'Content-Type': 'application/json' },
  body: JSON.stringify({ sku }),
});
```

The platform endpoint emits `add_to_cart` server-side via `emit_event()` —
no extra tracking request.

## Behavior guarantees (see /SPEC.md)

- `ep_aid` is **server-set** (~13 months); the SDK only reads it and never
  writes `document.cookie` (Safari ITP).
- No events leave before the session's `/v1/identity` registration completes.
- Sessions rotate after 30 minutes of inactivity (GA4 window).
- Queue persists in localStorage (cap 200), retries 5s/30s/2m, gives up after
  24 h; the server dedupes on `event_id`.
- `_ga`/`_fbp` are parsed, `_fbc` is built from a landing `fbclid`, and
  landing-URL click ids (`gclid`, `fbclid`, `ttclid`, `ScCid`, `twclid`,
  `epik`, `msclkid` — configurable) are harvested automatically.
- SSR-safe: importing and calling on the server is a no-op.

## Develop

```bash
npm install
npm test               # vitest (jsdom)
npm run build          # dist/index.js (ESM) + dist/ep.js (IIFE) + size report
npx playwright test    # real-browser smoke
```
