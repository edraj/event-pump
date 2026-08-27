// Query client for /internal/v1/query (read-only; internal listener).
//
// Every call is scoped to ONE tenant, because the API scopes it that way: the
// bearer token names the tenant and the query returns that tenant's rows and no
// other's. The UI never holds the token — it picks a tenant by picking which
// proxy path to call, and nginx injects that tenant's internal_token there
// (see lib/tenants.js and deploy/nginx-ui.conf.example).

/**
 * Escape hatch for a dev box talking straight to EP_INTERNAL_LISTEN with no
 * proxy in front: `window.EP_QUERY_TOKEN = '<internal_token>'`. It applies to
 * whichever tenant is selected, so it only makes sense with a single mount.
 * Never set it on a page anyone else can load — it is a server secret.
 */
export function apiToken() {
  return (typeof window !== 'undefined' && window.EP_QUERY_TOKEN) || '';
}

const QUERY_ROOT = '/internal/v1/query';

/** Filters the events query understands; anything else is ignored. */
export const EVENT_FILTERS = [
  'event_name',
  'origin',
  'user_id',
  'anonymous_id',
  'session_key',
  'destination',
  'status',
  'from',
  'to',
];

/** Delivery states a row can be in (SPEC §12), in lifecycle order. */
export const DELIVERY_STATUSES = ['pending', 'delivered', 'failed', 'dead', 'skipped'];

export function tenantUrl(tenant) {
  return `${tenant?.base ?? ''}${QUERY_ROOT}/tenant`;
}

/**
 * `<input type="datetime-local">` yields a naive `YYYY-MM-DDTHH:mm`, which the
 * server parses with DateTimeStyles.RoundtripKind — i.e. as *server*-local,
 * UTC in production — and then clamps against a UTC floor. Sent as-is, a
 * browser at UTC+3 asks for a window three hours off the one the operator
 * picked, and silently loses that much of it against the clamp with nothing on
 * screen to say so. Stamp the browser's own offset on before sending; a value
 * that already carries a zone (a hand-built link, a test) is passed through.
 */
function toInstant(value) {
  if (/(Z|[+-]\d{2}:?\d{2})$/.test(value)) return value;
  const parsed = new Date(value);
  return Number.isNaN(parsed.getTime()) ? value : parsed.toISOString();
}

const INSTANT_FILTERS = new Set(['from', 'to']);

/** Builds the events query URL; empty/blank filters are omitted. */
export function eventsUrl(tenant, filters = {}, { cursor = null, limit = 50 } = {}) {
  const params = new URLSearchParams();
  for (const key of EVENT_FILTERS) {
    const value = (filters[key] ?? '').toString().trim();
    if (value) params.set(key, INSTANT_FILTERS.has(key) ? toInstant(value) : value);
  }
  params.set('limit', String(limit));
  if (cursor) params.set('cursor', cursor);
  return `${tenant?.base ?? ''}${QUERY_ROOT}/events?${params.toString()}`;
}

export function identityUrl(tenant, sessionKey) {
  return `${tenant?.base ?? ''}${QUERY_ROOT}/identity/${encodeURIComponent(sessionKey)}`;
}

/**
 * A tenant mount whose nginx has not been set up fails in several ways and none
 * of them says so on its own. They share one root cause: no proxy location
 * matches this tenant's query path.
 */
function mountHint(url) {
  const cut = url.indexOf(QUERY_ROOT);
  const base = cut > 0 ? url.slice(0, cut) : '';
  return (
    `check that nginx proxies ${base}${QUERY_ROOT}/ for this tenant ` +
    '(deploy/nginx-ui.conf.example has the worked example), or correct the ' +
    "tenant's `base` in window.EP_TENANTS if the mount lives elsewhere"
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
 *
 * `kind` is which endpoint was asked for, because the same status means
 * different things on different ones — see the 404 below.
 */
async function errorMessage(response, url, kind) {
  const fallback = `${response.status} ${response.statusText}`;

  // A Basic challenge means nginx, not us. Two deployments produce it and the
  // response cannot tell them apart: the credentials on file were rejected
  // (rotated htpasswd, or a browser that dropped its cached credential), or
  // this tenant's mount sits outside the prefix the UI authenticated on —
  // credentials are cached per directory prefix (RFC 7617 §2.2), so the browser
  // was never challenged there and attached nothing. Name both;
  // re-authenticating is the cheap one to rule out first.
  if (response.status === 401 && /basic/i.test(header(response, 'WWW-Authenticate'))) {
    return (
      `${fallback} — nginx asked for Basic credentials: either yours were rejected ` +
      '(reload and re-authenticate), or the browser was never challenged on this ' +
      `path and sent none: ${mountHint(url)}`
    );
  }

  // Our own 401 — JSON, no WWW-Authenticate. The mount reached the API but the
  // bearer it injected matched no tenant, so say that rather than "unauthorized":
  // the fix is one nginx line, and the reader should not have to go find it.
  if (response.status === 401) {
    return `${fallback} — this tenant mount is not sending a valid internal_token `
      + '(check the proxy_set_header Authorization line for it)';
  }

  // A 404 only means "no such mount" for endpoints that exist for every tenant.
  // /query/identity 404s with an empty body whenever the session key is simply
  // not in identity_registry for this app_id — a server-origin event, an expired
  // or mistyped key, one pasted from another tenant — and blaming nginx there
  // sends the operator to fix a mount that is working.
  if (response.status === 404) {
    return kind === 'identity'
      ? `${fallback} — no identity row for this session key on this tenant `
        + '(it may belong to another tenant, or the session may have sent no client-side event)'
      : `${fallback} — no such tenant mount on this server: ${mountHint(url)}`;
  }

  try {
    const body = await response.json();
    if (!body || typeof body.error !== 'string') return fallback;
    return body.detail ? `${body.error}: ${body.detail}` : body.error;
  } catch {
    return fallback;
  }
}

async function getJson(url, kind) {
  const headers = { Accept: 'application/json' };
  const token = apiToken();
  if (token) headers.Authorization = `Bearer ${token}`;
  const response = await fetch(url, { headers });
  if (!response.ok) throw new Error(await errorMessage(response, url, kind));

  // The 200 case. With this tenant's query path unproxied, `location <ui-base>/`
  // catches it instead and try_files serves index.html — a cheerful 200 carrying
  // HTML. Parsing that raises a bare SyntaxError about an unexpected '<', which
  // reads like a bug in the UI rather than a missing nginx block.
  //
  // Only a SyntaxError means "the bytes are not JSON". fetch resolves as soon as
  // the headers land, so json() also rejects when the connection drops mid-body
  // or the navigation is aborted — blaming nginx for that would send the
  // operator editing locations over a network blip. Let those through.
  try {
    return await response.json();
  } catch (err) {
    if (!(err instanceof SyntaxError)) throw err;
    throw new Error(
      `the query API returned a non-JSON body — probably the SPA's own index.html: ${mountHint(url)}`,
    );
  }
}

/** Who is this mount? Names the tenant and describes its plan and destinations. */
export function fetchTenantInfo(tenant) {
  return getJson(tenantUrl(tenant), 'tenant');
}

export function fetchEvents(tenant, filters, options) {
  return getJson(eventsUrl(tenant, filters, options), 'events');
}

export function fetchIdentity(tenant, sessionKey) {
  return getJson(identityUrl(tenant, sessionKey), 'identity');
}

/**
 * The identity row carries one set of handles per destination family. Showing
 * all of them regardless of what the tenant runs is noise — eight dashes and no
 * way to tell "not set yet" from "this tenant has no Adjust". Keyed by the
 * destination codes /query/tenant reports.
 */
export const DESTINATION_HANDLES = {
  ga4: ['ga4_client_id', 'ga4_session_id', 'firebase_app_instance_id'],
  amplitude: ['amplitude_device_id'],
  adjust: ['adjust_adid', 'adjust_platform_ad_id'],
  meta: ['fbp', 'fbc'],
  // moengage / moengage_customer key off user_id and the MoEngage customer id,
  // neither of which is a handle column on the identity row.
};

/** Handle fields worth showing for a tenant, in destination order. */
export function handlesFor(destinations = []) {
  return destinations
    .map((destination) => ({
      destination: destination.code,
      fields: DESTINATION_HANDLES[destination.code] ?? [],
    }))
    .filter((group) => group.fields.length > 0);
}

/**
 * Lower bound the date pickers accept. The API clamps `from` to
 * EP_QUERY_MAX_DAYS ago regardless, so letting the form offer a wider range
 * would just return a silently narrower answer.
 */
export function windowFloor(queryMaxDays, now = new Date()) {
  const floor = new Date(now.getTime() - queryMaxDays * 86400000);
  return toLocalInput(floor);
}

/** `<input type="datetime-local">` wants local time with no zone suffix. */
export function toLocalInput(date) {
  const pad = (n) => String(n).padStart(2, '0');
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}`
    + `T${pad(date.getHours())}:${pad(date.getMinutes())}`;
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
