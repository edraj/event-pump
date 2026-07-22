#!/usr/bin/env bash
# Builds the eventpump RPM: source tarball + vendored NuGet cache + prebuilt
# events UI + rpmbuild.
#
# The vendored cache makes %build fully offline (mock-friendly). It is tied to
# the .NET SDK feature band that creates it — build the vendor tarball and the
# RPM with the same SDK (this script does both on the same host, so that holds
# automatically). ILCompiler targets glibc 2.34 (RHEL 9), so a Fedora-built
# package installs on EL9+; RPM's ELF dependency generator enforces the floor.
#
# The events UI is built here rather than in %build for the same reason: npm
# has no offline story comparable to the NuGet cache, so the bundle ships into
# the SRPM prebuilt. Set EP_UI_DIST to a directory of already-built UI assets
# (CI does this, from the `ui` release job) to skip the local npm build.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
SPEC="$ROOT/deploy/rpm/eventpump.spec"
VERSION="$(rpmspec -q --srpm --qf '%{version}' "$SPEC")"
TOP="${RPM_TOPDIR:-$ROOT/build/rpm}"

mkdir -p "$TOP/SOURCES"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

echo "== source tarball (eventpump-$VERSION.tar.gz)"
tar czf "$TOP/SOURCES/eventpump-$VERSION.tar.gz" \
  -C "$ROOT" \
  --transform "s,^,eventpump-$VERSION/," \
  --exclude='*/bin' --exclude='*/obj' \
  server/src server/migrations server/sql \
  deploy/systemd deploy/.env.example deploy/tracking-plan.example.json \
  deploy/nginx-ui.conf.example \
  README.md SPEC.md LICENSE

echo "== events UI (eventpump-ui-$VERSION.tar.gz)"
UI_DIST="${EP_UI_DIST:-}"
if [ -n "$UI_DIST" ]; then
  echo "   using prebuilt assets from $UI_DIST"
else
  if ! command -v npm >/dev/null 2>&1; then
    echo "error: npm not found — install Node 20+, or point EP_UI_DIST at a" >&2
    echo "       prebuilt UI directory (ui/dist/client, or an unpacked" >&2
    echo "       eventpump-ui-*.tar.gz from a release)." >&2
    exit 1
  fi
  (cd "$ROOT/ui" && npm ci --no-audit --no-fund && npm run build)
  # Routify emits the SPA under dist/client; tolerate a plain dist/ layout.
  UI_DIST="$ROOT/ui/dist/client"
  [ -f "$UI_DIST/index.html" ] || UI_DIST="$ROOT/ui/dist"
fi
if [ ! -f "$UI_DIST/index.html" ]; then
  echo "error: no index.html under $UI_DIST — the UI build produced nothing" >&2
  exit 1
fi
mkdir -p "$TMP/ui-dist"
cp -r "$UI_DIST/." "$TMP/ui-dist/"
tar czf "$TOP/SOURCES/eventpump-ui-$VERSION.tar.gz" -C "$TMP" ui-dist

echo "== vendoring NuGet packages (linux-x64)"
VENDOR="$TMP/vendor"
mkdir -p "$VENDOR"
# A scratch publish (not plain restore): the NativeAOT runtime pack and
# ILCompiler are only pulled during publish-time restore.
NUGET_PACKAGES="$VENDOR/nuget-vendor" \
  dotnet publish "$ROOT/server/src/EventPump" -c Release -r linux-x64 \
    -o "$VENDOR/publish-scratch" --nologo -v q >/dev/null
# The .nupkg zip archives duplicate the extracted package trees and are the
# incompressible half of the tarball; offline restore reads only the extracted
# layout (verified: full offline AOT publish from a stripped cache). ~-51%.
# NOTE: the runtime-pack payloads must stay — the pre-ILC build stage copies
# them (MSB3030 if pruned) even though they never reach the final binary.
find "$VENDOR/nuget-vendor" -name '*.nupkg' -delete
tar czf "$TOP/SOURCES/eventpump-nuget-vendor-$VERSION.tar.gz" -C "$VENDOR" nuget-vendor

cp "$ROOT/deploy/rpm/eventpump.sysusers" "$TOP/SOURCES/"

echo "== rpmbuild"
rpmbuild --define "_topdir $TOP" -ba "$SPEC"

echo
echo "== artifacts"
find "$TOP/RPMS" "$TOP/SRPMS" -name '*.rpm' -newer "$SPEC" 2>/dev/null || find "$TOP/RPMS" "$TOP/SRPMS" -name '*.rpm'
