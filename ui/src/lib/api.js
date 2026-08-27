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

/** Builds the events query URL; empty/blank filters are omitted. */
export function eventsUrl(tenant, filters = {}, { cursor = null, limit = 50 } = {}) {
  const params = new URLSearchParams();
  for (const key of EVENT_FILTERS) {
    const value = (filters[key] ?? '').toString().trim();
    if (value) params.set(key, value);
  }
  params.set('limit', String(limit));
  if (cursor) params.set('cursor', cursor);
  return `${tenant?.base ?? ''}${QUERY_ROOT}/events?${params.toString()}`;
}

export function identityUrl(tenant, sessionKey) {
  return `${tenant?.base ?? ''}${QUERY_ROOT}/identity/${encodeURIComponent(sessionKey)}`;
}

async function getJson(url) {
  const headers = { Accept: 'application/json' };
  const token = apiToken();
  if (token) headers.Authorization = `Bearer ${token}`;
  const response = await fetch(url, { headers });
  if (!response.ok) throw new Error(describe(response));
  return response.json();
}

/**
 * A bare "401 Unauthorized" on this UI almost always means one thing — the
 * mount is not injecting a token, or is injecting a stale one — so say that
 * instead of making the reader go and read nginx to find out.
 */
function describe(response) {
  if (response.status === 401) {
    return '401 — this tenant mount is not sending a valid internal_token '
      + '(check the proxy_set_header Authorization line for it)';
  }
  if (response.status === 404) return '404 — no such tenant mount on this server';
  return `${response.status} ${response.statusText}`;
}

/** Who is this mount? Names the tenant and describes its plan and destinations. */
export function fetchTenantInfo(tenant) {
  return getJson(tenantUrl(tenant));
}

export function fetchEvents(tenant, filters, options) {
  return getJson(eventsUrl(tenant, filters, options));
}

export function fetchIdentity(tenant, sessionKey) {
  return getJson(identityUrl(tenant, sessionKey));
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
