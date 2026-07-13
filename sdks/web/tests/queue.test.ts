import { beforeEach, describe, expect, it } from 'vitest';
import { EventQueue } from '../src/queue';

beforeEach(() => localStorage.clear());

function ev(id: string): Record<string, unknown> {
  return { event_id: id, event_name: 'x', occurred_at: 'now', anonymous_id: 'a' };
}

describe('EventQueue (SPEC §7)', () => {
  it('persists across restarts', () => {
    const queue = new EventQueue();
    queue.push(ev('1'), 1_000);
    queue.push(ev('2'), 2_000);

    const reborn = new EventQueue();
    reborn.restore();
    expect(reborn.size()).toBe(2);
    expect(reborn.peek(10).map((e) => e.event.event_id)).toEqual(['1', '2']);
    expect(reborn.peek(10)[0]!.firstAttemptAt).toBe(1_000);
  });

  it('caps at 200 dropping oldest first', () => {
    const queue = new EventQueue();
    for (let i = 0; i < 205; i++) queue.push(ev(String(i)), i);
    expect(queue.size()).toBe(200);
    expect(queue.peek(1)[0]!.event.event_id).toBe('5');
  });

  it('ack removes sent envelopes and persists the removal', () => {
    const queue = new EventQueue();
    queue.push(ev('1'), 1_000);
    queue.push(ev('2'), 1_000);
    queue.ack(queue.peek(1));
    expect(queue.size()).toBe(1);

    const reborn = new EventQueue();
    reborn.restore();
    expect(reborn.peek(10).map((e) => e.event.event_id)).toEqual(['2']);
  });
});
