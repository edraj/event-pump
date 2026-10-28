/// User-attribute allowlist mirror (SPEC §6.1). The tracking plan on the
/// server is the source of truth; the SDK ships this hardcoded set of the
/// six v1.1 attributes so bad calls are dropped locally without a round-trip
/// rejection. Adding a name here requires an SDK release; the server enforces
/// the same set independently.
const Set<String> kUserAttributesAllowlist = {
  'first_name',
  'last_name',
  'email',
  'phone',
  'gender',
  'city',
};

/// Identity-handle allowlist mirror (SPEC §9 / IdentityValidation.cs).
/// Handles are platform-specific identifiers that senders use to stitch EP
/// events to a destination's own identity model (GA4 client id, Amplitude
/// device/session id, Adjust ADID, Meta fbp/fbc, etc.). The server-side
/// list is authoritative; this mirror lets the SDK drop unknown keys
/// client-side instead of taking a round-trip rejection.
const Set<String> kIdentityHandlesAllowlist = {
  'ga4_client_id',
  'ga4_session_id',
  'firebase_app_instance_id',
  'amplitude_device_id',
  'amplitude_session_id',
  'adjust_adid',
  'adjust_platform_ad_id',
  'fbp',
  'fbc',
  'moengage_customer_id',
  'ga4_user_id',
  'amplitude_user_id',
  'meta_external_id',
};
