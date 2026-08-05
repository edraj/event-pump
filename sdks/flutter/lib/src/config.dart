/// SDK configuration (SPEC §7).
class EventPumpConfig {
  const EventPumpConfig({
    required this.endpoint,
    required this.appId,
    required this.appToken,
    this.appVersion,
    this.build,
    this.debug = false,
  });

  /// Ingestion API base, e.g. `https://collect.example.com`.
  final String endpoint;

  /// Stable identifier of this app (matches the tenant's `app_id` on the
  /// pump). Sent as `X-App-Id` on every request. The pump cross-checks it
  /// against the tenant resolved from `appToken` — a mismatch is 401. Comes
  /// from the app's own `pubspec.yaml` name (e.g. `zain_mart`).
  final String appId;

  /// Per-app bearer token (identifies + rate-limits; not a secret).
  final String appToken;

  final String? appVersion;
  final String? build;
  final bool debug;
}
