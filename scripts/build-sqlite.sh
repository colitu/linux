#!/usr/bin/env bash
# Builds libe_sqlite3.so from the sqlite.org source on an old glibc, and replaces the one
# dotnet publish copied from NuGet: that one needs glibc 2.38, every other library 2.28 at most,
# so the app could not start on Rocky/RHEL 8 and 9, Ubuntu 22.04 or Debian 12.
#
#   scripts/build-sqlite.sh <path of libe_sqlite3.so to replace>
#
# Runs inside rockylinux/rockylinux:8 (release-linux.yml). Same SQLite version and options as the
# package it replaces.
set -euo pipefail

OUT="${1:?path of libe_sqlite3.so}"
VERSION=3530400
YEAR=2026
SHA256=1e71ddf93849c6a6ecf58b827c0692073d2dd7ee40196158068f7b29f422e87d
MAX_GLIBC=2.28

dnf install -y -q gcc glibc-devel unzip binutils >/dev/null
WORK="$(mktemp -d)"
curl -fsSL -o "$WORK/sqlite.zip" "https://www.sqlite.org/$YEAR/sqlite-amalgamation-$VERSION.zip"
echo "$SHA256  $WORK/sqlite.zip" | sha256sum -c -
unzip -q "$WORK/sqlite.zip" -d "$WORK"

gcc -O2 -fPIC -shared -s -Wl,-soname,libe_sqlite3.so -Wl,-z,noexecstack \
  -DNDEBUG -DSQLITE_THREADSAFE=1 -DSQLITE_USE_URI=1 -DSQLITE_TEMP_STORE=1 \
  -DSQLITE_ENABLE_COLUMN_METADATA -DSQLITE_ENABLE_FTS5 -DSQLITE_ENABLE_RTREE \
  -o "$WORK/libe_sqlite3.so" "$WORK/sqlite-amalgamation-$VERSION/sqlite3.c" -lm -lpthread -ldl

NEEDED="$(objdump -T "$WORK/libe_sqlite3.so" | grep -oE 'GLIBC_[0-9.]+' | sed 's/GLIBC_//' | sort -uV | tail -n1)"
if [[ "$(printf '%s\n%s\n' "$NEEDED" "$MAX_GLIBC" | sort -V | tail -n1)" != "$MAX_GLIBC" ]]; then
  echo "libe_sqlite3.so needs glibc $NEEDED, the limit is $MAX_GLIBC" >&2
  exit 1
fi
install -m 0755 "$WORK/libe_sqlite3.so" "$OUT"
echo "[+] $OUT: SQLite $VERSION, glibc $NEEDED"
