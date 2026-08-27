import { readFileSync, readdirSync } from 'node:fs';
import { join } from 'node:path';
import { svelte } from '@sveltejs/vite-plugin-svelte';
import tailwindcss from '@tailwindcss/vite';
import routify from '@roxi/routify/vite-plugin';
import { defineConfig } from 'vite';

// dev: one proxy mount per tenant, mirroring deploy/nginx-ui.conf.example.
//
// The query API has no ?app_id= — the internal_token is what selects a tenant
// (SPEC v1.2 §9.3) — and that token is a server secret the bundle must not
// hold. So dev does what nginx does: give each tenant a path prefix and inject
// its token there. Point EP_TENANTS_DIR at the same directory the api reads and
// the switcher lights up with the real tenants.
const TENANT_PREFIX = '/t';

function devTenants(dir) {
  if (!dir) return [];
  try {
    return readdirSync(dir)
      .filter((file) => file.endsWith('.json') || file.endsWith('.jsonc'))
      .sort()
      .map((file) => parseTenant(join(dir, file)))
      .filter((tenant) => tenant?.app_id && tenant?.internal_token);
  } catch (problem) {
    console.warn(`[eventpump-ui] EP_TENANTS_DIR unreadable (${problem.message}) — single mount`);
    return [];
  }
}

/** Only app_id and internal_token are needed, so this stays a shallow read. */
function parseTenant(path) {
  try {
    const raw = readFileSync(path, 'utf8')
      .replace(/"(?:[^"\\]|\\.)*"|\/\*[\s\S]*?\*\/|\/\/[^\n]*/g, (match) =>
        (match.startsWith('"') ? match : ' '))
      .replace(/,(\s*[}\]])/g, '$1');
    const { app_id, internal_token } = JSON.parse(raw);
    return { app_id, internal_token };
  } catch (problem) {
    console.warn(`[eventpump-ui] skipping ${path}: ${problem.message}`);
    return null;
  }
}

export default defineConfig(() => {
  const api = process.env.EP_INTERNAL_URL || 'http://127.0.0.1:8081';
  const tenants = devTenants(process.env.EP_TENANTS_DIR);

  const proxy = {};
  for (const tenant of tenants) {
    proxy[`${TENANT_PREFIX}/${tenant.app_id}/internal`] = {
      target: api,
      rewrite: (path) => path.replace(`${TENANT_PREFIX}/${tenant.app_id}`, ''),
      headers: { Authorization: `Bearer ${tenant.internal_token}` },
    };
  }
  // No tenant dir: the single implicit mount, same as a pre-switcher install.
  // Pair it with window.EP_QUERY_TOKEN for a proxy-less dev box.
  if (tenants.length === 0) proxy['/internal'] = api;

  return {
    // Where the bundle will be mounted. '/' (a vhost root) unless EP_UI_BASE says
    // otherwise — set it to serve under a subpath, e.g. EP_UI_BASE=/ep/ui/ when
    // the API owns the domain root. Vite rewrites the asset URLs in index.html
    // and exposes the value as import.meta.env.BASE_URL, which src/lib/base.js
    // turns into the router's URL rewrite.
    base: process.env.EP_UI_BASE || '/',
    plugins: [routify({}), svelte(), tailwindcss(), devTenantList(tenants)],
    server: { proxy },
    test: {
      include: ['tests/**/*.test.js'],
    },
  };
});

/**
 * Declares the dev mounts to the page the same way nginx's sub_filter does in
 * production, so lib/tenants.js reads one shape in both. Dev only — a built
 * bundle is served by whoever configures the real mounts.
 */
function devTenantList(tenants) {
  return {
    name: 'eventpump-dev-tenants',
    apply: 'serve',
    transformIndexHtml() {
      if (tenants.length === 0) return;
      const list = tenants.map((t) => ({ app_id: t.app_id, base: `${TENANT_PREFIX}/${t.app_id}` }));
      return [{
        tag: 'script',
        injectTo: 'head-prepend',
        children: `window.EP_TENANTS=${JSON.stringify(list)};`,
      }];
    },
  };
}
