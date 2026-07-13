import 'package:dio/dio.dart';

import 'client.dart';

/// Dio interceptor for the tier-2 X-Event pattern (SPEC §8): an event is
/// emitted by the platform endpoint only when the request carries the
/// headers — opt in per request via `Options(extra: {'epEvent': 'name'})`:
///
///   dio.post('/cart', data: ..., options: Options(extra: {'epEvent': 'add_to_cart'}));
class EventPumpDioInterceptor extends Interceptor {
  EventPumpDioInterceptor([EventPumpClient? client]) : _client = client;

  final EventPumpClient? _client;

  EventPumpClient get _resolved => _client ?? EventPump.instance;

  @override
  void onRequest(RequestOptions options, RequestInterceptorHandler handler) {
    final eventName = options.extra['epEvent'];
    if (eventName is String && eventName.isNotEmpty) {
      options.headers.addAll(_resolved.eventHeaders(eventName));
    }
    handler.next(options);
  }
}
