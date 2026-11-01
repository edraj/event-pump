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

// Identity-handle allowlist was mirrored here in an earlier draft, but the
// server already silently drops unknown handles (default: break in
// IdentityValidation), so the local mirror saved no round-trip and only
// blocked forward compatibility: a handle added on the server couldn't be
// sent via setHandles until a new app build shipped through the stores,
// which is exactly the forward-compat the server's lenient handling exists
// for. Removed.
