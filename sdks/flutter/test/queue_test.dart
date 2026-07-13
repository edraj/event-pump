import 'dart:io';

import 'package:event_pump/event_pump.dart';
import 'package:flutter_test/flutter_test.dart';

void main() {
  late Directory dir;
  late File file;

  setUp(() {
    dir = Directory.systemTemp.createTempSync('ep_queue_test_');
    file = File('${dir.path}/queue.jsonl');
  });

  tearDown(() => dir.deleteSync(recursive: true));

  Map<String, Object?> ev(String id) => {'event_id': id, 'event_name': 'x'};

  group('EventFileQueue (SPEC §7)', () {
    test('persists JSONL across restarts', () {
      final queue = EventFileQueue(file);
      queue.append(ev('1'), 1000);
      queue.append(ev('2'), 2000);

      expect(file.readAsLinesSync(), hasLength(2));

      final reborn = EventFileQueue(file);
      expect(reborn.length, 2);
      expect(reborn.peek(10).map((e) => e.event['event_id']), ['1', '2']);
      expect(reborn.peek(10).first.firstAttemptAtMs, 1000);
    });

    test('ack rewrites atomically via temp + rename', () {
      final queue = EventFileQueue(file);
      queue.append(ev('1'), 1000);
      queue.append(ev('2'), 1000);
      queue.append(ev('3'), 1000);

      queue.ack(queue.peek(2));

      expect(File('${file.path}.tmp').existsSync(), isFalse);
      expect(file.readAsLinesSync(), hasLength(1));
      final reborn = EventFileQueue(file);
      expect(reborn.peek(10).map((e) => e.event['event_id']), ['3']);
    });

    test('caps at 500, dropping oldest first', () {
      final queue = EventFileQueue(file);
      for (var i = 0; i < 505; i++) {
        queue.append(ev('$i'), i);
      }
      expect(queue.length, 500);
      expect(queue.peek(1).first.event['event_id'], '5');
      expect(EventFileQueue(file).length, 500);
    });

    test('tolerates corrupt lines', () {
      file.writeAsStringSync('{"e":{"event_id":"ok"},"t":5}\nnot json\n{"bad":1}\n');
      final queue = EventFileQueue(file);
      expect(queue.length, 1);
      expect(queue.peek(1).first.event['event_id'], 'ok');
    });
  });
}
