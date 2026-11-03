# event_pump

Event Pump Flutter SDK. Dio transport; no third-party tracking libraries; no
hardware ids — `anonymous_id` is a persisted random UUID that dies with the
install (by design, see /SPEC.md §2).

## Usage

```dart
await EventPump.init(const EventPumpConfig(
  endpoint:     'https://collect.example.com',
  tenantApiKey: 'YOUR_TENANT_API_KEY',   // sent as Bearer; pump resolves the tenant from this
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

### Amplitude alongside its own SDK (hybrid tracking)

Forward the Amplitude SDK's ids so client and server events land on the same
Amplitude device and session:

```dart
await EventPump.instance.identify({
  'amplitude_device_id': await amplitude.getDeviceId(),
  'amplitude_session_id': await amplitude.getSessionId(),  // int is fine
});
```

Call it once after `init()`, and again whenever Amplitude starts a new
session (e.g. on resume). Forwarded device ids are remembered across launches
and re-sent with every registration, so a login or logout never puts the
`anonymous_id` default back; each event carries the Amplitude session it was
tracked in.

### MoEngage before login (on by default)

MoEngage needs a customer id, and a session nobody has logged into has none.
So registrations made while no user is set carry
`moengage_customer_id: anonymousId`, and pre-login events, `first_visit`
included, reach MoEngage under the device id. After `setUser()` the user id
takes over. Give MoEngage's own SDK the same id:

```dart
final ep = await EventPump.init(const EventPumpConfig(
  endpoint:     'https://collect.example.com',
  tenantApiKey: 'YOUR_TENANT_API_KEY',
));
MoEngage.setUniqueId(ep.anonymousId!);  // re-read after every init; it can change
```

Every device becomes a registered (billed) MoEngage user. To keep pre-login
traffic out of MoEngage, pass `moengageDeviceCustomerId: false`. Don't pass
`anonymousId` to `identify()` yourself: that call races the registration, and
the events it misses are skipped for good.

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

## License

LGPL-3.0-only, GNU Lesser General Public License v3.0: [LICENSE](LICENSE),
with the GPLv3 text it extends in [COPYING](COPYING). Linking `event_pump`
into an application does not place the application under copyleft. 0.2.0 is
the first version under these terms (see [CHANGELOG](CHANGELOG.md)); the
Event Pump server is AGPL-3.0-only.
