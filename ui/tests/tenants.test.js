import { describe, expect, it } from 'vitest';
import {
  pickTenant,
  readTenants,
  rememberTenant,
  rememberedTenant,
  tenantFromUrl,
  withTenant,
} from '../src/lib/tenants.js';

const kefah = { app_id: 'kefahapp', base: '/t/kefahapp' };
const itimad = { app_id: 'itimadapp', base: '/t/itimadapp' };

describe('readTenants', () => {
  it('reads the declared mounts and strips trailing slashes', () => {
    const tenants = readTenants({
      EP_TENANTS: [
        { app_id: 'kefahapp', base: '/t/kefahapp/' },
        { app_id: 'itimadapp', base: '/t/itimadapp' },
      ],
    });
    expect(tenants).toEqual([kefah, itimad]);
  });

  // A deployment that predates the switcher declares no EP_TENANTS: one vhost,
  // one injected token. It must keep working, with the server supplying the
  // name the page does not know.
  it('falls back to one implicit mount at EP_QUERY_BASE', () => {
    expect(readTenants({})).toEqual([{ app_id: null, base: '' }]);
    expect(readTenants({ EP_QUERY_BASE: '/ep/' })).toEqual([{ app_id: null, base: '/ep' }]);
    expect(readTenants(undefined)).toEqual([{ app_id: null, base: '' }]);
  });

  it('ignores junk entries rather than rendering a broken switcher', () => {
    expect(readTenants({ EP_TENANTS: [kefah, null, 'nope'] })).toEqual([kefah]);
  });
});

describe('pickTenant', () => {
  const tenants = [kefah, itimad];

  // A shared session link has to open against the tenant it was copied from —
  // the same session_key under another tenant is a 404, not another session.
  it('prefers the URL over the remembered selection', () => {
    expect(pickTenant(tenants, { fromUrl: 'itimadapp', remembered: 'kefahapp' })).toBe(itimad);
  });

  it('falls back to the remembered selection, then the first mount', () => {
    expect(pickTenant(tenants, { remembered: 'itimadapp' })).toBe(itimad);
    expect(pickTenant(tenants, {})).toBe(kefah);
  });

  it('falls through an id that no longer exists instead of rendering nothing', () => {
    expect(pickTenant(tenants, { fromUrl: 'retired', remembered: 'gone' })).toBe(kefah);
  });
});

describe('withTenant', () => {
  it('carries the tenant on app links', () => {
    expect(withTenant('/session/abc', kefah)).toBe('/session/abc?tenant=kefahapp');
    expect(withTenant('/ep/ui/?x=1', kefah)).toBe('/ep/ui/?x=1&tenant=kefahapp');
  });

  it('adds nothing for the implicit single mount, where it would name nothing', () => {
    expect(withTenant('/session/abc', { app_id: null, base: '' })).toBe('/session/abc');
  });
});

describe('tenantFromUrl', () => {
  it('reads ?tenant=', () => {
    expect(tenantFromUrl('?tenant=kefahapp&x=1')).toBe('kefahapp');
    expect(tenantFromUrl('')).toBeNull();
  });
});

describe('rememberTenant', () => {
  function store() {
    const values = new Map();
    return {
      getItem: (k) => values.get(k) ?? null,
      setItem: (k, v) => values.set(k, v),
      removeItem: (k) => values.delete(k),
    };
  }

  it('round-trips a selection', () => {
    const s = store();
    rememberTenant('itimadapp', s);
    expect(rememberedTenant(s)).toBe('itimadapp');
  });

  it('clears for the implicit mount, which has no id to remember', () => {
    const s = store();
    rememberTenant('itimadapp', s);
    rememberTenant(null, s);
    expect(rememberedTenant(s)).toBeNull();
  });

  // Storage throws outright in some privacy modes; a remembered selection is a
  // convenience and must never stop the page rendering.
  it('is a no-op with no storage available', () => {
    expect(() => rememberTenant('kefahapp', null)).not.toThrow();
    expect(rememberedTenant(null)).toBeNull();
  });
});
