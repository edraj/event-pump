#!/usr/bin/env bash
# Builds the eventpump RPM: source tarball + vendored NuGet cache + rpmbuild.
#
# The vendored cache makes %build fully offline (mock-friendly). It is tied to
# the .NET SDK feature band that creates it — build the vendor tarball and the
# RPM with the same SDK (this script does both on the same host, so that holds
# automatically). ILCompiler targets glibc 2.34 (RHEL 9), so a Fedora-built
# package installs on EL9+; RPM's ELF dependency generator enforces the floor.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
SPEC="$ROOT/deploy/rpm/eventpump.spec"
VERSION="$(rpmspec -q --srpm --qf '%{version}' "$SPEC")"
TOP="${RPM_TOPDIR:-$ROOT/build/rpm}"

mkdir -p "$TOP/SOURCES"

echo "== source tarball (eventpump-$VERSION.tar.gz)"
tar czf "$TOP/SOURCES/eventpump-$VERSION.tar.gz" \
  -C "$ROOT" \
  --transform "s,^,eventpump-$VERSION/," \
  --exclude='*/bin' --exclude='*/obj' \
  server/src server/migrations server/sql \
  deploy/systemd deploy/.env.example deploy/tracking-plan.example.json \
  README.md SPEC.md LICENSE

echo "== vendoring NuGet packages (linux-x64)"
VENDOR="$(mktemp -d)"
trap 'rm -rf "$VENDOR"' EXIT
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
