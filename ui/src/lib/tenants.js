// Which tenant is the UI looking at?
//
// The query API has no `?app_id=` parameter: the bearer token IS the tenant
// selector (SPEC v1.2 §9.3 — ApiApp.ResolveInternalTenant scans every tenant's
// internal_token and scopes the whole query to the one it matches). That token
// is a SERVER secret, so it must not live in this bundle. The deployment
// therefore gives each tenant its own proxy path, and nginx injects that
// tenant's token on the way through:
//
//   window.EP_TENANTS = [
//     { app_id: 'kefahapp',  base: '/t/kefahapp'  },
//     { app_id: 'itimadapp', base: '/t/itimadapp' },
//   ];
//
// Switching tenants is then just switching which prefix the query calls go to.
// See deploy/nginx-ui.conf.example for the matching location blocks.

import { basePath } from './base.js';

/** The single unnamed mount used when no EP_TENANTS list is configured. */
const IMPLICIT = { app_id: null, base: '' };

/**
 * Where the query API sits when the page declares no tenants. This is the rule
 * the pre-tenant client used (v0.7.0) and it exists for the same reason: the
 * query calls have to stay at or below the path the browser authenticated on,
 * because Basic credentials are cached per directory prefix (RFC 7617 §2.2).
 * Defaulting to the root instead would send a subpath deployment's queries to a
 * path nothing proxies — the SPA's own index.html, served by try_files.
 *
 * `??`, not `||`: an explicit '' is a meaningful override — "the query API is at
 * the root even though the UI is not" — and it is the one a deployment without
 * auth_basic reaches for. Only an unset value falls through.
 */
function implicitBase(win) {
  const override = win?.EP_QUERY_BASE;
  return normalizeMount(override ?? win?.EP_UI_BASE ?? basePath);
}

/**
 * Normalises whatever the page declared into a non-empty tenant list.
 *
 * A deployment that predates the switcher declares no EP_TENANTS at all — one
 * vhost, one injected token — so it falls back to a single implicit mount at
 * EP_QUERY_BASE. Its `app_id` is null because the page genuinely does not know
 * it yet; the server fills it in (see resolveLabel).
 */
export function readTenants(win = typeof window !== 'undefined' ? window : undefined) {
  const declared = Array.isArray(win?.EP_TENANTS) ? win.EP_TENANTS : [];
  const usable = declared
    .filter((entry) => entry && typeof entry === 'object')
    .map((entry) => ({
      app_id: entry.app_id ?? null,
      base: normalizeMount(entry.base),
    }));
  // After the filter, not before it: a list of bare strings — a hand-edited
  // config, or a sub_filter that rewrote the objects away — passes a length
  // check and then filters down to nothing. Returning [] there leaves
  // pickTenant undefined, store.js's subscriber returning early, and the page
  // on "loading tenant…" for good, with no error and no request ever sent.
  if (usable.length === 0) {
    return [{ ...IMPLICIT, base: implicitBase(win) }];
  }
  return usable;
}

/** Mounts are joined to absolute paths, so a trailing slash would double up. */
function normalizeMount(base) {
  return (base || '').replace(/\/+$/, '');
}

/**
 * Picks the active tenant, most explicit source first:
 *   1. `?tenant=<app_id>` — what a shared link carries, so a copied session URL
 *      opens against the tenant it was copied from rather than the reader's
 *      last selection.
 *   2. the last selection, remembered per browser.
 *   3. the first configured tenant.
 * An unknown or stale id falls through rather than leaving the page blank.
 */
export function pickTenant(tenants, { fromUrl = null, remembered = null } = {}) {
  const byId = (id) => (id ? tenants.find((t) => t.app_id === id) : undefined);
  return byId(fromUrl) ?? byId(remembered) ?? tenants[0];
}

const STORAGE_KEY = 'ep.tenant';

export function rememberedTenant(store = safeStorage()) {
  return store?.getItem(STORAGE_KEY) ?? null;
}

export function rememberTenant(appId, store = safeStorage()) {
  if (!store) return;
  if (appId) store.setItem(STORAGE_KEY, appId);
  else store.removeItem(STORAGE_KEY);
}

// Storage throws outright in some privacy modes; a remembered selection is a
// convenience, never a reason for the page to fail to render.
function safeStorage() {
  try {
    return typeof localStorage !== 'undefined' ? localStorage : null;
  } catch {
    return null;
  }
}

/** The `?tenant=` on the current URL, if any. */
export function tenantFromUrl(search = typeof location !== 'undefined' ? location.search : '') {
  return new URLSearchParams(search).get('tenant');
}

/**
 * Adds `?tenant=` to an app path so links keep their tenant. Skipped for the
 * implicit single mount, where the parameter would name nothing.
 */
export function withTenant(path, tenant) {
  if (!tenant?.app_id) return path;
  const separator = path.includes('?') ? '&' : '?';
  return `${path}${separator}tenant=${encodeURIComponent(tenant.app_id)}`;
}
