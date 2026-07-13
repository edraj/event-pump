# event_pump

Event Pump Flutter SDK. Dio transport; no third-party tracking libraries; no
hardware ids — `anonymous_id` is a persisted random UUID that dies with the
install (by design, see /SPEC.md §2).

## Usage

```dart
await EventPump.init(const EventPumpConfig(
  endpoint: 'https://collect.example.com',
  appToken: 'YOUR_APP_TOKEN',
));

EventPump.instance.track('product_viewed', {'sku': 'A1'});
EventPump.instance.screen('CheckoutScreen');
EventPump.instance.setUser('user-123'); // login only
EventPump.instance.clearUser();         // logout: rotates the session only
```

### Tier-2 X-Event pattern (dio)

```dart
final dio = Dio()..interceptors.add(EventPumpDioInterceptor());
await dio.post('/cart/items',
    data: {'sku': 'A1'},
    options: Options(extra: {'epEvent': 'add_to_cart'}));
```

Plain http: `http.post(uri, headers: {...epEventHeaders('add_to_cart')})`.

### Late Adjust adid (the designed path)

```dart
final adid = await Adjust.getAdid();
await EventPump.instance.identify({'adjust_adid': adid});
```

### Optional automatic screens (off by default)

```dart
MaterialApp(navigatorObservers: [EventPumpRouteObserver()], ...)
```

## Behavior guarantees

- Sessions persist across quick restarts and rotate after 30 minutes in
  background (GA4 window); `session_key` is a UUIDv7.
- Offline queue: JSONL file in the app-support dir, cap 500, atomic
  truncate-on-success; flushes on regained connectivity; server dedupes on
  `event_id`.
- Context (device model, OS, app version, screen metrics) is collected after
  the first frame and patched via a partial `/v1/identity` — `init()` never
  blocks on platform channels.

## Develop

```bash
flutter pub get
flutter test
```

See `example/` for a complete app (init + track + interceptor + late
`identify`).
