/**
 * User-attribute allowlist mirror (SPEC §6.1). The tracking plan on the
 * server is the source of truth; the SDK ships this hardcoded set of the
 * six v1.1 attributes so bad calls are dropped locally without a round-trip
 * rejection. Adding a name here requires an SDK release; the server enforces
 * the same set independently.
 */
export const USER_ATTRIBUTES_ALLOWLIST: ReadonlySet<string> = new Set([
  'first_name',
  'last_name',
  'email',
  'phone',
  'gender',
  'city',
]);
