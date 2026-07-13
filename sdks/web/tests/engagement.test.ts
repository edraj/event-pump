import { describe, expect, it } from 'vitest';
import { EngagementStopwatch } from '../src/engagement';

describe('EngagementStopwatch (SPEC §4)', () => {
  it('accumulates only while running and resets on take', () => {
    const sw = new EngagementStopwatch();
    sw.start(1_000);
    expect(sw.take(3_500)).toBe(2_500); // accumulated since start, reset on take
    expect(sw.take(4_000)).toBe(500); // still running: since previous take
  });

  it('pauses while hidden', () => {
    const sw = new EngagementStopwatch();
    sw.start(0);
    sw.pause(2_000); // 2s visible
    expect(sw.take(9_000)).toBe(2_000); // hidden time not counted
    sw.start(10_000);
    expect(sw.take(10_400)).toBe(400);
  });

  it('is zero before started', () => {
    const sw = new EngagementStopwatch();
    expect(sw.take(5_000)).toBe(0);
  });
});
