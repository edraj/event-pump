import 'dart:ui' show AppLifecycleState;

import 'package:event_pump/event_pump.dart' show sdkName;
import 'package:fake_async/fake_async.dart';
import 'package:flutter_test/flutter_test.dart';

import 'fakes.dart';

void main() {
  late Harness harness;

  tearDown(() => harness.cleanup());

  group('S0–S4 ordering (SPEC §3)', () {
    test('identity registers before any events; buffered events flush after S3', () {
      fakeAsync((async) {
        final base = DateTime.utc(2026, 7, 13);
        harness = Harness(now: () => base.add(async.elapsed));
        harness.transport.manual = true;
        final client = harness.build();

        client.init();
        client.track('product_viewed', properties: {'sku': 'A1'});
        async.flushMicrotasks();

        expect(harness.transport.calls.map((c) => c.$1), ['/v1/identity']);
        expect(harness.transport.sentEvents(), isEmpty);

        harness.transport.completeNext(); // S3 completes -> S4 opens
        async.flushMicrotasks();
        harness.transport.manual = false;
        client.flush();
        async.flushMicrotasks();

        final events = harness.transport.sentEvents();
        expect(events, hasLength(1));
        expect(events.first['event_name'], 'product_viewed');
        expect(events.first['properties'], {'sku': 'A1'});
        final identity = harness.transport.identityBodies().first;
        expect(events.first['anonymous_id'], identity['anonymous_id']);
        expect(events.first['session_key'], identity['session_key']);
        expect(identity['session_number'], 1);
        expect((identity['handles']! as Map)['amplitude_device_id'], identity['anonymous_id']);
        client.dispose();
      });
    });
  });

  group('session persistence across restarts (SPEC §3, approved decision)', () {
    test('cold start within 30 minutes resumes; later cold start rotates', () {
      final base = DateTime.utc(2026, 7, 13);
      harness = Harness(now: () => base);

      final first = harness.build();
      first.init();
      final firstKey = first.eventHeaders()['X-Session-Key'];
      first.dispose();

      final soon = harness.build(now: () => base.add(const Duration(minutes: 10)));
      soon.init();
      expect(soon.eventHeaders()['X-Session-Key'], firstKey);
      expect(harness.store.values['ep_session_number'], '1');
      soon.dispose();

      final late = harness.build(now: () => base.add(const Duration(minutes: 45)));
      late.init();
      expect(late.eventHeaders()['X-Session-Key'], isNot(firstKey));
      expect(harness.store.values['ep_session_number'], '2');
      // anonymous_id never rotates with sessions
      expect(late.eventHeaders()['X-Anonymous-Id'], first.eventHeaders()['X-Anonymous-Id']);
      late.dispose();
    });

    test('resumed lifecycle after >30 min rotates and re-registers', () {
      fakeAsync((async) {
        final base = DateTime.utc(2026, 7, 13);
        harness = Harness(now: () => base.add(async.elapsed));
        final client = harness.build();
        client.init();
        async.flushMicrotasks();
        final firstKey = client.eventHeaders()['X-Session-Key'];

        async.elapse(const Duration(minutes: 31));
        client.handleLifecycle(AppLifecycleState.resumed);
        async.flushMicrotasks();

        expect(client.eventHeaders()['X-Session-Key'], isNot(firstKey));
        final identities = harness.transport.identityBodies();
        expect(identities, hasLength(2));
        expect(identities.last['session_number'], 2);
        client.dispose();
      });
    });
  });

  group('user transitions (SPEC §3)', () {
    test('setUser re-registers without rotating; clearUser rotates session only', () {
      fakeAsync((async) {
        final base = DateTime.utc(2026, 7, 13);
        harness = Harness(now: () => base.add(async.elapsed));
        final client = harness.build();
        client.init();
        async.flushMicrotasks();
        final before = harness.transport.identityBodies().first;

        client.setUser('u-42');
        async.flushMicrotasks();
        final withUser = harness.transport.identityBodies().last;
        expect(withUser['user_id'], 'u-42');
        expect(withUser['anonymous_id'], before['anonymous_id']);
        expect(withUser['session_key'], before['session_key']);

        client.clearUser();
        async.flushMicrotasks();
        final cleared = harness.transport.identityBodies().last;
        expect(cleared.containsKey('user_id'), isFalse);
        expect(cleared['session_key'], isNot(before['session_key']));
        expect(cleared['session_number'], 2);
        expect(cleared['anonymous_id'], before['anonymous_id']);
        client.dispose();
      });
    });
  });

  group('engagement time (SPEC §4)', () {
    test('each event carries foreground ms since the previous event', () {
      fakeAsync((async) {
        final base = DateTime.utc(2026, 7, 13);
        harness = Harness(now: () => base.add(async.elapsed));
        final client = harness.build();
        client.init();
        async.flushMicrotasks();

        async.elapse(const Duration(milliseconds: 1200));
        client.track('product_viewed');
        async.elapse(const Duration(milliseconds: 3400));
        client.track('product_viewed');
        client.flush();
        async.flushMicrotasks();

        final events = harness.transport.sentEvents();
        expect((events[0]['context']! as Map)['engagement_time_msec'], 1200);
        expect((events[1]['context']! as Map)['engagement_time_msec'], 3400);
        client.dispose();
      });
    });

    test('stopwatch pauses while backgrounded', () {
      fakeAsync((async) {
        final base = DateTime.utc(2026, 7, 13);
        harness = Harness(now: () => base.add(async.elapsed));
        final client = harness.build();
        client.init();
        async.flushMicrotasks();

        async.elapse(const Duration(seconds: 2));
        client.handleLifecycle(AppLifecycleState.paused);
        async.elapse(const Duration(minutes: 5)); // background time not counted
        client.handleLifecycle(AppLifecycleState.resumed);
        async.elapse(const Duration(milliseconds: 500));
        client.track('product_viewed');
        client.flush();
        async.flushMicrotasks();

        final events = harness.transport.sentEvents();
        expect((events.last['context']! as Map)['engagement_time_msec'], 2500);
        client.dispose();
      });
    });
  });

  group('queue give-up (SPEC §7)', () {
    test('events older than 24h are dropped, not sent', () {
      fakeAsync((async) {
        final base = DateTime.utc(2026, 7, 13);
        harness = Harness(now: () => base.add(async.elapsed));
        harness.transport.manual = true;
        final client = harness.build();
        client.init();
        async.flushMicrotasks();
        harness.transport.completeNext(); // identity ok
        async.flushMicrotasks();

        client.track('product_viewed');
        client.flush();
        async.flushMicrotasks();
        harness.transport.completeNext(false); // send fails, stays queued
        async.flushMicrotasks();

        async.elapse(const Duration(hours: 25));
        final sendsBefore =
            harness.transport.calls.where((c) => c.$1 == '/v1/events').length;
        client.flush();
        async.flushMicrotasks();
        final sendsAfter =
            harness.transport.calls.where((c) => c.$1 == '/v1/events').length;
        expect(sendsAfter, sendsBefore); // nothing left to send
        client.dispose();
      });
    });
  });

  group('reportError (thin /v1/errors path)', () {
    test('posts immediately, bypassing the queue and the S4 gate', () {
      fakeAsync((async) {
        final base = DateTime.utc(2026, 7, 14);
        harness = Harness(now: () => base.add(async.elapsed));
        harness.transport.manual = true; // identity registration hangs
        final client = harness.build();
        client.init();
        async.flushMicrotasks();

        client.reportError(StateError('bad state'), StackTrace.current);
        async.flushMicrotasks();

        final errors =
            harness.transport.calls.where((c) => c.$1 == '/v1/errors').toList();
        expect(errors, hasLength(1));
        final body = errors.single.$2;
        expect(body['kind'], 'StateError');
        expect(body['message'], contains('bad state'));
        expect(body['stack'], isNotEmpty);
        expect(body['anonymous_id'], isNotNull);
        expect(body['session_key'], isNotNull);
        expect((body['sdk']! as Map)['name'], sdkName);
        expect(harness.transport.calls.where((c) => c.$1 == '/v1/events'), isEmpty);
        client.dispose();
      });
    });
  });

  group('eventHeaders (SPEC §8)', () {
    test('returns the tier-2 headers after init', () {
      harness = Harness(now: DateTime.now);
      final client = harness.build();
      client.init();
      final headers = client.eventHeaders('add_to_cart');
      expect(headers['X-Event'], 'add_to_cart');
      expect(headers['X-Session-Key'], isNotEmpty);
      expect(headers['X-Anonymous-Id'], isNotEmpty);
      client.dispose();
    });
  });

  group('setUserAttributes (SPEC §6.1)', () {
    test('is a no-op without a prior setUser call', () {
      fakeAsync((async) {
        final base = DateTime.utc(2026, 7, 13);
        harness = Harness(now: () => base.add(async.elapsed));
        final client = harness.build();
        client.init();
        async.flushMicrotasks();
        final before = harness.transport.calls.length;

        client.setUserAttributes({'email': 'a@b.co'});
        async.flushMicrotasks();

        expect(harness.transport.calls.length, before); // no round-trip
        client.dispose();
      });
    });

    test('posts allowlisted attributes with user_id after setUser', () {
      fakeAsync((async) {
        final base = DateTime.utc(2026, 7, 13);
        harness = Harness(now: () => base.add(async.elapsed));
        final client = harness.build();
        client.init();
        async.flushMicrotasks();

        client.setUser('u-42');
        async.flushMicrotasks();
        client.setUserAttributes({
          'first_name': 'Ali',
          'email': 'ali@example.com',
          'phone': '+9647701234567',
          'gender': 'male',
          'city': 'Baghdad',
        });
        async.flushMicrotasks();

        final identity = harness.transport.identityBodies().last;
        expect(identity['user_id'], 'u-42');
        final attributes = identity['attributes']! as Map;
        expect(attributes['first_name'], 'Ali');
        expect(attributes['email'], 'ali@example.com');
        expect(attributes['phone'], '+9647701234567');
        expect(attributes['gender'], 'male');
        expect(attributes['city'], 'Baghdad');
        client.dispose();
      });
    });

    test('drops keys outside the six-name allowlist locally', () {
      fakeAsync((async) {
        final base = DateTime.utc(2026, 7, 13);
        harness = Harness(now: () => base.add(async.elapsed));
        final client = harness.build();
        client.init();
        async.flushMicrotasks();
        client.setUser('u-42');
        async.flushMicrotasks();

        client.setUserAttributes({
          'first_name': 'Ali',
          'ssn': '123-45-6789',        // not allowlisted
          'passport_number': 'X99',    // not allowlisted
        });
        async.flushMicrotasks();

        final attributes = harness.transport.identityBodies().last['attributes']! as Map;
        expect(attributes.keys, ['first_name']);
        expect(attributes.containsKey('ssn'), isFalse);
        expect(attributes.containsKey('passport_number'), isFalse);
        client.dispose();
      });
    });

    test('does not send when every provided key is dropped', () {
      fakeAsync((async) {
        final base = DateTime.utc(2026, 7, 13);
        harness = Harness(now: () => base.add(async.elapsed));
        final client = harness.build();
        client.init();
        async.flushMicrotasks();
        client.setUser('u-42');
        async.flushMicrotasks();
        final before = harness.transport.calls.length;

        client.setUserAttributes({'ssn': '123'});
        async.flushMicrotasks();

        expect(harness.transport.calls.length, before);
        client.dispose();
      });
    });
  });
}
