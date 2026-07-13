/** SSR-safe, quota-safe storage access. Failures degrade to null, never throw. */

export function local(): Storage | null {
  try {
    return typeof window === 'undefined' ? null : window.localStorage;
  } catch {
    return null;
  }
}

export function session(): Storage | null {
  try {
    return typeof window === 'undefined' ? null : window.sessionStorage;
  } catch {
    return null;
  }
}

export function readJson<T>(storage: Storage | null, key: string): T | null {
  try {
    const raw = storage?.getItem(key);
    return raw ? (JSON.parse(raw) as T) : null;
  } catch {
    return null;
  }
}

export function writeJson(storage: Storage | null, key: string, value: unknown): boolean {
  try {
    storage?.setItem(key, JSON.stringify(value));
    return storage !== null;
  } catch {
    return false;
  }
}
