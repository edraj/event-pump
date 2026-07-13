import 'dart:async';
import 'dart:io';

import 'package:event_pump/event_pump.dart';

class MemoryStore implements KeyValueStore {
  final Map<String, String> values = {};

  @override
  String? getString(String key) => values[key];

  @override
  void setString(String key, String value) => values[key] = value;
}

class FakeTransport implements Transport {
  final List<(String path, Map<String, Object?> body)> calls = [];
  final List<Completer<bool>> pending = [];
  bool manual = false;
  bool respondOk = true;

  @override
  Future<bool> post(String path, Map<String, Object?> body) {
    calls.add((path, body));
    if (!manual) return Future.value(respondOk);
    final completer = Completer<bool>();
    pending.add(completer);
    return completer.future;
  }

  void completeNext([bool ok = true]) => pending.removeAt(0).complete(ok);

  List<Map<String, Object?>> identityBodies() =>
      calls.where((c) => c.$1 == '/v1/identity').map((c) => c.$2).toList();

  List<Map<String, Object?>> sentEvents() => calls
      .where((c) => c.$1 == '/v1/events')
      .expand((c) => (c.$2['events']! as List).cast<Map<String, Object?>>())
      .toList();
}

class Harness {
  Harness({DateTime Function()? now})
      : store = MemoryStore(),
        transport = FakeTransport(),
        directory = Directory.systemTemp.createTempSync('ep_test_') {
    _now = now;
  }

  final MemoryStore store;
  final FakeTransport transport;
  final Directory directory;
  DateTime Function()? _now;

  EventPumpClient build({DateTime Function()? now}) => EventPumpClient(
        config: const EventPumpConfig(
          endpoint: 'https://collect.test',
          appToken: 'tok-app',
        ),
        store: store,
        transport: transport,
        queue: EventFileQueue(File('${directory.path}/queue.jsonl')),
        now: now ?? _now,
      );

  void cleanup() => directory.deleteSync(recursive: true);
}
