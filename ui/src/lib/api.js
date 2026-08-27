// Query client for /internal/v1/query (same-origin; nginx proxies + gates it).

import { basePath } from './base.js';

/**
 * Where the query API is mounted. Defaults to the UI's own base path, which
 * keeps the query calls at or below the path the browser authenticated on:
 * Basic credentials are cached per directory prefix (RFC 7617 §2.2), so a
 * query API parked at a sibling path would receive these fetches with no
 * Authorization header and nginx would 401 them. '' at a root deployment, so
 * the URLs are unchanged there. Override for unusual setups:
 * window.EP_QUERY_BASE = 'https://...'.
 *
 * `??`, not `||`: '' is a meaningful override — "the query API is at the root
 * even though the UI is not" — and it is the one a deployment without
 * auth_basic would reach for. Only an unset value should fall through.
 */
export function apiBase() {
  const override = typeof window !== 'undefined' ? window.EP_QUERY_BASE : undefined;
  return override ?? basePath;
}

/**
 * The query endpoints authenticate with the tenant's server-side
 * `internal_token`. In the deployed setup nginx injects the Authorization
 * header (see deploy/nginx-ui.conf.example) and this returns nothing — the
 * token stays server-side, which is where a server secret belongs. Setting
 * `window.EP_QUERY_TOKEN` is the escape hatch for a dev box talking straight
 * to EP_INTERNAL_LISTEN with no proxy in front; never do it on a page anyone
 * else can load.
 */
export function apiToken() {
  return (typeof window !== 'undefined' && window.EP_QUERY_TOKEN) || '';
}

/** Builds the events query URL; empty/blank filters are omitted. */
export function eventsUrl(filters = {}, { cursor = null, limit = 50 } = {}) {
  const params = new URLSearchParams();
  for (const key of [
    'event_name',
    'origin',
    'user_id',
    'anonymous_id',
    'session_key',
    'destination',
    'status',
    'from',
    'to',
  ]) {
    const value = (filters[key] ?? '').toString().trim();
    if (value) params.set(key, value);
  }
  params.set('limit', String(limit));
  if (cursor) params.set('cursor', cursor);
  return `${apiBase()}/internal/v1/query/events?${params.toString()}`;
}

export function identityUrl(sessionKey) {
  return `${apiBase()}/internal/v1/query/identity/${encodeURIComponent(sessionKey)}`;
}

/**
 * A subpath deployment whose nginx has not been updated fails in one of two
 * ways, and neither says what is wrong on its own. Both are the same root
 * cause: no proxy location matches the query path.
 */
function queryPathHint() {
  return (
    `check that nginx proxies ${apiBase()}/internal/v1/query/ ` +
    '(deploy/nginx-ui.conf.example has the worked example), or set ' +
    'window.EP_QUERY_BASE if the query API deliberately lives elsewhere'
  );
}

/** Header lookup that survives a plain object stub and a real Headers. */
function header(response, name) {
  return response.headers?.get?.(name) ?? '';
}

/**
 * The query API answers a rejected request with `{"error", "detail"}`, and for
 * an unparseable id filter `detail` names which filter was wrong. Reporting
 * only the status code would send the operator hunting for it by hand, which
 * is the same "no signal" problem the 400 was introduced to fix. Falls back to
 * the status line whenever the body is missing, empty or not our shape (an
 * nginx-generated 502 page, say).
 */
async function errorMessage(response) {
  const fallback = `${response.status} ${response.statusText}`;

  // A Basic challenge means nginx, not us. Two deployments produce it and the
  // response cannot tell them apart: the credentials on file were rejected
  // (rotated htpasswd, or a browser that dropped its cached credential), or
  // the query API sits outside the prefix the UI authenticated on — credentials
  // are cached per directory prefix (RFC 7617 §2.2), so the browser was never
  // challenged there and attached nothing. Name both; re-authenticating is the
  // cheap one to rule out first. Our own 401 is JSON with no WWW-Authenticate,
  // and still reports as "unauthorized" — a wrong internal_token is a different
  // problem with a different fix.
  if (response.status === 401 && /basic/i.test(header(response, 'WWW-Authenticate'))) {
    return (
      `${fallback} — nginx asked for Basic credentials: either yours were rejected ` +
      '(reload and re-authenticate), or the browser was never challenged on this ' +
      `path and sent none: ${queryPathHint()}`
    );
  }

  try {
    const body = await response.json();
    if (!body || typeof body.error !== 'string') return fallback;
    return body.detail ? `${body.error}: ${body.detail}` : body.error;
  } catch {
    return fallback;
  }
}

async function getJson(url) {
  const headers = { Accept: 'application/json' };
  const token = apiToken();
  if (token) headers.Authorization = `Bearer ${token}`;
  const response = await fetch(url, { headers });
  if (!response.ok) throw new Error(await errorMessage(response));

  // The 200 case. With the query path unproxied, `location <ui-base>/` catches
  // it instead and try_files serves index.html — a cheerful 200 carrying HTML.
  // Parsing that raises a bare SyntaxError about an unexpected '<', which
  // reads like a bug in the UI rather than a missing nginx block. Only fires
  // when the body genuinely will not parse, so a working deployment that omits
  // Content-Type is unaffected.
  //
  // Only a SyntaxError means "the bytes are not JSON". fetch resolves as soon
  // as the headers land, so json() also rejects when the connection drops
  // mid-body or the navigation is aborted — blaming nginx for that would send
  // the operator editing locations over a network blip. Let those through.
  try {
    return await response.json();
  } catch (err) {
    if (!(err instanceof SyntaxError)) throw err;
    throw new Error(
      `the query API returned a non-JSON body — probably the SPA's own index.html: ${queryPathHint()}`,
    );
  }
}

export function fetchEvents(filters, options) {
  return getJson(eventsUrl(filters, options));
}

export function fetchIdentity(sessionKey) {
  return getJson(identityUrl(sessionKey));
}

/** Tailwind classes per delivery status chip. */
export function statusClass(status) {
  switch (status) {
    case 'delivered':
      return 'bg-green-100 text-green-800';
    case 'pending':
      return 'bg-amber-100 text-amber-800';
    case 'failed':
      return 'bg-orange-100 text-orange-800';
    case 'dead':
      return 'bg-red-100 text-red-800';
    case 'skipped':
      return 'bg-gray-200 text-gray-600';
    default:
      return 'bg-gray-100 text-gray-500';
  }
}

export function shortId(id) {
  return id ? `${id.slice(0, 8)}…` : '';
}

export function formatTime(iso) {
  if (!iso) return '';
  const date = new Date(iso);
  return `${date.toLocaleDateString()} ${date.toLocaleTimeString()}`;
}
