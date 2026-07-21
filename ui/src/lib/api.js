// Query client for /internal/v1/query (same-origin; nginx proxies + gates it).
// Override the base for unusual setups: window.EP_QUERY_BASE = 'https://...'.

export function apiBase() {
  return (typeof window !== 'undefined' && window.EP_QUERY_BASE) || '';
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

async function getJson(url) {
  const response = await fetch(url, { headers: { Accept: 'application/json' } });
  if (!response.ok) throw new Error(`${response.status} ${response.statusText}`);
  return response.json();
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
