# Design — Account Attributes (v1.1)

**Status:** APPROVED (design) — pending green light to apply §8 SPEC diff, then Step 2 (implementation) begins.
**Branch:** `account-attributes`
**Scope:** Adds first-class user attributes / traits so a Flutter or web client
can call `setUserAttributes({...})` after `setUser()` and have six allowlisted
PII fields flow end-to-end to GA4, Amplitude, MoEngage (`type:"customer"`),
and Adjust — closing the gap where today only `user_id` propagates.
**SPEC change:** Yes — introduces new §6.1, extends §7, §9.2, adds §9.6, extends
§11, §12, §13. Approval required per SPEC §14.

---

## 1. Summary of design decisions

| # | Decision | Rationale (short) |
|---|---|---|
| D1 | Name: `attributes` for wire / DB / tracking-plan / errors; SDK method `setUserAttributes` | Matches branch name and initial snippet; avoids collision with event `properties`; avoids Segment-flavored "traits" |
| D2 | Wire slot: new top-level `attributes` object on `POST /v1/identity`, sibling of `handles` and `context` | Semantically distinct from destination-ID handles and auto-collected device context |
| D3 | Allowlist in tracking-plan JSON | Matches existing pattern (`events` block); per-deployment editable without code change |
| D4 | Storage: new `user_attributes` table keyed by `user_id` — person-scoped | Attributes persist across sessions, devices, logouts (unlike session-scoped identity_registry) |
| D5 | Initial allowlist: `first_name`, `last_name`, `email`, `phone`, `gender`, `city` | Minimum useful set; more added via tracking-plan edit |
| D6 | Server-side normalization: email lowercase, phone E.164, gender enum, string trim | Consistent canonical values regardless of client platform |
| D7 | Canonical raw values in DB; per-destination naming / hashing done at send time in each sender | Storage stays destination-neutral; hashing lives where the destination knows what it wants |
| D8 | Attribute values are PII: never logged; log names only | Extension of existing SPEC §13 "no payloads in logs" rule |
| D9 | SDK method requires `user_id` first; no-op (release) / warn (debug) otherwise; hardcoded six-name allowlist on client | Attributes are person-scoped; ordering enforced in login flow |
| D10 | Tracking plan is the feature gate — empty/absent `attributes` block = off | No separate `EP_ATTRIBUTES_ENABLED` env var; one gate, not two |
| D11 | Per-destination mapping table — final wire format verified against current docs at implementation time (§12) | Follows existing repo pattern (senders never code from memory) |
| D12 (revised) | MoEngage `type:"customer"` sync routed through the **outbox** as a synthetic delivery to new destination `moengage_customer`, reserved event `ep_attributes_synced` | Fire-and-forget failed: most users set attributes once at signup and never again — one lost sync = never delivered. Outbox gives retry / dead-letter / circuit breaker for free |
| D13 | Attributes never rescue otherwise-skipped events | Preserves the "identity is never fabricated" rule |
| D14 (new) | Consent gate for PII-bearing outbound fields: per-destination `EP_<X>_ATTRIBUTES_ENABLED` env vars, defaults chosen per destination sensitivity | Meta CAPI already gates hashed PII behind a config flag; GA4 `user_data` and Adjust `s2s_email` carry equivalent hashed PII and were previously ungated — consent posture should be consistent |
| D15 (new) | Flutter `track()` param becomes named: `track(event_name, {properties})` | Keep the concept name `properties` for event payload (distinct from `attributes`); switch from positional to named for readability at the call site (`ep.track('x', properties: {...})`) |
| N1 | DSR delete endpoint in this branch — `DELETE /internal/v1/user_attributes/{user_id}` — **DB-only** (O5 locked) | Person-scoped storage needs person-scoped deletion; internal listener + bearer token. Fan-out to destination delete APIs deferred to follow-up branch; gap documented in SPEC §9.6 |
| N2 | Retention policy: deferred to follow-up branch | Compliance/product decision, not a technical one; rows persist until DSR-deleted |

---

## 2. Layer 1 — Wire contract

**New top-level `attributes` object on `POST /v1/identity`**, sibling of `handles` and `context`:

```json
{
  "session_key": "01924abc-...",
  "anonymous_id": "b3e8...",
  "session_number": 3,
  "user_id": "zm_84321",
  "handles":  { "ga4_client_id": "GA1.1..." },
  "attributes": {
    "first_name": "Ali",
    "last_name": "Hassan",
    "email": "ali@example.com",
    "phone": "+9647701234567",
    "gender": "male",
    "city": "Baghdad"
  },
  "context":  { "language": "ar", "os": "iOS" }
}
```

**Rules:**

- **Partial upsert** — keys present replace, keys absent survive. To clear a stored value, send `null` for that key.
- **Requires `user_id`** — the `attributes` block requires `user_id` in the same body OR already stored on `identity_registry[session_key]` from a prior upsert. Otherwise reject with `attributes_require_user_id` (400).
- **Server normalizes** each value before storing (see Layer 3).
- **Rejection codes:**
  - `unknown_attribute:<name>` — 400, name not in tracking-plan allowlist
  - `invalid_attribute:<name>` — 400, value fails normalization (e.g. phone not E.164)
  - `attributes_too_large` — 400, serialized `attributes` object > 4 KB
  - `attributes_require_user_id` — 400, `attributes` present without a `user_id` in scope

**Naming:** wire key, DB column, tracking-plan block, error codes all use `attributes`. SDK method is `setUserAttributes` (both SDKs — "user" reads naturally in the method name).

**Naming separation from `track()`.** Distinct concepts, distinct names — do not mix:

| Concept | Describes | Name | Where |
|---|---|---|---|
| event payload | an *event* | `properties` | `track(event_name, properties)` — SPEC §1 event model field |
| user attributes | a *person* | `attributes` | `setUserAttributes(attributes)`, wire block on `/v1/identity`, DB column, tracking-plan block |

The conceptual name `properties` stays for event payload. What changes in this branch is the **Flutter** call ergonomics: the parameter goes from positional to **named** (`properties:`), so the call site reads `ep.track('user_logged_in', properties: {'method': 'otp'})`. See D15 and Layer 4.

---

## 3. Layer 2 — Storage

**New table `user_attributes`, keyed by `user_id`.** Not a column on `identity_registry`. Attributes are person-scoped and persist across sessions, devices, and logouts.

```sql
CREATE TABLE user_attributes (
    user_id              text        PRIMARY KEY,
    attributes           jsonb       NOT NULL DEFAULT '{}',
    hash                 text,        -- sha256(canonical_json(attributes))
    moengage_synced_hash text,        -- last hash successfully delivered to MoEngage type:"customer"
    moengage_synced_at   timestamptz,
    created_at           timestamptz NOT NULL DEFAULT now(),
    updated_at           timestamptz NOT NULL DEFAULT now()
);
```

**Semantics:**

- `identity_registry` is unchanged. Session/device data stays there; person data lives in `user_attributes`.
- Stores **raw canonical values** post-normalization (email lowercased, phone in E.164, gender in canonical enum). No hashing at rest — hashing happens in each sender at send time per its destination's requirement.
- Upsert:
  ```sql
  INSERT INTO user_attributes (user_id, attributes, hash, updated_at)
  VALUES ($1, $2, $3, now())
  ON CONFLICT (user_id) DO UPDATE
    SET attributes = user_attributes.attributes || excluded.attributes,
        hash       = <recomputed>,
        updated_at = now();
  ```
- `null` values in the incoming JSON are handled by the merge to remove keys (details in Step 2 — either strip nulls to remove, or use `jsonb_strip_nulls` post-merge).
- `moengage_synced_hash` is updated by the MoEngage sender **only after a successful `type:"customer"` delivery** — the gap between it and `hash` drives whether a new sync gets enqueued (see §6.3).
- **Retention: TBD (deferred to follow-up branch).** Rows persist indefinitely until explicit DSR deletion. Per-user deletion is handled by the DSR endpoint (Layer 6) from day one; bulk age-out policy is a compliance/product decision needing separate input.

---

## 4. Layer 3 — Allowlist and normalization

**Allowlist lives in the tracking-plan JSON.** The server validator loads it at boot (same path as `event_registry` today). An empty or absent `attributes` block means the feature is off for that deployment.

```json
{
  "attributes": {
    "first_name": { "type": "string", "max_length": 128 },
    "last_name":  { "type": "string", "max_length": 128 },
    "email":      { "type": "email",  "max_length": 254 },
    "phone":      { "type": "e164",   "max_length": 16  },
    "gender":     { "type": "enum",   "values": ["male", "female", "other", "unknown"] },
    "city":       { "type": "string", "max_length": 128 }
  },
  "events": {
    "product_viewed": { "origin": "client", "destinations": ["ga4", "amplitude"] },
    "ep_attributes_synced": { "origin": "server", "reserved": true, "destinations": ["moengage_customer"] }
  }
}
```

**Normalization rules (server-side, at validation):**

| Name | Rule |
|---|---|
| `first_name`, `last_name`, `city` | trim whitespace; max 128 chars |
| `email` | trim + lowercase; basic RFC 5322 shape (`local@domain`); max 254 chars |
| `phone` | E.164: `+` then 8–15 digits, no separators; max 16 chars |
| `gender` | one of `male` / `female` / `other` / `unknown` |

**Type/normalizer functions** (`string`, `email`, `e164`, `enum`) live in server code — the tracking plan references them by name. Adding a new name that reuses an existing type is a plan edit; adding a truly new type (e.g. `iso8601-date`) needs a code change.

**Reserved event `ep_attributes_synced`** is auto-registered at boot; producers cannot use it via `emit_event()` or `/internal/v1/events`. It exists solely as the routing key for the MoEngage customer-sync path (§6.3).

**Reserved-event enforcement.** The event name `ep_attributes_synced` complies with SPEC §1's `^[a-z][a-z0-9_]{0,63}$` regex — no exemption needed. The "manually uncallable" property is enforced separately via the tracking-plan entry's `"reserved": true` flag: at boot, the tracking-plan loader collects reserved names into a set, and both `emit_event()` (server-side SQL) and `/internal/v1/events` (HTTP) reject calls with names in that set (error: `reserved_event_name`). Convention: the `ep_*` prefix is reserved for internal Event Pump events; only entries with `"reserved": true` may use it.

> **Consistency note (out of scope, worth flagging):** the existing `first_visit` server event (SPEC §8) is emitted only by the server's first-visit logic — it's morally reserved but not currently marked `"reserved": true` in the tracking plan. Adding the flag to `first_visit` is a small consistency fix that could be done in this branch or a follow-up.

---

## 5. Layer 4 — SDK

### 5.1 `setUserAttributes(Map)` — both SDKs

Same name, same shape, same semantics.

```dart
// Flutter
Future<void> onAuthSuccess(Account account) async {
  final ep = EventPump.instance;
  ep.setUser(account.id);
  ep.setUserAttributes({
    'first_name': account.firstName,
    'last_name':  account.lastName,
    'email':      account.email,
    'phone':      account.phone,       // must be E.164
    'gender':     account.gender,      // 'male' | 'female' | 'other' | 'unknown'
    'city':       account.city,
  });
  ep.track('user_logged_in', properties: {'method': 'otp', 'type': 'mobile'});
}
```

```js
// Web
ep.setUser(account.id);
ep.setUserAttributes({ first_name, last_name, email, phone, gender, city });
ep.track('user_logged_in', { method: 'otp', type: 'web' });
```

**Behavior:**

- **Requires `user_id`** already set via `setUser()`. If not: **no-op in release, `debugPrint` / `console.warn` in debug builds.**
- **Partial upsert** — call multiple times to set/change different keys. `null` clears a key.
- **Persists across sessions on the server** — `clearUser()` (logout) rotates `session_key` but does NOT delete `user_attributes`. Next login for the same `user_id` picks up the stored attributes automatically.
- **SDK hardcodes the shipped six-name allowlist.** Unknown keys dropped locally: **silent in release, `debugPrint` / `console.warn` in debug.**
- **Web SDK size:** additional bytes must stay within the ≤ 8 KB gzipped budget (SPEC §14). New size reported at implementation.

### 5.2 Flutter `track()` — positional → named `properties:` (D15)

Small ergonomic change to the existing Flutter signature:

```diff
-void track(String eventName, [Map<String, Object?>? properties])
+void track(String eventName, {Map<String, Object?>? properties})
```

Call sites change from `ep.track('name', {...})` to `ep.track('name', properties: {...})`. This is a **breaking change** for any existing Flutter caller — flagged as a v1.1 SDK release note. Web is unchanged (JS has no named-arg concept; the existing `track(name, properties?)` reads fine positionally).

Rationale: reading `ep.track('user_logged_in', properties: {...})` makes the payload semantics explicit at the call site and prevents future confusion with `attributes` (person-level). Also matches the pattern most Dart SDK authors use for optional map/object parameters.

---

## 6. Layer 5 — Delivery

### 6.1 Per-destination mapping (intent — verified at code time per SPEC §12)

| Attribute | GA4 MP | Amplitude V2 | MoEngage `type:"customer"` | Adjust S2S |
|---|---|---|---|---|
| `first_name` | `user_properties.first_name` | `user_properties.first_name` | `attributes.first_name` (raw) | `partner_params.first_name` |
| `last_name` | `user_properties.last_name` | `user_properties.last_name` | `attributes.last_name` (raw) | `partner_params.last_name` |
| `email` | `user_data.sha256_email_address` | `user_properties.email` | `attributes.email` (raw) | `s2s_email` (SHA-256 of lowercase) |
| `phone` | `user_data.sha256_phone_number` (E.164 no `+`) | `user_properties.phone` | `attributes.mobile` (E.164 raw) | `s2s_phone` (SHA-256 no `+`) |
| `gender` | `user_properties.gender` | `user_properties.gender` | `attributes.gender` (mapped `M` / `F` / `O`) | `partner_params.gender` (mapped `m` / `f`) |
| `city` | `user_properties.city` | `user_properties.city` | `attributes.city` | `partner_params.city` |

### 6.2 Sender behavior

- Every event delivery does a second lookup — `SELECT attributes FROM user_attributes WHERE user_id = $1` — or combines it with the existing `identity_registry` lookup as a single `LEFT JOIN`.
- Events **without `user_id`** get no attributes attached. Correct — there's no person to describe.
- **Missing `user_attributes` row** = silently omit attribute fields. **Attributes never rescue an otherwise-skipped event** — a GA4 event is still `skipped: no_ga4_identity` when `ga4_client_id` is absent, regardless of attributes.
- **Attribute values are PII: never logged.** Structured logs and error responses reference attribute *names* only (`invalid_attribute:phone`), never *values*. Extension of SPEC §13.
- Attribute-derived fields on each destination are **gated by that destination's `_ATTRIBUTES_ENABLED` flag** (§6.4). Disabled = send the event core without attribute enrichment; never fail the delivery.

### 6.3 MoEngage `type:"customer"` sync — routed through the outbox (D12 revised)

MoEngage requires a separate `type:"customer"` request to set user attributes. Because most users set attributes once at signup and never re-emit them, a lost sync must be recoverable — so this path uses the standard outbox/delivery machinery, not fire-and-forget.

**Mechanics:**

- **New destination code `moengage_customer`** — a delivery target distinct from `moengage` (events). Its own delivery-row rows, its own retry state, its own circuit breaker.
- **Reserved server-origin event `ep_attributes_synced`** — auto-registered in `event_registry` at boot; producers cannot emit it manually. Its only route: `["moengage_customer"]`.
- **Enqueue rule.** On `POST /v1/identity` upsert where `attributes` was upserted AND `EP_MOENGAGE_ATTRIBUTES_ENABLED=true` AND `user_attributes.hash != user_attributes.moengage_synced_hash`: insert a synthetic outbox row (`event_name = 'ep_attributes_synced'`, `origin = 'server'`, `user_id = <the user>`), which fans out one delivery row to `moengage_customer`.
- **MoEngage sender dispatches by destination.** For `destination = 'moengage'` → send `type:"event"` payload as today (with attributes attached to events per §6.1's mapping). For `destination = 'moengage_customer'` → fetch `user_attributes` by `user_id`, send `type:"customer"` payload with the raw attributes. On success (`delivered`): update `user_attributes.moengage_synced_hash = hash` and `moengage_synced_at = now()`.
- **Retries.** Standard exponential backoff + dead-letter + per-destination circuit breaker (SPEC §11). If a customer sync goes dead, subsequent attribute changes will re-enqueue because the hash comparison still fails.
- **Safety valve.** A worker-hosted sweep job (piggybacks on the retention timer) can optionally scan for `user_attributes WHERE hash IS NOT NULL AND (moengage_synced_hash IS NULL OR moengage_synced_hash != hash) AND updated_at < now() - '1 hour'` and enqueue missing sync rows. Optional in v1.1 — the enqueue-on-upsert path should catch everything in the healthy case; the sweep exists to self-heal after DB restore / migration / bug where enqueue was missed.

### 6.4 Consent gating for PII outbound fields (D14 new)

Meta CAPI already gates hashed PII behind a `default OFF` config flag (SPEC §12). GA4 `user_data.sha256_email_address` and Adjust `s2s_email` carry equivalent hashed identifier PII with no gate — that's a consent-posture inconsistency. Same rule applies to attribute descriptors (`user_properties`, `partner_params`).

**Per-destination env vars (all default OFF unless noted):**

| Env var | Gates | Default | Rationale |
|---|---|---|---|
| `EP_GA4_ATTRIBUTES_ENABLED` | GA4 `user_properties` + `user_data` (all attribute-derived fields) | OFF | Requires explicit opt-in to send any PII (hashed or raw descriptors) to GA4 |
| `EP_AMPLITUDE_ATTRIBUTES_ENABLED` | Amplitude `user_properties` | OFF | Requires explicit opt-in to send descriptor PII to Amplitude |
| `EP_MOENGAGE_ATTRIBUTES_ENABLED` | MoEngage `type:"customer"` sync path (§6.3) | **ON** | MoEngage is designed to be the raw-PII destination; disabling it neuters the whole feature. Off means events still flow (`type:"event"` unchanged) but no customer sync happens |
| `EP_ADJUST_ATTRIBUTES_ENABLED` | Adjust `s2s_email` / `s2s_phone` + `partner_params` PII | OFF | Requires explicit opt-in to send hashed identifiers and descriptors to Adjust |

**When a flag is OFF:** the sender still delivers the event core (identity, event params, revenue, etc.) as today. Only the attribute-derived fields are omitted from the payload. No event is `skipped` because of a disabled attribute flag — this is enrichment, not identity.

Configuration surface documented in SPEC §13.

---

## 7. Layer 6 — DSR (Right-to-be-Forgotten) — DB-only (O5 locked)

**New endpoint:** `DELETE /internal/v1/user_attributes/{user_id}`

- Internal listener (same port as `/internal/v1/events`).
- Auth: `Authorization: Bearer <internal token>` (same as internal events).
- Deletes the `user_attributes` row for the given `user_id`.
- Idempotent: **204 whether the row existed or not**.
- **DB-only in v1.1.** Downstream cleanup (MoEngage / GA4 / Amplitude / Adjust delete APIs) is **deferred to a follow-up branch**. This is a documented compliance gap: after a DSR request is fulfilled by this endpoint, the user's PII is gone from our DB and from any future outbound payload, but historical data already delivered to destinations remains until their own retention or a manual per-destination deletion.
- SPEC §9.6 records the gap explicitly so it's not silent tech debt.

---

## 8. SPEC.md changes (exact diff)

### 8.1 Update approval banner (line 3)

```diff
-**Status: APPROVED v1.0 (2026-07-12). Behavior changes require spec re-approval.**
+**Status: APPROVED v1.1 (2026-07-14). Behavior changes require spec re-approval.**
+
+**v1.1 changes:** adds §6.1 (user attributes) — person-scoped storage
+(`user_attributes` table keyed by `user_id`), allowlisted in the tracking plan,
+delivered via existing senders under per-destination consent gates. Adds §9.6
+(DSR delete endpoint, DB-only). Extends §7 (SDK API: `setUserAttributes` on
+both SDKs; Flutter `track()` becomes named-arg for `properties:`), §9.2 (wire
+body), §11 (new table + reserved event `ep_attributes_synced` + destination
+`moengage_customer`), §12 (per-destination mapping), §13 (config + tracking
+plan). See §6.1.
```

### 8.2 New subsection §6.1 (insert immediately after §6, before the `---`)

````markdown
### 6.1 User attributes

Distinct from destination handles (§6): **attributes** describe the *person*,
not the device or session. Other CDPs call these "traits" (Segment) or
"user properties" (GA4/Amplitude) or "user attributes" (MoEngage).

- **Wire slot:** new top-level `"attributes"` object on `POST /v1/identity`
  (§9.2), sibling of `handles` and `context`. Partial upsert — keys present
  replace, keys absent survive. `null` value clears a stored key.
- **Requires `user_id`:** the `attributes` block requires `user_id` in the
  same body OR already stored on `identity_registry[session_key]`. Otherwise
  rejected with `attributes_require_user_id` (400).
- **Person-scoped storage:** table `user_attributes` keyed by `user_id`
  (§11). Persists across sessions, devices, and logouts. `clearUser()` does
  NOT delete stored attributes.
- **Allowlist:** declared in the tracking-plan JSON under `attributes`
  (§13). A name not in the allowlist is rejected with
  `unknown_attribute:<name>` (400). Empty/absent block disables the feature.
- **PII:** values are PII by construction. Never log values; log attribute
  *names* only (§13). Server error responses may reference names, never
  values.
- **SDK method (both SDKs):** `setUserAttributes(Map)` — partial upsert.
  Requires `user_id` (via `setUser()`) first, else no-op (release) /
  warn (debug). SDKs hardcode the shipped allowlist and drop unknown keys
  locally to avoid roundtrip rejections.
- **Consent gating:** attribute-derived outbound fields for each destination
  are behind per-destination `EP_<X>_ATTRIBUTES_ENABLED` flags (§13). When
  OFF, the destination sender still delivers the event core; only
  attribute-derived fields are omitted.

**Normative allowlist (v1.1):**

| Name         | Type   | Normalization                                     | Length cap |
|--------------|--------|---------------------------------------------------|------------|
| `first_name` | string | trim                                              | 128        |
| `last_name`  | string | trim                                              | 128        |
| `email`      | string | trim + lowercase; RFC 5322 basic shape            | 254        |
| `phone`      | string | E.164 (`+` then 8–15 digits, no separators)       | 16         |
| `gender`     | enum   | one of `male` / `female` / `other` / `unknown`    | —          |
| `city`       | string | trim                                              | 128        |

**Rejection codes:** `unknown_attribute:<name>`, `invalid_attribute:<name>`,
`attributes_too_large` (serialized `attributes` > 4 KB),
`attributes_require_user_id`.

**Per-destination mapping** (verified against each destination's current
public API docs at implementation time per §12):

| Attribute    | GA4 MP                              | Amplitude V2                  | MoEngage `type:"customer"`         | Adjust S2S                        |
|--------------|-------------------------------------|-------------------------------|------------------------------------|-----------------------------------|
| `first_name` | `user_properties.first_name`        | `user_properties.first_name`  | `attributes.first_name` (raw)      | `partner_params.first_name`       |
| `last_name`  | `user_properties.last_name`         | `user_properties.last_name`   | `attributes.last_name` (raw)       | `partner_params.last_name`        |
| `email`      | `user_data.sha256_email_address`    | `user_properties.email`       | `attributes.email` (raw)           | `s2s_email` (SHA-256 of lower)    |
| `phone`      | `user_data.sha256_phone_number`     | `user_properties.phone`       | `attributes.mobile` (E.164)        | `s2s_phone` (SHA-256, no `+`)     |
| `gender`     | `user_properties.gender`            | `user_properties.gender`      | `attributes.gender` (`M`/`F`/`O`)  | `partner_params.gender` (`m`/`f`) |
| `city`       | `user_properties.city`              | `user_properties.city`        | `attributes.city`                  | `partner_params.city`             |

A destination with no mapped attributes for the current stored state omits
them. **Attributes never rescue an event that would otherwise be `skipped`
for missing identity** (§12).

**MoEngage `type:"customer"` sync — routed through the outbox.** MoEngage
requires a separate `type:"customer"` payload to set user attributes. The
sync runs through the standard outbox/delivery machinery (retries,
dead-letter, circuit breaker) — not fire-and-forget — so a lost sync self-
heals on retry rather than requiring a repeat client call:

- New destination code `moengage_customer` (distinct from `moengage`
  events).
- Reserved server-origin event `ep_attributes_synced` (auto-registered;
  callers of `emit_event()` cannot use this name). Its only route:
  `["moengage_customer"]`.
- On `/v1/identity` upsert where `attributes` was changed AND
  `EP_MOENGAGE_ATTRIBUTES_ENABLED=true` AND
  `user_attributes.hash != user_attributes.moengage_synced_hash`: the API
  handler inserts a synthetic outbox row (`event_name =
  'ep_attributes_synced'`, `user_id = <the user>`), fanning out one
  delivery row to `moengage_customer`.
- The MoEngage sender dispatches by destination: `moengage` →
  `type:"event"`; `moengage_customer` → fetch `user_attributes` by
  `user_id`, send `type:"customer"`. On `delivered`: update
  `user_attributes.moengage_synced_hash = hash`, `moengage_synced_at =
  now()`.
- Optional worker sweep (piggybacks on retention timer): re-enqueue rows
  where `hash != moengage_synced_hash AND updated_at < now() - '1 hour'`.
  Belt-and-suspenders for the healthy enqueue-on-upsert path.
````

### 8.3 Extend §7 SDK public API blocks (line 285 and 297)

Web block:

```diff
 identify(handles: Partial<Handles>)
+setUserAttributes(attributes: Partial<Attributes>)   // §6.1 — partial upsert, requires setUser()
 eventHeaders(event_name?: string): Record<string, string>
```

Flutter block — both add `setUserAttributes` AND update `track()` signature to named-arg:

```diff
-EventPump.instance.track(name, {properties});
+EventPump.instance.track(name, properties: {...});      // v1.1: positional → named `properties:`
 EventPump.instance.screen(name, {properties});
 EventPump.instance.setUser(userId);  EventPump.instance.clearUser();
 EventPump.instance.identify({handles});          // e.g. late adjust_adid
+EventPump.instance.setUserAttributes({attributes}); // §6.1 — partial upsert, requires setUser()
 EventPump.instance.eventHeaders([eventName]);
```

Note in §7 body: v1.1 is a breaking change to `track()` for existing Flutter callers (positional `properties` becomes named); web signature unchanged.

### 8.4 Extend §9.2 body schema (lines 384–389)

```diff
-- Body: `{"session_key", "anonymous_id", "session_number", "user_id"?,
-  "first_seen_at"?, "handles"?: {…§6…}, "context"?: {…§5 full…}}`.
+- Body: `{"session_key", "anonymous_id", "session_number", "user_id"?,
+  "first_seen_at"?, "handles"?: {…§6…}, "attributes"?: {…§6.1…},
+  "context"?: {…§5 full…}}`.
 - **Partial upsert**: only the fields present are written. `handles.click_ids`
-  merges per click-id name (latest `captured_at` wins). `context` merges at the
+  merges per click-id name (latest `captured_at` wins). `attributes` merges at
+  the top level (present keys replace, absent keys survive; `null` clears a
+  key); values validated and normalized per §6.1; requires `user_id` in scope.
+  A hash change vs `user_attributes.moengage_synced_hash` enqueues a
+  `moengage_customer` delivery per §6.1. `context` merges at the
   top level (present keys replace, absent keys survive) so late collectors can
   patch without erasing earlier fields.
```

### 8.5 Add §9.6 DSR endpoint (DB-only, gap documented)

```markdown
### 9.6 `DELETE /internal/v1/user_attributes/{user_id}` — DSR deletion

- Internal listener, same as 9.3. Auth: `Authorization: Bearer <internal token>`.
- Deletes the `user_attributes` row for the given `user_id`.
- **Idempotent:** returns `204` whether the row existed or not.
- **DB-only in v1.1.** Fan-out to destination delete APIs (MoEngage,
  GA4 User Deletion, Amplitude User Privacy, Adjust Forget Device) is
  **deferred to a follow-up branch**. After this endpoint fulfills a DSR
  request, the user's PII is gone from our DB and from any future outbound
  payload — but data already delivered to destinations remains until their
  own retention or a manual per-destination deletion. Documented gap.
```

### 8.6 Extend §11 with `user_attributes` table + reserved event + `moengage_customer` destination

Insert after the `identity_registry` bullet (line 481):

```markdown
- **`user_attributes`** — person-scoped attribute store (§6.1), keyed by
  `user_id`. Columns: `{attributes jsonb, hash text, moengage_synced_hash
  text, moengage_synced_at timestamptz, created_at, updated_at}`. Partial
  upsert merges `attributes` at the top level (`||` operator). `hash` is
  `sha256(canonical_json(attributes))`; when `hash != moengage_synced_hash`
  and MoEngage attributes are enabled, `/v1/identity` enqueues a
  `moengage_customer` delivery (§6.1). Retention: TBD (deferred to
  follow-up); rows persist indefinitely until DSR deletion via §9.6.
- **Reserved server-origin event `ep_attributes_synced`** — auto-registered
  in `event_registry` at boot; routes to `moengage_customer` only.
  `emit_event()` rejects this name if called manually (raises
  `reserved_event_name`).
- **Destination `moengage_customer`** — separate delivery target from
  `moengage` (events). Independent circuit breaker and retry state. Handled
  by the MoEngage sender via a `type:"customer"` payload built from
  `user_attributes` (§6.1). On `delivered`, the sender updates the source
  row's `moengage_synced_hash` and `moengage_synced_at`.
```

### 8.7 Extend §12 destinations table

Update the MoEngage row and add a new row:

```diff
-| MoEngage Data API | `user_id` → their customer id | `skipped: no_user_id` | auth per current docs; receives `first_visit` |
+| MoEngage Data API (`moengage`) | `user_id` → their customer id | `skipped: no_user_id` | `type:"event"` transport; auth per current docs; receives `first_visit`; attribute-derived `user_properties`-equivalent per §6.1 gated by `EP_MOENGAGE_ATTRIBUTES_ENABLED` |
+| MoEngage customer sync (`moengage_customer`) | `user_id` + non-empty `attributes` | `skipped: no_attributes` / `skipped: no_user_id` / `skipped: attributes_disabled` | `type:"customer"` transport; triggered by `ep_attributes_synced` enqueue (§6.1); flag: `EP_MOENGAGE_ATTRIBUTES_ENABLED` (default ON) |
```

Add a note under the table:

```markdown
Each sender additionally pulls user attributes from `user_attributes`
(§6.1) via `user_id` and includes the mapped fields per §6.1's mapping
table, **gated by that destination's `EP_<X>_ATTRIBUTES_ENABLED` flag**
(§13). When the flag is OFF, the sender still delivers the event core;
attribute-derived fields are omitted. Missing rows or missing fields are
silently omitted. Attributes never rescue an event that would otherwise be
`skipped` for missing identity.
```

### 8.8 Extend §13 tracking-plan JSON example

```diff
 {
+  "attributes": {
+    "first_name": { "type": "string", "max_length": 128 },
+    "last_name":  { "type": "string", "max_length": 128 },
+    "email":      { "type": "email",  "max_length": 254 },
+    "phone":      { "type": "e164",   "max_length": 16  },
+    "gender":     { "type": "enum",   "values": ["male", "female", "other", "unknown"] },
+    "city":       { "type": "string", "max_length": 128 }
+  },
   "events": {
     "product_viewed": { "origin": "client", "destinations": ["ga4", "amplitude"] },
     "order_placed":   { "origin": "server", "destinations": ["ga4", "amplitude", "adjust", "moengage"],
                         "meta_name": "Purchase", "adjust_token": "abc123" },
+    "ep_attributes_synced": { "origin": "server", "reserved": true, "destinations": ["moengage_customer"] },
     "first_visit":    { "origin": "server", "destinations": ["moengage"] }
   }
 }
```

### 8.9 Extend §13 config env-var table

Add rows to the env-var table:

```markdown
| `EP_GA4_ATTRIBUTES_ENABLED` | Attribute-derived fields (`user_properties`, `user_data`) in GA4 payloads. Default: OFF |
| `EP_AMPLITUDE_ATTRIBUTES_ENABLED` | Attribute-derived `user_properties` in Amplitude payloads. Default: OFF |
| `EP_MOENGAGE_ATTRIBUTES_ENABLED` | MoEngage `type:"customer"` sync path (§6.1). Default: ON (MoEngage is the designated raw-PII destination) |
| `EP_ADJUST_ATTRIBUTES_ENABLED` | Attribute-derived `s2s_email` / `s2s_phone` / `partner_params` in Adjust payloads. Default: OFF |
```

### 8.10 Extend §13 observability line (lines 597–599)

```diff
-Structured JSON logs on every delivery state transition. **Never log payloads
-(PII):** `event_id` + `event_name` + `destination` + `status` only. No secrets in
-code, logs, or tests.
+Structured JSON logs on every delivery state transition. **Never log payloads
+or attribute values (PII):** `event_id` + `event_name` + `destination` +
+`status` only. Validation errors may reference attribute *names* (e.g.
+`invalid_attribute:phone`) but never *values*. No secrets in code, logs, or tests.
```

---

## 9. Open items (post-approval)

- **N2 — Retention policy:** deferred to follow-up branch. `user_attributes` grows unbounded until DSR-deleted. SPEC §11 records this as a known gap.
- **N3 — Shared-account leak:** if two people share one account, attributes propagate to all events with that `user_id`. Inherent to any user-scoped model; app-level identity concern, not fixable at this layer.
- **Follow-up branch — DSR fan-out to destination delete APIs.** N1 delivers DB-only deletion in v1.1; downstream cleanup (MoEngage, GA4 User Deletion, Amplitude User Privacy, Adjust Forget Device) is a separate initiative.

---

## 10. Files touched (Step 2 preview)

**Server:**
- `server/migrations/0004_user_attributes.sql` (new) — table + index; register `ep_attributes_synced` reserved event
- `server/src/EventPump/Api/IdentityValidation.cs` — accept `attributes` block, load allowlist, per-field normalization, hash + change-detection, enqueue `ep_attributes_synced` on hash mismatch
- `server/src/EventPump/Api/ApiApp.cs` — wire the validator + DSR endpoint
- `server/src/EventPump/Data/EventStore.cs` — `UserAttributesUpsert` record, upsert/delete SQL, sync-row insert
- `server/src/EventPump/Config/TrackingPlan.cs` — parse `attributes` block; reject manual use of `ep_attributes_synced`
- `server/src/EventPump/Config/EpConfig.cs` — four new `EP_<X>_ATTRIBUTES_ENABLED` env vars (§6.4)
- `server/src/EventPump/Senders/Ga4Sender.cs` — emit `user_properties` + `user_data` (SHA-256), gated by `EP_GA4_ATTRIBUTES_ENABLED`
- `server/src/EventPump/Senders/AmplitudeSender.cs` — emit `user_properties`, gated by `EP_AMPLITUDE_ATTRIBUTES_ENABLED`
- `server/src/EventPump/Senders/MoEngageSender.cs` — dispatch by destination: `moengage` → `type:"event"` (unchanged path); `moengage_customer` → `type:"customer"` with `user_attributes` fetch; on `delivered` update `moengage_synced_hash`. Whole path gated by `EP_MOENGAGE_ATTRIBUTES_ENABLED`
- `server/src/EventPump/Senders/AdjustSender.cs` — emit `partner_params` + `s2s_email` / `s2s_phone`, gated by `EP_ADJUST_ATTRIBUTES_ENABLED`
- `server/src/EventPump/Serialization/*` — new source-gen JSON contexts for attribute payloads (AOT)
- `server/tests/EventPump.Tests/*` — validation (allowlist + normalization + rejection codes), upsert + partial-merge + hash, sync-enqueue on hash mismatch, per-sender payload correctness with flag ON/OFF, `ep_attributes_synced` manual-use rejection, DSR endpoint idempotency

**SDK — Flutter (`sdks/flutter`):**
- `lib/src/client.dart` — new `setUserAttributes(Map<String, Object?>)` method; **`track()` signature changes to named `properties:`** (breaking change, v1.1 release note); `screen()` gets same treatment for consistency
- `lib/src/allowlist.dart` (new) — hardcoded six-name mirror
- `example/` — update to new named-arg call sites
- `test/*` — SDK unit tests for validation, no-op-without-user, partial upsert; migrate existing track/screen tests to named-arg

**SDK — Web (`sdks/web`):**
- `src/index.ts` — new `setUserAttributes(attributes)` method + type export
- `src/allowlist.ts` (new) — hardcoded six-name mirror
- `test/*` — same coverage as Flutter (minus track rename — web unchanged)
- Report new gzipped size against ≤ 8 KB budget (SPEC §14)

**Config / deploy:**
- `deploy/tracking-plan.example.json` — add `attributes` block + `ep_attributes_synced` reserved event
- `deploy/.env.example` — new four env vars with defaults per §6.4
- `deploy/mock-destinations.mjs` — mock endpoints for MoEngage `type:"customer"`
- `deploy/smoke.sh` — extend to assert attributes propagate from a web batch and a flutter batch, cover MoEngage customer sync flow, cover DSR endpoint

---

## 11. Approval + remaining go/no-go

**Approved (design):** D1–D15, N1 (DB-only DSR), N2 (retention deferred). All notes from the reviewer folded in.

**Remaining green light:** apply §8 SPEC.md diff to disk as-is (or with edits). Once applied, Step 2 (implementation) begins per §10's file list.
