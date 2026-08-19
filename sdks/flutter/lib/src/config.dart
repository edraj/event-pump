/// SDK configuration (SPEC §7).
class EventPumpConfig {
  const EventPumpConfig({
    required this.endpoint,
    required this.tenantApiKey,
    this.appVersion,
    this.build,
    this.debug = false,
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
}
