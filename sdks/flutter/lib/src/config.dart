/// SDK configuration (SPEC §7).
class EventPumpConfig {
  const EventPumpConfig({
    required this.endpoint,
    required this.appToken,
    this.appVersion,
    this.build,
    this.debug = false,
  });

  /// Ingestion API base, e.g. `https://collect.example.com`.
  final String endpoint;

  /// Per-app bearer token (identifies + rate-limits; not a secret).
  final String appToken;

  final String? appVersion;
  final String? build;
  final bool debug;
}
