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
Version:        0.1.2
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
install -m0640 deploy/tracking-plan.example.json %{buildroot}%{_sysconfdir}/eventpump/tracking-plan.json
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
%config(noreplace) %attr(0640,root,eventpump) %{_sysconfdir}/eventpump/tracking-plan.json
%dir %{_datadir}/eventpump
%{_datadir}/eventpump/migrations/
%{_datadir}/eventpump/sql/

%files ui
%license LICENSE
%{_datadir}/eventpump/ui/
%{_datadir}/eventpump/nginx/

%changelog
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
