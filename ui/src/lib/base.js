// Base-path support, so the bundle can be served somewhere other than a vhost
// root (SPEC §9.5 deployments often carve the API and UI out of one domain).
//
// Vite sets import.meta.env.BASE_URL from `base` in vite.config.js, which reads
// EP_UI_BASE at build time. It is always slash-terminated ('/' or '/ep/ui/').
// Everything here works from a normalized form with no trailing slash, so '' is
// the root deployment and '/ep/ui' is a subpath.

/** Strips the trailing slash Vite guarantees; '' for a root deployment. */
export function normalizeBase(base) {
  return (base || '/').replace(/\/+$/, '');
}

/**
 * Runtime override wins over the build-time base, so the prebuilt bundle the
 * RPM ships (always built for '/') can still be mounted somewhere else without
 * rebuilding — set `window.EP_UI_BASE = '/ep/ui'` before the app script, the
 * same way `window.EP_QUERY_BASE` redirects the query calls. Asset URLs are
 * baked into index.html at build time, so a runtime move also needs the server
 * to rewrite those (nginx sub_filter); the router half is this.
 */
export function resolveBase(runtime, buildTime) {
  return normalizeBase(runtime ?? buildTime ?? '/');
}

export const basePath = resolveBase(
  typeof window !== 'undefined' ? window.EP_UI_BASE : undefined,
  typeof import.meta !== 'undefined' ? import.meta.env?.BASE_URL : '/',
);

/** Prefixes an app-absolute path for use in an href. */
export function withBase(path, base = basePath) {
  return `${base}${path}`;
}

/**
 * Routify keeps its own root-relative URLs ('/', '/session/x') and reflects
 * them to the browser through these hooks. Without them the router matches the
 * raw pathname — '/ep/ui/session/x' hits no route and renders the 404.
 * Returns [] at the root so the router keeps its default behaviour exactly.
 */
export function makeUrlRewrite(base = basePath) {
  if (!base) return [];
  return [
    {
      toExternal: (url) => `${base}${url}`,
      toInternal: (url) => {
        if (url === base) return '/';
        if (url.startsWith(`${base}/`)) return url.slice(base.length);
        // Already internal (Routify re-runs this on its own URLs), or a path
        // outside the mount point — leave it alone rather than mangling it.
        return url;
      },
    },
  ];
}

export const urlRewrite = makeUrlRewrite();
