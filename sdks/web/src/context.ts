/**
 * SPEC §5: full context collected once per session at S2, sent in /v1/identity.
 * Chromium-only fields degrade to undefined; everything is SSR-safe; async
 * collectors patch later via a partial /v1/identity upsert.
 */

function prune(context: Record<string, unknown>): Record<string, unknown> {
  const pruned: Record<string, unknown> = {};
  for (const [key, value] of Object.entries(context)) {
    if (value !== undefined && value !== null && value !== '') pruned[key] = value;
  }
  return pruned;
}

export function collectContext(config: { appVersion?: string; build?: string }): Record<string, unknown> {
  if (typeof window === 'undefined') return {};
  const context: Record<string, unknown> = {};
  try {
    const nav = navigator;
    context.language = nav.language;
    context.languages = nav.languages ? [...nav.languages].slice(0, 5) : undefined;
    context.user_agent = nav.userAgent;
    context.touch = 'ontouchstart' in window || nav.maxTouchPoints > 0;

    const uaData = (nav as { userAgentData?: { platform?: string; mobile?: boolean } }).userAgentData;
    if (uaData) {
      context.os = uaData.platform;
      context.category = uaData.mobile ? 'mobile' : 'desktop';
    }

    const connection = (nav as {
      connection?: { effectiveType?: string; saveData?: boolean };
    }).connection;
    if (connection) {
      context.connection_type = connection.effectiveType;
      context.save_data = connection.saveData === true;
    }

    context.screen_resolution = `${screen.width}x${screen.height}`;
    context.viewport = `${window.innerWidth}x${window.innerHeight}`;
    context.dpr = window.devicePixelRatio;
    try {
      context.orientation = screen.orientation?.type;
    } catch {
      /* unsupported */
    }
    try {
      context.timezone = Intl.DateTimeFormat().resolvedOptions().timeZone;
    } catch {
      /* unsupported */
    }
    try {
      context.color_scheme = window.matchMedia?.('(prefers-color-scheme: dark)').matches
        ? 'dark'
        : 'light';
    } catch {
      /* unsupported */
    }

    context.referrer = document.referrer || undefined;
    context.initial_url = location.href;
    const params = new URLSearchParams(location.search);
    for (const key of ['utm_source', 'utm_medium', 'utm_campaign', 'utm_term', 'utm_content']) {
      const value = params.get(key);
      if (value) context[key] = value;
    }
  } catch {
    /* context must never break tracking */
  }
  if (config.appVersion) context.app_version = config.appVersion;
  if (config.build) context.build = config.build;
  return prune(context);
}

/** High-entropy UA hints (Chromium only) — resolves late, patched via partial identity. */
export async function collectLateContext(): Promise<Record<string, unknown> | null> {
  const uaData = (globalThis.navigator as unknown as {
    userAgentData?: {
      platform?: string;
      getHighEntropyValues?: (hints: string[]) => Promise<Record<string, string>>;
    };
  })?.userAgentData;
  if (!uaData?.getHighEntropyValues) return null;
  try {
    const high = await uaData.getHighEntropyValues(['platformVersion', 'model']);
    return prune({
      os: uaData.platform,
      os_version: high.platformVersion,
      model: high.model || undefined,
    });
  } catch {
    return null;
  }
}
