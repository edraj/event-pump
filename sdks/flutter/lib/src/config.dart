/// SDK configuration (SPEC §7).
class EventPumpConfig {
  const EventPumpConfig({
    required this.endpoint,
    required this.tenantApiKey,
    this.appVersion,
    this.build,
    this.debug = false,
    this.moengageDeviceCustomerId = false,
  });

  /// Ingestion API base, e.g. `https://collect.example.com`.
  final String endpoint;

  /// Per-tenant API key (SPEC v1.2). Sent as
  /// `Authorization: Bearer <key>` on every request. The pump resolves the
  /// tenant from this value. Treat as a secret to the extent your build
  /// pipeline allows — it ships in the APK.
  final String tenantApiKey;

  final String? appVersion;
  final String? build;
  final bool debug;

  /// Key pre-login MoEngage traffic on this device (off by default).
  ///
  /// MoEngage events need a customer id, and before [EventPumpClient.setUser]
  /// there is none, so the pump skips them as `no_user_id`. When true, every
  /// registration made while no user is set carries
  /// `moengage_customer_id: anonymousId`, so pre-login events — `first_visit`
  /// included, since it is emitted by that same registration — reach MoEngage
  /// under the device id. Pair it with
  /// `MoEngage.setUniqueId(EventPump.instance.anonymousId!)` so the app's own
  /// MoEngage SDK names the same profile.
  ///
  /// Each device then becomes a registered MoEngage user, which is why this
  /// is opt-in. Once a user is set, their user_id (or a
  /// `moengage_customer_id` passed to identify()) takes over; the pump never
  /// keys a logged-in event on the device id. Erasing a person also deletes
  /// the device profiles of the devices they logged in on (SPEC §9.7).
  final bool moengageDeviceCustomerId;
}
