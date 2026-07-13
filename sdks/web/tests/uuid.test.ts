import { describe, expect, it } from 'vitest';
import { uuidv4, uuidv7 } from '../src/uuid';

const UUID_RE = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;

describe('uuidv4', () => {
  it('produces RFC-4122 v4 ids', () => {
    for (let i = 0; i < 50; i++) {
      const id = uuidv4();
      expect(id).toMatch(UUID_RE);
      expect(id[14]).toBe('4');
      expect('89ab').toContain(id[19]!);
    }
  });

  it('does not repeat', () => {
    const ids = new Set(Array.from({ length: 200 }, () => uuidv4()));
    expect(ids.size).toBe(200);
  });
});

describe('uuidv7', () => {
  it('produces v7 ids whose timestamp prefix orders by time', () => {
    const early = uuidv7(1_700_000_000_000);
    const late = uuidv7(1_700_000_100_000);
    expect(early).toMatch(UUID_RE);
    expect(early[14]).toBe('7');
    expect('89ab').toContain(early[19]!);
    expect(early < late).toBe(true);
  });

  it('embeds the millisecond timestamp in the first 48 bits', () => {
    const ms = 1_700_000_000_000;
    const id = uuidv7(ms);
    const hex = id.replaceAll('-', '').slice(0, 12);
    expect(parseInt(hex, 16)).toBe(ms);
  });
});
