# Event Pump — Native AOT server package for EL9+ and Fedora.
#
# The binary is self-contained (no .NET runtime dependency at run time) and
# ILCompiler links against a conservative glibc baseline (2.34 = RHEL 9), so
# a package built on Fedora installs and runs on EL9+. RPM's automatic ELF
# dependency generator records the exact glibc/openssl soname requirements,
# so an incompatible target fails at dnf time, never at run time.

# Native AOT publish strips symbols (StripSymbols=true) — no debuginfo to extract.
%global debug_package %{nil}

Name:           eventpump
Version:        0.8.2
Release:        1%{?dist}
Summary:        Event Pump first-party event pipeline (ingestion API + delivery worker)
License:        AGPL-3.0-only
URL:            https://github.com/edraj/event-pump

# Created by deploy/rpm/build-rpm.sh:
#   Source0: server sources + deploy files + docs
#   Source1: vendored NuGet package cache (offline restore; RID linux-x64)
#   Source3: prebuilt events UI bundle (npm has no offline-restore story, so
#            the SPA is built by build-rpm.sh and vendored already-built)
Source0:        %{name}-%{version}.tar.gz
Source1:        %{name}-nuget-vendor-%{version}.tar.gz
Source2:        eventpump.sysusers
Source3:        %{name}-ui-%{version}.tar.gz

# Vendored NuGet cache is RID-specific (linux-x64).
ExclusiveArch:  x86_64

BuildRequires:  dotnet-sdk-10.0
# Native AOT link step prerequisites
BuildRequires:  clang
BuildRequires:  zlib-devel
BuildRequires:  glibc-devel
BuildRequires:  binutils
BuildRequires:  systemd-rpm-macros
%{?sysusers_requires_compat}

# Native AOT dlopens OpenSSL at run time (it is not in the ELF NEEDED list,
# so the automatic dependency generator cannot see it). Npgsql TLS needs it.
Requires:       openssl-libs

%description
Event Pump ingests events from web/mobile clients and backend services into a
PostgreSQL outbox (inside the platform's business database) and delivers them
to downstream destinations (GA4 Measurement Protocol, Amplitude, MoEngage,
Adjust S2S, Meta CAPI) over their server-to-server APIs.

One self-contained Native AOT binary with three subcommands: `eventpump api`
(ingestion endpoints), `eventpump worker` (delivery pipelines + partition
maintenance), and `eventpump migrate` (plain-SQL schema migrations). The two
services run independently restartable under systemd.

%package ui
Summary:        Static events explorer UI for Event Pump
BuildArch:      noarch
Requires:       %{name} = %{version}-%{release}

%description ui
Prebuilt single-page events explorer: filter and inspect recent events with
per-destination delivery states, and drill into a session's identity registry
row. Static assets only — install them under %{_datadir}/eventpump/ui and
point a web server at that directory.

The UI reads the read-only query endpoints on the API's internal listener
(EP_INTERNAL_LISTEN, 127.0.0.1:8081 by default), which it expects to reach
same-origin under /internal/v1/query/. The web server is therefore the sole
public doorway and the only authentication gate; a ready-to-adapt nginx vhost
ships at %{_datadir}/eventpump/nginx/eventpump-ui.conf.example.

%prep
%setup -q
# unpack the vendored NuGet cache into ./nuget-vendor
%setup -q -T -D -a 1
# unpack the prebuilt events UI into ./ui-dist
%setup -q -T -D -a 3

%build
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
export DOTNET_NOLOGO=1
export NUGET_PACKAGES=$PWD/nuget-vendor
# Fully offline restore: no remote sources; everything resolves from the
# vendored cache. NuGet walks up from the project dir and finds this file.
cat > nuget.config <<'NUGETCONF'
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
  </packageSources>
</configuration>
NUGETCONF
dotnet publish server/src/EventPump -c Release -r linux-x64 -o publish --nologo

%install
install -D -m0755 publish/eventpump %{buildroot}%{_bindir}/eventpump
install -D -m0644 deploy/systemd/eventpump-api.service %{buildroot}%{_unitdir}/eventpump-api.service
install -D -m0644 deploy/systemd/eventpump-worker.service %{buildroot}%{_unitdir}/eventpump-worker.service
install -D -m0644 %{SOURCE2} %{buildroot}%{_sysusersdir}/eventpump.conf
install -d %{buildroot}%{_sysconfdir}/eventpump
install -m0640 deploy/.env.example %{buildroot}%{_sysconfdir}/eventpump/eventpump.env
# Tenant files (SPEC v1.2 §13.2). The directory is created empty and owned by
# the package; the example is documentation under %{_datadir} and deliberately
# NOT installed here — EP_TENANTS_DIR loads every *.json/*.jsonc it finds, so
# an example dropped in this directory would boot as a real tenant.
install -d -m0750 %{buildroot}%{_sysconfdir}/eventpump/tenants
install -D -m0644 deploy/tenants/zainmart.example.jsonc \
  %{buildroot}%{_datadir}/eventpump/tenants/zainmart.example.jsonc
install -D -m0644 deploy/tenants/README.md \
  %{buildroot}%{_datadir}/eventpump/tenants/README.md
install -d %{buildroot}%{_datadir}/eventpump/migrations
install -m0644 server/migrations/*.sql %{buildroot}%{_datadir}/eventpump/migrations/
install -D -m0644 server/sql/producer_contract.sql %{buildroot}%{_datadir}/eventpump/sql/producer_contract.sql

# events UI: prebuilt static assets, served by nginx from this directory
install -d %{buildroot}%{_datadir}/eventpump/ui
cp -a ui-dist/. %{buildroot}%{_datadir}/eventpump/ui/
find %{buildroot}%{_datadir}/eventpump/ui -type d -exec chmod 0755 {} +
find %{buildroot}%{_datadir}/eventpump/ui -type f -exec chmod 0644 {} +
install -D -m0644 deploy/nginx-ui.conf.example \
  %{buildroot}%{_datadir}/eventpump/nginx/eventpump-ui.conf.example

%pre
%sysusers_create_compat %{SOURCE2}

%post
%systemd_post eventpump-api.service eventpump-worker.service

%posttrans
# The release that introduced tenant files dropped
# /etc/eventpump/tracking-plan.json from the package
# (tenant files replace it). rpm moves an operator-edited config file it no
# longer owns aside as .rpmsave — but the back-compat single-tenant path is
# still supported and eventpump.env still points EP_TRACKING_PLAN at that
# exact path, so losing the file would stop the api from booting. Put it back.
# This runs at the end of the transaction, after the old package's files are
# removed. Nothing to do on a fresh install (no .rpmsave exists).
if [ ! -f %{_sysconfdir}/eventpump/tracking-plan.json ] \
   && [ -f %{_sysconfdir}/eventpump/tracking-plan.json.rpmsave ]; then
    mv %{_sysconfdir}/eventpump/tracking-plan.json.rpmsave \
       %{_sysconfdir}/eventpump/tracking-plan.json
fi

%preun
%systemd_preun eventpump-api.service eventpump-worker.service

%postun
%systemd_postun_with_restart eventpump-api.service eventpump-worker.service

%files
%license LICENSE
%doc README.md SPEC.md
%{_bindir}/eventpump
%{_unitdir}/eventpump-api.service
%{_unitdir}/eventpump-worker.service
%{_sysusersdir}/eventpump.conf
%dir %attr(0750,root,eventpump) %{_sysconfdir}/eventpump
%config(noreplace) %attr(0640,root,eventpump) %{_sysconfdir}/eventpump/eventpump.env
%dir %attr(0750,root,eventpump) %{_sysconfdir}/eventpump/tenants
%dir %{_datadir}/eventpump
%{_datadir}/eventpump/migrations/
%{_datadir}/eventpump/sql/
%{_datadir}/eventpump/tenants/

%files ui
%license LICENSE
%{_datadir}/eventpump/ui/
%{_datadir}/eventpump/nginx/

%changelog
* Thu Sep 10 2026 Kefah Issa <kefah.issa@gmail.com> - 0.8.2-1
- Adjust S2S traffic can now be routed to Adjust's sandbox per tenant.
  Adjust files an event under production whenever the payload omits the
  environment field, so a UAT deployment had no way to keep its test events
  out of live attribution -- every one of them credited a real campaign.
  Tenant files take an optional destination_config.adjust.environment
  ("sandbox" or "production"), mirrored as EP_ADJUST_ENVIRONMENT on the
  legacy single-tenant path. Leaving it unset is what every existing
  deployment already has and keeps today's behaviour exactly (#48).
- An unrecognised environment stops the boot rather than reaching Adjust.
  Adjust answers 200 whether or not it understood the value and reports
  nothing about which scope it filed the event under, so "sanbox", "uat" or
  a stray space would have resolved silently to production -- the one
  failure the setting exists to prevent. Both config surfaces are checked
  and the message names what was written: "tenant 'zainmart.jsonc':
  adjust.environment must be sandbox or production, got 'sanbox'". Case is
  normalised rather than refused (Adjust reads the value literally, so
  Sandbox has to go out as sandbox), and empty or whitespace-only means
  unset, the way null and "" already do elsewhere in a tenant file (#48).
- The DSR erasure is scoped by the same setting. gdpr_forget_device was
  built from the endpoint and app_token alone, so a sandbox tenant's forget
  landed on the production scope -- where Adjust answers 200 for a device
  it has never seen, settling the row delivered with nothing forgotten, and
  where a UAT handset's real production install is what the call would
  erase instead. A tenant that sets environment now sends it on the erasure
  call too; one that leaves it unset sends the URL it always sent (#48).
- NO MIGRATIONS. Config-only release; `eventpump migrate` has nothing new
  to apply.

* Tue Sep 08 2026 Kefah Issa <kefah.issa@gmail.com> - 0.8.1-1
- DSR erasure now fans out to the destinations, not just the local tables.
  POST /internal/v1/erasure/{app_id}/{user_id} deletes the user_attributes
  and identity_registry rows as before, and additionally queues a delete at
  every destination that has an erasure API, under the handle that
  destination knows the person by rather than our own user_id. An
  /attributes variant erases only the attribute sync. Deliveries already
  queued for that person are cancelled first, since one landing after the
  downstream delete rebuilds exactly what was erased. Every request writes
  an erasure_audit row -- who, under which handles, which destinations, and
  what each one did with it -- readable over
  GET /internal/v1/erasure/{app_id}/{user_id}/audit and deliberately exempt
  from retention, because a complaint about an ignored erasure can arrive
  long after the outbox partition it came from was dropped. MoEngage,
  Adjust and Amplitude are covered; GA4 is not (its deletion API is
  OAuth-authenticated, which this service holds no credential for) and
  records skipped: ga4_oauth_not_configured so the gap is visible per
  request rather than silent (#42, #45).
- Events from backend producers now reach the destinations. A server-origin
  event carries a user_id but no session_key -- a backend is not inside a
  client session -- so the worker's identity join missed and every
  identity-gated destination skipped it. The worker now falls back to
  resolving the PERSON: their most recently active identity_registry row.
  EXPECT DELIVERY VOLUME AT GA4, AMPLITUDE, MOENGAGE AND ADJUST TO RISE on
  upgrade, by however many server-origin events a deployment emits; they
  were being dropped before. Set EP_IDENTITY_USER_FALLBACK=false to keep
  the old session-key-only behaviour. A person-resolved row contributes
  handles only -- its session, device context and IP are dropped, because
  the event did not happen there -- and Adjust additionally refuses an ADID
  older than adjust.max_identity_age_days (30 by default, 0 = no limit),
  since an ADID names an install and carries its attribution, so a fresh
  conversion fired at a long-abandoned one credits a campaign that did not
  earn it (#44).
- Rejected events are now visible. A batch is validated per event, so a
  partial failure has always been reported in the response body and not the
  status code; what was missing was anything an operator could alert on.
  Every rejection now increments
  events_rejected_total{app_id,origin,endpoint,reason} and writes a warning
  naming the index, event id, event name and reason -- including the
  whole-batch refusals (malformed_json, missing_events_array,
  batch_too_large), which return before per-event validation runs and so
  reported nothing at all. A tenant whose SDK ships truncated JSON was
  dropping 100% of its traffic against a rejection count of zero. The
  status code is unchanged: a wholly rejected batch still answers 200,
  because both SDKs ack only on 2xx and would otherwise re-upload a doomed
  batch until the 24h give-up, delaying every valid event behind it (#43).
- ACTION REQUIRED: an enabled destination with missing credentials now
  stops the boot instead of failing at delivery time. The message names the
  tenant, the destination and the field -- "tenant 'zainmart': ga4 enabled
  but api_secret is empty". Required per destination is what its sender
  actually reads: GA4 api_secret plus one of measurement_id /
  firebase_app_id, Amplitude api_key, MoEngage moengage_app_id + api_key,
  Adjust app_token, Meta pixel_id + access_token. Both config paths are
  checked, so a legacy EP_META_ENABLED=true with an empty pixel_id, or a
  tenant file with a REPLACE_ME left blank, will not start after this
  upgrade -- check every enabled destination before restarting. Disabled
  destinations are not validated, so scaffolding one with empty credentials
  and switching it on later still works. A typo'd key previously booted
  clean and only surfaced hours later, with the tenant's outbox filling
  with failed rows behind circuit-breaker backoff (#46).
- ACTION REQUIRED: EP_* booleans are parsed one way everywhere, and two
  kinds of value change behaviour on this upgrade. A value outside
  true/false, 1/0, yes/no, on/off now STOPS THE BOOT rather than resolving
  to a default -- this catches typos, but it also catches a trailing
  comment left on a value in an env_file. And the reads this replaced were
  case-sensitive and inconsistent with each other (a default-off flag read
  == "true", a default-on flag read != "false"), so a spelling the old
  build and this one disagree about FLIPS MEANING SILENTLY:
  EP_GA4_ENABLED=True was off and is now on, so a destination dark since
  install starts sending live traffic, and
  EP_MOENGAGE_ATTRIBUTES_ENABLED=0 was on and is now off. Boot writes one
  stderr line per affected variable naming both readings; re-spell the
  value to settle it. Grep your env files for boolean values that are not
  lowercase true or false before upgrading (#42).
- MIGRATIONS: run `eventpump migrate` before starting the new binaries.
  0011 and 0012 add the erasure lookup index and the erasure_audit table;
  0013 widens the identity_registry index to serve the person lookup; 0014
  drops identity_registry_app_idx, redundant since 0009 made the primary
  key composite on (app_id, session_key). 0013 builds an index under a
  SHARE lock and 0014 drops one under ACCESS EXCLUSIVE, neither of which
  can be CONCURRENTLY inside a migration transaction -- brief at one
  identity row per session, but run it off-peak if identity_registry has
  grown to millions of rows.

* Thu Aug 27 2026 Kefah Issa <kefah.issa@gmail.com> - 0.8.0-1
- The events UI is now scoped to one tenant at a time, and says which. The
  query API has no app_id parameter -- the tenant's server-side
  internal_token is the selector (SPEC 9.3) -- so a single-mount UI could
  only show whichever tenant nginx happened to inject a token for, with
  nothing on the page naming it. Each tenant now gets its own proxy path,
  nginx injects that tenant's token there, and switching tenant is
  switching prefix. The selection survives a reload and rides on copied
  links as ?tenant= (#37).
- New endpoint GET /internal/v1/query/tenant: the tenant the bearer
  resolved to, its plan's event names and user attributes, its query
  window, and the destinations it runs. It carries no credentials. The UI
  uses it to name the tenant and to drive the filter pickers from the
  tenant's own plan rather than free-text boxes that answer a typo with an
  empty page (SPEC 13.2).
- The from/to filters now leave the browser as instants. They were sent as
  the naive local time the picker yields, which the server reads in its own
  zone, so a browser east of UTC asked for a window offset by its own
  offset and lost that much of it to the query-window clamp with nothing on
  screen to say so (#37).
- ACTION REQUIRED for multi-tenant deployments only. A single-tenant
  deployment needs no change: with no window.EP_TENANTS declared the UI
  keeps using one mount under the UI base, exactly as 0.7.x did, and its
  URLs are byte-identical. To serve more than one tenant, declare the list
  to the page and give each entry a location that injects that tenant's
  token -- see deploy/nginx-ui.conf.example:
      sub_filter '<head>' '<head><script>window.EP_TENANTS=[...]</script>';
      location /t/kefahapp/internal/v1/query/ {
          proxy_pass http://127.0.0.1:8081/internal/v1/query/;
          proxy_set_header Authorization "Bearer <that tenant's token>";
      }
  Every mount must stay at or below the path auth_basic challenges on, for
  the RFC 7617 2.2 reason described in the 0.7.0 entry. Anyone past
  auth_basic can read every tenant listed on the vhost; split them across
  vhosts with their own htpasswd files if that is too much.
- Also: a tenant switch no longer leaves the previous tenant's rows under
  the new tenant's name while the new query is in flight; a 404 from the
  identity lookup is reported as a missing session rather than a missing
  nginx mount; and the session page waits for the tenant's plan before
  reporting that it declares no attributes (#37).

* Thu Aug 27 2026 Kefah Issa <kefah.issa@gmail.com> - 0.7.2-1
- The two misconfiguration diagnostics added in 0.7.1 no longer fire on
  deployments that are configured correctly. Any failure to read a response
  body was reported as "the query API returned a non-JSON body" and blamed an
  unproxied path, but fetch resolves once the headers arrive, so a connection
  dropped mid-body or an aborted navigation landed there too; only a genuine
  parse failure does now. The Basic-challenge message insisted the browser had
  never been challenged on that path, which is also what nginx returns when it
  rejects credentials it was given and did not like (a rotated htpasswd, say),
  so it now names re-authentication as the first cause to rule out (#38).
- No behaviour change for a working deployment: only error text differs.
  Upgrading from 0.7.1 needs no action; upgrading from 0.6.0 or earlier still
  needs the nginx change described in the 0.7.0 entry.

* Mon Aug 24 2026 Kefah Issa <kefah.issa@gmail.com> - 0.7.1-1
- The events UI now names the cause when its query calls are not proxied,
  instead of failing cryptically. 0.7.0 moved those calls under the UI
  prefix but cannot edit an operator's nginx; until they do, the UI showed
  either a bare "401 Unauthorized" (indistinguishable from a wrong
  internal_token) or "Unexpected token <" (the SPA's own index.html, served
  by try_files because nothing proxied the path). Both messages now name the
  cause and the exact path nginx should be proxying for that deployment
  (#34).
- No behaviour change for a working deployment: only error text differs.
  Upgrading from 0.7.0 needs no action; upgrading from 0.6.0 or earlier
  still needs the nginx change described in the 0.7.0 entry.
* Mon Aug 24 2026 Kefah Issa <kefah.issa@gmail.com> - 0.7.0-1
- Fix the events UI failing every query under a subpath deployment. The
  query API was documented to sit at a sibling path (/ep/internal/...) of
  the UI mount (/ep/ui/); browsers cache Basic credentials per directory
  prefix (RFC 7617 2.2) and attach them only at or below the path they were
  challenged on, so nginx answered 401 and fetch() handed that to the app
  rather than prompting. The UI loaded and nothing ever returned data. The
  query base now follows the UI base, keeping the calls inside the
  authenticated scope (#31).
- ACTION REQUIRED for subpath deployments only. Root deployments are
  unaffected and their URLs are byte-identical. Under a subpath: drop
  window.EP_QUERY_BASE from the sub_filter and move the query proxy under
  the UI prefix -- see the worked example in deploy/nginx-ui.conf.example:
      location /ep/ui/internal/v1/query/ {
          proxy_pass http://127.0.0.1:8081/internal/v1/query/;
      }
  A deployment that must keep the two apart (a vhost with no auth_basic)
  can still set window.EP_QUERY_BASE explicitly; an empty string now means
  "the root" rather than being ignored.
* Mon Aug 24 2026 Kefah Issa <kefah.issa@gmail.com> - 0.6.0-1
- The `?tenant_api_key=` query form is now accepted on POST /v1/events only.
  It exists for the sendBeacon page-unload flush, which cannot set headers;
  /v1/identity and /v1/errors are always sent with fetch by both SDKs, so
  accepting it there only widened where the key gets written down (access
  logs, Referer, proxy caches). Header auth is unchanged everywhere (#25).
- /internal/v1/query/events answers 400 `invalid_uuid` for an unparseable
  `anonymous_id` or `session_key` instead of coercing it to the nil UUID and
  rendering "no events" — indistinguishable from a session that genuinely
  has none. The events UI now shows which filter was rejected (#27).
- Do not queue a second MoEngage attribute sync while one is still
  undelivered: `moengage_synced_hash` only advances on a successful
  delivery, so a form saving field by field queued one job per save. A
  queued job counts as covering a later call only when it already carries
  that call's moengage_customer_id — the one value the sender cannot
  re-read at delivery time (#26).
- Documented: `first_seen_at` is accepted and deliberately ignored on
  /v1/identity; the server records its own first sighting (#28). Rate
  limiting is per (app_id, caller address), not per API key, and
  EP_TRUSTED_PROXIES gates both that bucket and the stored client_ip (#25).
- Ignore local dev config and CI scratch dirs (#24).
* Mon Aug 24 2026 Kefah Issa <kefah.issa@gmail.com> - 0.5.0-1
- Forward the user agent the server observed rather than the one the client
  declared, for GA4, Adjust and Meta CAPI. A non-browser observed agent (a
  native SDK's own HTTP client) still falls back to the app-declared one (#23).
- Make user_agent_observed server-owned unconditionally: a request that sent
  no User-Agent header could previously plant a forged value in the identity
  registry and have it shipped to destinations in preference to the declared
  user_agent (#29).
- Security gate: run all three scanners every time. The runner's default
  `bash -e` aborted the step on the first non-zero exit, so only the first
  gate to find anything was ever reported (#29).
- Secrets gate: allowlist the placeholder credentials themselves instead of
  exempting deploy/smoke.sh and deploy/.env.example whole (#29).
- Test dependency: Testcontainers.PostgreSql 4.13.0 -> 4.14.0, clearing an
  SSH.NET advisory (#22).
* Sun Aug 23 2026 Kefah Issa <kefah.issa@gmail.com> - 0.4.0-1
- Reject a tracking plan that mislabels first_visit (#15).
- Never send a delivery past its claim lease — stops duplicate sends to
  destinations that do not de-duplicate (#16).
- Retry a missing identity within a grace window before skipping (#17).
- Rate-limit by the visitor's IP, not the shared API key; trust X-Real-IP only
  from EP_TRUSTED_PROXIES (#18).
- New config: EP_IDENTITY_GRACE_S (default 300s), EP_TRUSTED_PROXIES
  (default 127.0.0.1/32,::1/128 — set to your reverse proxy).
* Wed Aug 19 2026 Kefah Issa <kefah.issa@gmail.com> - 0.3.0-1
- Multi-tenancy (SPEC v1.2). One process, one shared PostgreSQL, many tenants:
  each gets a config file in EP_TENANTS_DIR carrying its own tracking plan,
  destination credentials, CORS origins, cookie domain and rate limits, and
  every table is keyed by app_id so no query, delivery or DSR erasure can cross
  a tenant boundary. Senders and worker pipelines are per (tenant, destination).
- ACTION REQUIRED ON UPGRADE. EP_CLIENT_TOKENS is removed and the api refuses
  to start while it is still set, rather than silently filing every tenant's
  traffic under one app_id and splitting error_reports aggregation mid-history.
  Single-tenant deployments set EP_TENANT_API_KEY plus EP_LEGACY_APP_ID and
  keep working unchanged; multi-app_id deployments move to EP_TENANTS_DIR, one
  file per tenant (see %{_datadir}/eventpump/tenants/README.md).
- Two-tier auth. tenant_api_key authenticates SDK traffic on the public
  listener and ships inside app bundles; internal_token authenticates backend
  producers and DSR erasure on the internal listener and never leaves the
  server. The two must be distinct, and a value used as one tenant's client key
  may not be another's internal token — both are refused at boot, because a
  collision would let a bundled client key act as another tenant on
  /internal/v1/*. EP_INTERNAL_TOKEN stays optional on the legacy env path: unset
  still means the internal listener is closed, as it did before.
- emit_event() takes the tenant as its first argument. Its signature changed, so
  applying this release DROPS the EXECUTE grants held against the old one; every
  producing role needs its GRANT re-run in the same maintenance window as the
  migrate, or its calls fail inside their own transactions.
- Events record whether they came from web or app. The server decides once at
  ingestion and writes context.platform, preferring an SDK declaration, then the
  SDK name, then page/screen, then the User-Agent. Meta's action_source and
  MoEngage's platform follow it instead of being assumed.
- Adjust deliveries retry on HTTP 429 instead of being discarded as dead.
- Per-destination user identifiers, so a person is named correctly at GA4,
  Amplitude, MoEngage and Meta, and switching users on a shared device no longer
  carries the previous person's handles.
- Unauthenticated traffic is held to the configured EP_RATE_LIMIT rather than a
  hard-coded fallback, restoring the knob for operators who tightened it.

* Mon Jul 27 2026 Kefah Issa <kefah.issa@gmail.com> - 0.2.2-1
- The API now documents itself. Both listeners serve an OpenAPI 3.1 spec at
  /docs/openapi.json and a Swagger UI page at /docs/, covering every route the
  app maps: ingestion, the read-only query endpoints, the DSR deletion and the
  operational probes. The spec is embedded in the binary and compared against
  the live route table by the test suite, so it cannot drift from the code, and
  the four endpoints that enforce no token check say so rather than implying a
  gate that is not there. Paste a client or internal token into Authorize to
  try requests from the page.
- New EP_DOCS setting (both | internal | off; default both) decides which
  listeners answer. The spec names every /internal/* route, so a deployment
  whose public listener faces the internet can set EP_DOCS=internal to keep
  that inventory off it, or off to drop the page altogether.
- swagger-ui is fetched from unpkg, pinned by version and subresource-integrity
  hash; the fetch is the browser's, so /docs renders empty on an isolated
  network. Ingestion, delivery and query behaviour are unchanged.

* Sun Jul 26 2026 Kefah Issa <kefah.issa@gmail.com> - 0.2.1-1
- The events UI can be mounted somewhere other than a vhost root. Previously
  index.html asked for /assets/... and the client router matched the raw
  pathname, so under a subpath it found no route and rendered its 404 — leaving
  nowhere to put the UI on a host whose domain root belongs to the API.
  Set the base at build time with EP_UI_BASE, or relocate the prebuilt bundle
  this package ships by setting window.EP_UI_BASE and rewriting the asset URLs
  in the web server; see nginx-ui.conf.example under %%{_datadir}/eventpump/nginx
  for both. A root deployment is unchanged.

* Sun Jul 26 2026 Kefah Issa <kefah.issa@gmail.com> - 0.2.0-1
- SPEC v1.1: first-class person-scoped user attributes (§6.1). New
  user_attributes table keyed by user_id with six allowlisted attributes,
  server-side normalization (email lowercased, phone E.164, gender enum), an
  `attributes` block on POST /v1/identity, and DSR deletion via
  DELETE /internal/v1/user_attributes/{user_id} (§9.6).
- MoEngage type:"customer" sync via the reserved ep_attributes_synced event on
  the new moengage_customer destination, flowing through the normal
  outbox/retry/circuit-breaker path.
- Attribute-derived fields on GA4, Amplitude, Adjust and Meta payloads, each
  behind its own EP_<X>_ATTRIBUTES_ENABLED flag (MoEngage defaults ON, the
  rest OFF).
- Per-destination event/property rename map (§6.2). The property map is an
  allowlist: once declared for an event/destination, undeclared canonical keys
  are dropped from that payload. `meta_name` is retired in favour of
  destinations.meta.events.<x>.name and a plan still carrying it is rejected
  at boot.
- GA4 Phase 2 e-commerce: nested items[] built from canonical properties.
- Events UI surfaces each event's person-scoped email/phone, and the session
  view gains an attributes block.
- New deploy path: deploy/systemd-user/ runs api+worker as one
  `eventpump standalone` systemd --user unit under ~/eventpump, for single-VM
  EL9 hosts without root.
- BREAKING (Flutter SDK): track()/screen() take `properties:` as a named
  argument instead of an optional positional one. Web SDK is unchanged.

* Wed Jul 22 2026 Kefah Issa <kefah.issa@gmail.com> - 0.1.2-1
- New noarch subpackage eventpump-ui: the prebuilt events explorer installs to
  %%{_datadir}/eventpump/ui, with the nginx vhost example alongside it under
  %%{_datadir}/eventpump/nginx. The SPA is built by build-rpm.sh and vendored
  into the SRPM prebuilt (Source3), so %%build stays fully offline.

* Tue Jul 14 2026 Kefah Issa <kefah.issa@gmail.com> - 0.1.1-1
- Strip .nupkg archives from the vendored NuGet cache: SRPM roughly halves.
  Offline %%build verified against the stripped cache; runtime-pack payloads
  must remain (the pre-ILC build stage copies them).

* Mon Jul 13 2026 Kefah Issa <kefah.issa@gmail.com> - 0.1.0-1
- Initial package: self-contained Native AOT eventpump binary
  (api/worker/migrate), hardened systemd units, sysusers service account,
  config in /etc/eventpump (noreplace), migrations and the SQL producer
  contract under /usr/share/eventpump.
