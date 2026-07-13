import 'dart:convert';
import 'dart:io';

/// One queued event plus its first-attempt time (for the 24h give-up rule,
/// SPEC §7). The wrapper is local-only and never transmitted.
class QueuedEnvelope {
  QueuedEnvelope(this.event, this.firstAttemptAtMs);

  final Map<String, Object?> event;
  final int firstAttemptAtMs;

  Map<String, Object?> toJson() => {'e': event, 't': firstAttemptAtMs};

  static QueuedEnvelope? fromLine(String line) {
    try {
      final decoded = jsonDecode(line);
      if (decoded is! Map<String, dynamic>) return null;
      final event = decoded['e'];
      final time = decoded['t'];
      if (event is! Map<String, dynamic> || time is! int) return null;
      return QueuedEnvelope(Map<String, Object?>.from(event), time);
    } on FormatException {
      return null;
    }
  }
}

/// JSONL file queue in the app-support dir (SPEC §7): persists across
/// restarts, cap 500 oldest-first drop, atomic truncate-on-success via
/// temp-file + rename.
class EventFileQueue {
  EventFileQueue(this._file, {int cap = 500}) : _cap = cap;

  final File _file;
  final int _cap;
  List<QueuedEnvelope> _items = [];
  bool _loaded = false;

  int get length {
    _ensureLoaded();
    return _items.length;
  }

  void append(Map<String, Object?> event, int nowMs) {
    _ensureLoaded();
    _items.add(QueuedEnvelope(event, nowMs));
    if (_items.length > _cap) {
      _items = _items.sublist(_items.length - _cap);
      _rewrite();
    } else {
      _file.writeAsStringSync(
        '${jsonEncode(_items.last.toJson())}\n',
        mode: FileMode.append,
        flush: true,
      );
    }
  }

  List<QueuedEnvelope> peek(int max) {
    _ensureLoaded();
    return _items.take(max).toList();
  }

  /// Removes acknowledged envelopes; atomic truncate via temp + rename.
  void ack(List<QueuedEnvelope> sent) {
    _ensureLoaded();
    final removed = Set<QueuedEnvelope>.from(sent);
    _items = _items.where((item) => !removed.contains(item)).toList();
    _rewrite();
  }

  void _ensureLoaded() {
    if (_loaded) return;
    _loaded = true;
    if (!_file.existsSync()) return;
    _items = _file
        .readAsLinesSync()
        .map(QueuedEnvelope.fromLine)
        .whereType<QueuedEnvelope>()
        .toList();
  }

  void _rewrite() {
    final temp = File('${_file.path}.tmp');
    final content = _items.map((item) => jsonEncode(item.toJson())).join('\n');
    temp.writeAsStringSync(content.isEmpty ? '' : '$content\n', flush: true);
    temp.renameSync(_file.path);
  }
}
