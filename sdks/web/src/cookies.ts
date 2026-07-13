/**
 * Cookie READING only. This SDK never writes document.cookie (SPEC §2):
 * Safari ITP caps script-written storage at 7 days; the server sets ep_aid.
 */

export function readCookie(name: string): string | null {
  if (typeof document === 'undefined') return null;
  const prefix = `${name}=`;
  for (const part of document.cookie.split(';')) {
    const trimmed = part.trim();
    if (trimmed.startsWith(prefix)) return decodeURIComponent(trimmed.slice(prefix.length));
  }
  return null;
}

/** `_ga=GA1.1.A.B` -> `A.B` (the GA4 client id). */
export function parseGaClientId(): string | null {
  const ga = readCookie('_ga');
  if (!ga) return null;
  const parts = ga.split('.');
  return parts.length >= 4 ? parts.slice(2).join('.') : null;
}

/** Session id from the first `_ga_<container>` cookie (GS1 and GS2 formats). */
export function parseGaSessionId(): string | null {
  if (typeof document === 'undefined') return null;
  for (const part of document.cookie.split(';')) {
    const eq = part.indexOf('=');
    if (eq < 0) continue;
    const name = part.slice(0, eq).trim();
    if (!name.startsWith('_ga_')) continue;
    const segments = part.slice(eq + 1).split('.');
    const third = segments[2];
    if (!third) return null;
    const match = third.startsWith('s') ? third.match(/^s(\d+)/) : third.match(/^(\d+)/);
    return match ? match[1]! : null;
  }
  return null;
}

export function readFbp(): string | null {
  return readCookie('_fbp');
}

/** Meta's documented `_fbc` construction from a landing fbclid. */
export function buildFbc(fbclid: string, nowMs: number): string {
  return `fb.1.${nowMs}.${fbclid}`;
}
