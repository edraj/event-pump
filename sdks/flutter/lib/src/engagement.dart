/// Foreground stopwatch (SPEC §4): each event takes the milliseconds
/// accumulated since the previous event; paused while backgrounded.
class EngagementStopwatch {
  int _accumulated = 0;
  int? _runningSince;

  void start(int nowMs) => _runningSince ??= nowMs;

  void pause(int nowMs) {
    final since = _runningSince;
    if (since != null) {
      _accumulated += nowMs - since;
      _runningSince = null;
    }
  }

  /// Returns accumulated foreground ms and resets; keeps running if running.
  int take(int nowMs) {
    var total = _accumulated;
    final since = _runningSince;
    if (since != null) {
      total += nowMs - since;
      _runningSince = nowMs;
    }
    _accumulated = 0;
    return total < 0 ? 0 : total;
  }
}
