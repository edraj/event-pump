import 'client.dart';

/// Plain-http helper for the tier-2 X-Event pattern (SPEC §8):
///
///   http.post(uri, headers: {...epEventHeaders('add_to_cart'), ...});
Map<String, String> epEventHeaders(String eventName) =>
    EventPump.instance.eventHeaders(eventName);
