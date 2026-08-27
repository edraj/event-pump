// The active tenant, and what the server says about it.
//
// One store rather than per-route state: both routes need the same answer, and
// switching tenants has to re-scope every query at once. `info` is refetched
// whenever `active` changes — its `app_id` is the authoritative name, since the
// mount only says which token nginx injects, not who that token turned out to
// be.

import { writable } from 'svelte/store';
import { fetchTenantInfo } from './api.js';
import {
  pickTenant,
  readTenants,
  rememberTenant,
  rememberedTenant,
  tenantFromUrl,
} from './tenants.js';

export const tenants = readTenants();

export const active = writable(
  pickTenant(tenants, { fromUrl: tenantFromUrl(), remembered: rememberedTenant() }),
);

/** `{ status: 'loading' | 'ready' | 'error', data, error }` for the active tenant. */
export const info = writable({ status: 'loading', data: null, error: '' });

let inFlight = 0;

active.subscribe((tenant) => {
  if (!tenant) return;
  const request = ++inFlight;
  info.set({ status: 'loading', data: null, error: '' });
  fetchTenantInfo(tenant)
    .then((data) => {
      // A slow answer for a tenant the user already switched away from must not
      // overwrite the current one.
      if (request === inFlight) info.set({ status: 'ready', data, error: '' });
    })
    .catch((problem) => {
      if (request === inFlight) {
        info.set({ status: 'error', data: null, error: String(problem.message ?? problem) });
      }
    });
});

/**
 * Switches tenant and makes the choice survive both a reload (localStorage) and
 * a copied link (`?tenant=`). replaceState rather than push: the tenant is which
 * dataset you are looking at, not a place you navigated to, so Back should leave
 * the page rather than silently re-scope it.
 */
export function selectTenant(tenant) {
  if (!tenant) return;
  active.set(tenant);
  rememberTenant(tenant.app_id);
  if (typeof history === 'undefined' || typeof location === 'undefined') return;
  const url = new URL(location.href);
  if (tenant.app_id) url.searchParams.set('tenant', tenant.app_id);
  else url.searchParams.delete('tenant');
  history.replaceState(history.state, '', url);
}
