import 'package:dio/dio.dart';
import 'package:event_pump/event_pump.dart';
import 'package:flutter_test/flutter_test.dart';

import 'fakes.dart';

void main() {
  late Harness harness;

  tearDown(() => harness.cleanup());

  group('EventPumpDioInterceptor (SPEC §8 tier-2)', () {
    test('stamps X-Event headers only on opted-in requests', () async {
      harness = Harness(now: DateTime.now);
      final client = harness.build();
      client.init();

      final interceptor = EventPumpDioInterceptor(client);

      final optedIn = RequestOptions(path: '/cart', extra: {'epEvent': 'add_to_cart'});
      interceptor.onRequest(optedIn, RequestInterceptorHandler());
      expect(optedIn.headers['X-Event'], 'add_to_cart');
      expect(optedIn.headers['X-Session-Key'], client.eventHeaders()['X-Session-Key']);
      expect(optedIn.headers['X-Anonymous-Id'], client.eventHeaders()['X-Anonymous-Id']);

      final plain = RequestOptions(path: '/cart');
      interceptor.onRequest(plain, RequestInterceptorHandler());
      expect(plain.headers.containsKey('X-Event'), isFalse);
      expect(plain.headers.containsKey('X-Session-Key'), isFalse);

      client.dispose();
    });
  });
}
