#!/usr/bin/env bash
# Builds gecko-client (ColituGecko sidecar) for linux-x64 and linux-arm64 from the private
# colitu-gecko repository, into gecko-dosyalari/linux-<arch>/gecko-client (not committed).
#
#   scripts/build-gecko-sidecar.sh [--source <clone-or-url>] [--commit <hash>] [--arch x64|arm64|both]
#
# The commit defaults to the pin in scripts/gecko-sidecar.json; without a pin pass --commit (a test
# build: the SHA-256 is printed, nothing is checked). With a pin, the rebuilt binaries must match the
# pinned SHA-256, or nothing is copied. The source is a clone of the private repository (it needs
# access; the default is the pinned repo URL). Go must be on PATH (or GO=/path/to/go); CGO is off, so
# no C toolchain is needed and cross-compiling works from any host.
set -euo pipefail

ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
JSON="$ROOT/scripts/gecko-sidecar.json"
GO="${GO:-go}"
SOURCE="$(sed -n 's/.*"repo": *"\([^"]*\)".*/\1/p' "$JSON" | head -n1)"
COMMIT="$(sed -n 's/.*"commit": *"\([0-9a-f]*\)".*/\1/p' "$JSON" | head -n1)"
PINNED=1
[[ -n "$COMMIT" ]] || PINNED=0
ARCHES="x64 arm64"
while [[ $# -gt 0 ]]; do
  case "$1" in
    --source) SOURCE="${2:?}"; shift 2 ;;
    --commit) COMMIT="${2:?}"; PINNED=0; shift 2 ;;
    --arch) case "${2:?}" in x64|arm64) ARCHES="$2" ;; both) ARCHES="x64 arm64" ;; *) echo "bad --arch" >&2; exit 2 ;; esac; shift 2 ;;
    *) echo "unknown argument $1" >&2; exit 2 ;;
  esac
done
[[ -n "$COMMIT" ]] || { echo "No commit pinned in scripts/gecko-sidecar.json: pass --commit <hash> for a test build." >&2; exit 2; }
command -v "$GO" >/dev/null 2>&1 || { echo "Go not found (set GO=)." >&2; exit 2; }

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
git -c core.autocrlf=false -c core.eol=lf clone --quiet --no-checkout "$SOURCE" "$WORK/src" \
  || { echo "git clone $SOURCE failed (the repository is private: it needs access)." >&2; exit 1; }
git -C "$WORK/src" -c advice.detachedHead=false checkout --quiet "$COMMIT"
HEAD_HASH="$(git -C "$WORK/src" rev-parse HEAD)"
[[ "$HEAD_HASH" == "$COMMIT"* || "$COMMIT" == "$HEAD_HASH"* ]] || { echo "checkout is at $HEAD_HASH, not $COMMIT" >&2; exit 1; }
echo "colitu-gecko ${HEAD_HASH:0:12} with $("$GO" version)"

declare -A GOARCH=([x64]=amd64 [arm64]=arm64)
BAD=0
for arch in $ARCHES; do
  out="$WORK/out/linux-$arch/gecko-client"
  mkdir -p "$(dirname "$out")"
  (cd "$WORK/src" && CGO_ENABLED=0 GOOS=linux GOARCH="${GOARCH[$arch]}" "$GO" build -trimpath -buildvcs=false \
    -ldflags '-s -w -buildid=' -o "$out" ./cmd/gecko-client)
  sum="$(sha256sum "$out" | cut -d ' ' -f 1)"
  echo "$sum  linux-$arch/gecko-client"
  if [[ "$PINNED" == 1 ]]; then
    want="$(sed -n "s/.*\"linux-$arch\": *\"\([0-9a-f]*\)\".*/\1/p" "$JSON" | head -n1)"
    [[ "$sum" == "$want" ]] || { echo "linux-$arch: built $sum, scripts/gecko-sidecar.json pins '$want'" >&2; BAD=1; }
  fi
done
[[ "$BAD" == 0 ]] || { echo "The rebuilt gecko-client does not match scripts/gecko-sidecar.json (nothing copied)." >&2; exit 1; }
for arch in $ARCHES; do
  mkdir -p "$ROOT/gecko-dosyalari/linux-$arch"
  install -m 0755 "$WORK/out/linux-$arch/gecko-client" "$ROOT/gecko-dosyalari/linux-$arch/gecko-client"
done
echo "gecko-client is in gecko-dosyalari/ ($([[ "$PINNED" == 1 ]] && echo "matches the pin" || echo "test build, not pinned"))."
