import { describe, expect, it } from 'vitest';
import { makeUrlRewrite, normalizeBase, resolveBase, withBase } from '../src/lib/base.js';

describe('resolveBase', () => {
  it('falls back to the build-time base when no runtime override is set', () => {
    expect(resolveBase(undefined, '/ep/ui/')).toBe('/ep/ui');
    expect(resolveBase(undefined, '/')).toBe('');
  });

  // This is the case that makes the RPM usable: bundle built for '/', mounted
  // elsewhere by the deployment.
  it('lets a runtime override relocate a root build', () => {
    expect(resolveBase('/ep/ui', '/')).toBe('/ep/ui');
    expect(resolveBase('/ep/ui/', '/')).toBe('/ep/ui');
  });

  it('lets a runtime override pin a subpath build back to the root', () => {
    expect(resolveBase('/', '/ep/ui/')).toBe('');
  });
});

describe('normalizeBase', () => {
  it('reduces a root build to the empty string', () => {
    expect(normalizeBase('/')).toBe('');
    expect(normalizeBase('')).toBe('');
    expect(normalizeBase(undefined)).toBe('');
  });

  it('drops the trailing slash Vite always appends', () => {
    expect(normalizeBase('/ep/ui/')).toBe('/ep/ui');
    expect(normalizeBase('/ep/ui')).toBe('/ep/ui');
  });
});

describe('withBase', () => {
  it('is identity at the root', () => {
    expect(withBase('/session/abc', '')).toBe('/session/abc');
  });

  it('prefixes under a subpath', () => {
    expect(withBase('/session/abc', '/ep/ui')).toBe('/ep/ui/session/abc');
    expect(withBase('/', '/ep/ui')).toBe('/ep/ui/');
  });
});

describe('makeUrlRewrite', () => {
  it('returns no rewrites at the root, leaving Routify untouched', () => {
    expect(makeUrlRewrite('')).toEqual([]);
  });

  const [rewrite] = makeUrlRewrite('/ep/ui');

  it('prefixes internal urls on the way to the browser', () => {
    expect(rewrite.toExternal('/')).toBe('/ep/ui/');
    expect(rewrite.toExternal('/session/abc')).toBe('/ep/ui/session/abc');
  });

  it('strips the base on the way back in', () => {
    expect(rewrite.toInternal('/ep/ui/session/abc')).toBe('/session/abc');
    expect(rewrite.toInternal('/ep/ui/')).toBe('/');
  });

  // The mount point with no trailing slash is what a user typing the URL by
  // hand produces; it has to resolve to the index, not to ''.
  it('maps the bare mount point to the index', () => {
    expect(rewrite.toInternal('/ep/ui')).toBe('/');
  });

  it('round-trips', () => {
    for (const url of ['/', '/session/abc']) {
      expect(rewrite.toInternal(rewrite.toExternal(url))).toBe(url);
    }
  });

  // Routify re-runs toInternal on urls that are already internal; that must not
  // corrupt them, and a path outside the mount point is left for the 404 route.
  it('leaves already-internal and out-of-scope urls alone', () => {
    expect(rewrite.toInternal('/session/abc')).toBe('/session/abc');
    expect(rewrite.toInternal('/elsewhere')).toBe('/elsewhere');
  });

  // '/ep/uixyz' shares a string prefix with '/ep/ui' but is a different mount.
  it('does not match a partial path segment', () => {
    expect(rewrite.toInternal('/ep/uixyz')).toBe('/ep/uixyz');
  });
});
