/**
 * Foreground/visible stopwatch (SPEC §4). Each event takes the milliseconds
 * accumulated since the previous event; paused while the document is hidden.
 */
export class EngagementStopwatch {
  private accumulated = 0;
  private runningSince: number | null = null;

  start(now: number): void {
    if (this.runningSince === null) this.runningSince = now;
  }

  pause(now: number): void {
    if (this.runningSince !== null) {
      this.accumulated += now - this.runningSince;
      this.runningSince = null;
    }
  }

  /** Returns accumulated visible ms and resets; keeps running if running. */
  take(now: number): number {
    let total = this.accumulated;
    if (this.runningSince !== null) {
      total += now - this.runningSince;
      this.runningSince = now;
    }
    this.accumulated = 0;
    return Math.max(0, Math.round(total));
  }
}
