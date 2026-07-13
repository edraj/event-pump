import { beforeEach, describe, expect, it } from 'vitest';
import { buildFbc, parseGaClientId, parseGaSessionId, readCookie, readFbp } from '../src/cookies';

function clearCookies(): void {
  for (const pair of document.cookie.split(';')) {
    const name = pair.split('=')[0]?.trim();
    if (name) document.cookie = `${name}=; expires=Thu, 01 Jan 1970 00:00:00 GMT`;
  }
}

beforeEach(clearCookies);

describe('readCookie', () => {
  it('reads a cookie by name', () => {
    document.cookie = 'ep_aid=0f2937de-92f9-4b6c-a222-abcdefabcdef';
    expect(readCookie('ep_aid')).toBe('0f2937de-92f9-4b6c-a222-abcdefabcdef');
  });

  it('returns null when absent', () => {
    expect(readCookie('ep_aid')).toBeNull();
  });
});

describe('GA cookie parsing (SPEC §6)', () => {
  it('extracts ga4_client_id from _ga', () => {
    document.cookie = '_ga=GA1.1.123456789.1700000000';
    expect(parseGaClientId()).toBe('123456789.1700000000');
  });

  it('extracts session id from a _ga_<container> cookie (GS1 format)', () => {
    document.cookie = '_ga_ABC123=GS1.1.1699999999.13.1.1700000042.0.0.0';
    expect(parseGaSessionId()).toBe('1699999999');
  });

  it('extracts session id from the GS2 format', () => {
    document.cookie = '_ga_ABC123=GS2.1.s1747000000$o5$g1$t1747000100$j0$l0$h0';
    expect(parseGaSessionId()).toBe('1747000000');
  });

  it('returns null when cookies absent', () => {
    expect(parseGaClientId()).toBeNull();
    expect(parseGaSessionId()).toBeNull();
  });
});

describe('Meta identifiers (SPEC §6)', () => {
  it('reads _fbp', () => {
    document.cookie = '_fbp=fb.1.1700000000000.1234567890';
    expect(readFbp()).toBe('fb.1.1700000000000.1234567890');
  });

  it('builds _fbc from a landing fbclid', () => {
    expect(buildFbc('AbCdEf123', 1_700_000_000_000)).toBe('fb.1.1700000000000.AbCdEf123');
  });
});
