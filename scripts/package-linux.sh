#!/usr/bin/env bash
# Packages a published Colitu VPN for Linux build as .deb, .rpm and .tar.gz.
#
#   scripts/package-linux.sh <version> <x64|arm64> <publish-dir> <out-dir>
#
# <publish-dir> is the output of
#   dotnet publish src/ColituVPN -c Release -r linux-<arch> -p:SelfContained=true -p:Version=<version>
# The Xray and sing-box cores come from their official GitHub releases, pinned by
# version and SHA-256 (archive, Xray's .dgst file, and every file taken from it):
# they run as root in TUN mode, so any mismatch stops the build. The geo rule files
# come from the core bundle in 2dust/v2rayN-core-bin, pinned to one commit and its
# SHA-256. Update the versions and all hashes together.
# Needs: curl, unzip, tar, rsync, dpkg-deb, rpmbuild, sha256sum.
#
# The .deb also carries the headless client (colitud + colitu, headless/): it is
# cross-compiled with Go (CGO_ENABLED=0). COLITU_HEADLESS=auto (default) includes it when
# Go (or HEADLESS_BIN_DIR, a prebuilt `stage-headless.sh build` dir) is available,
# 1 requires it, 0 leaves it out. The .rpm and .tar.gz do not include it yet.
set -euo pipefail

VERSION="${1:?version}"
ARCH="${2:?x64|arm64}"
PUBLISH="${3:?publish dir}"
OUT="${4:?out dir}"
ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"

# Rule files of 2026-10-04 (the bundle's own cores are replaced below).
CORE_COMMIT="f75f07b3b2906909e6bd2961928e7debd2d75d2a"
XRAY_VERSION="26.10.10"
SINGBOX_VERSION="1.14.3"
case "$ARCH" in
  x64)   DEB_ARCH=amd64; RPM_ARCH=x86_64;  CORE_ZIP=v2rayN-linux-64.zip
         CORE_SHA256=41734f3d7137a6eb72a8e52d2e63dacf8ef0f1b4d6c6b142c05eb8c81ef9cc04
         XRAY_ZIP=Xray-linux-64.zip
         XRAY_ZIP_SHA256=72a5fde819713a103c6fc26a87aa025c1518c918a34eba4bc38d64dc0728e5d9
         XRAY_SHA256=4cca4de3ab8c16b3803a70e9ebe0570d2a72480c716d89aa91603ec568bf0f44
         SINGBOX_TGZ=sing-box-$SINGBOX_VERSION-linux-amd64.tar.gz
         SINGBOX_TGZ_SHA256=e2bdf179c15a3652955dc44867e33ca0965c9c9b91b34267dcc5a5d639a5feee
         SINGBOX_SHA256=768d31fa7f68debc067bb4626539498a1e8751d36a7b99e1a54bee6a23815f93
         CRONET_SHA256=23109c55b08829bb1a68682a61a64852b1bbf243753b153cbd8f6e4ea2c05294 ;;
  arm64) DEB_ARCH=arm64; RPM_ARCH=aarch64; CORE_ZIP=v2rayN-linux-arm64.zip
         CORE_SHA256=0dcb239900953954c3ac37e4c65019ef63d8af75c100348a486a5cca1eef5c10
         XRAY_ZIP=Xray-linux-arm64-v8a.zip
         XRAY_ZIP_SHA256=58e339e67827220175b3a1c6f66f48ccd086b79209f30ba1c0bcb47885d4c22a
         XRAY_SHA256=fadf8d792889536a9410686f3d5c4df76ec7865b7aa2297495365759ffeb424c
         SINGBOX_TGZ=sing-box-$SINGBOX_VERSION-linux-arm64.tar.gz
         SINGBOX_TGZ_SHA256=29dae24d76bea3bc62d07dd3e60cfc12003de7debbd6b768df9be9c68360174e
         SINGBOX_SHA256=299f9f6080d9c43504714ddddae6aa9b853d430ccfc816c456a73613b5455689
         CRONET_SHA256=3c1fcfcb56261a4b318ffb6d2227b9726782edb5a894e74db2b534406db62498 ;;
  *) echo "unknown arch $ARCH" >&2; exit 1 ;;
esac

# Downloads $1 to $2 and stops the build unless its SHA-256 is $3.
fetch_pinned() {
  curl -fsSL "$1" -o "$2"
  echo "$3  $2" | sha256sum -c --quiet - || { echo "checksum mismatch: $1" >&2; exit 1; }
}
# Stops the build unless file $1 has SHA-256 $2.
check_file() {
  [[ -f "$1" ]] || { echo "missing: $1" >&2; exit 1; }
  echo "$2  $1" | sha256sum -c --quiet - || { echo "checksum mismatch: $1" >&2; exit 1; }
}

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
mkdir -p "$OUT"
OUT="$(cd "$OUT" && pwd)"
APP="$WORK/app"           # becomes /opt/colitu-vpn
mkdir -p "$APP"
rsync -a --exclude '*.pdb' "$PUBLISH/" "$APP/"

# ── Cores and rule files ────────────────────────────────────────────────
echo "[+] Cores from 2dust/v2rayN-core-bin@${CORE_COMMIT:0:12}/$CORE_ZIP"
curl -fsSL "https://raw.githubusercontent.com/2dust/v2rayN-core-bin/$CORE_COMMIT/$CORE_ZIP" -o "$WORK/core.zip"
echo "$CORE_SHA256  $WORK/core.zip" | sha256sum -c - || { echo "core bundle checksum mismatch" >&2; exit 1; }
mkdir -p "$WORK/core"
unzip -q "$WORK/core.zip" -d "$WORK/core"
CORE_BIN="$(find "$WORK/core" -maxdepth 3 -type d -name bin | head -n1)"
[[ -n "$CORE_BIN" ]] || { echo "core bundle has no bin/ folder" >&2; exit 1; }
mkdir -p "$APP/bin"
rsync -a "$CORE_BIN/" "$APP/bin/"
# Colitu runs Xray and sing-box only.
rm -rf "$APP/bin/mihomo" "$APP/bin/hysteria" "$APP/bin/naiveproxy" "$APP/bin/tuic" "$APP/bin/juicity" "$APP/bin/brook" "$APP/bin/shadowquic" 2>/dev/null || true
[[ -x "$APP/bin/xray/xray" || -f "$APP/bin/xray/xray" ]] || { echo "xray missing from the bundle" >&2; exit 1; }
[[ -f "$APP/bin/sing_box/sing-box" ]] || { echo "sing-box missing from the bundle" >&2; exit 1; }

# The cores themselves: the official releases, every file checked before it replaces the bundle's.
XRAY_URL="https://github.com/XTLS/Xray-core/releases/download/v$XRAY_VERSION/$XRAY_ZIP"
SINGBOX_URL="https://github.com/SagerNet/sing-box/releases/download/v$SINGBOX_VERSION/$SINGBOX_TGZ"
echo "[+] Xray $XRAY_VERSION ($XRAY_ZIP), sing-box $SINGBOX_VERSION ($SINGBOX_TGZ)"
fetch_pinned "$XRAY_URL" "$WORK/xray.zip" "$XRAY_ZIP_SHA256"
curl -fsSL "$XRAY_URL.dgst" -o "$WORK/xray.zip.dgst"
grep -qiE "^SHA2-256= *$XRAY_ZIP_SHA256\$" "$WORK/xray.zip.dgst" || { echo "the Xray release's .dgst does not list the pinned SHA-256" >&2; exit 1; }
fetch_pinned "$SINGBOX_URL" "$WORK/sing-box.tar.gz" "$SINGBOX_TGZ_SHA256"
mkdir -p "$WORK/xray" "$WORK/sing-box"
unzip -q "$WORK/xray.zip" xray -d "$WORK/xray"
tar -xzf "$WORK/sing-box.tar.gz" -C "$WORK/sing-box" --strip-components=1
check_file "$WORK/xray/xray" "$XRAY_SHA256"
check_file "$WORK/sing-box/sing-box" "$SINGBOX_SHA256"
check_file "$WORK/sing-box/libcronet.so" "$CRONET_SHA256"
install -m 0755 "$WORK/xray/xray" "$APP/bin/xray/xray"
install -m 0755 "$WORK/sing-box/sing-box" "$APP/bin/sing_box/sing-box"
install -m 0644 "$WORK/sing-box/libcronet.so" "$APP/bin/sing_box/libcronet.so"
# Russian sites direct: Xray uses the bundle's geosite.dat/geoip.dat, sing-box these rule sets
# (it would otherwise download them from GitHub, which is blocked in Russia).
mkdir -p "$APP/bin/srss"
install -m 0644 "$ROOT"/srss-dosyalari/*.srs "$APP/bin/srss/"
for f in geosite.dat geoip.dat srss/geosite-category-ru.srs srss/geoip-ru.srs; do
  [[ -s "$APP/bin/$f" ]] || { echo "bin/$f missing" >&2; exit 1; }
done

# ── Permissions ─────────────────────────────────────────────────────────
find "$APP" -type d -exec chmod 0755 {} +
find "$APP" -type f -exec chmod 0644 {} +
chmod 0755 "$APP/ColituVPN"
find "$APP/bin" -type f \( -name xray -o -name sing-box \) -exec chmod 0755 {} +
# TUN mode: sudo runs this launcher (root:root 0755 in the package), never the core directly.
install -m 0755 "$ROOT/scripts/colitu-core-root" "$APP/bin/colitu-core-root"
# TUN mode refuses cores that group/others can write: never ship one.
if [ -n "$(find "$APP" ! -type l -perm /022 -print -quit)" ]; then echo "group/other-writable files in $APP" >&2; exit 1; fi
find "$APP" -maxdepth 1 -type f -name '*.so' -exec chmod 0755 {} +
[[ -f "$APP/createdump" ]] && chmod 0755 "$APP/createdump"

# ── Shared files: launcher, desktop entry, icon ─────────────────────────
STAGE="$WORK/stage"
mkdir -p "$STAGE/opt/colitu-vpn" "$STAGE/usr/bin" "$STAGE/usr/share/applications" \
         "$STAGE/usr/share/icons/hicolor/256x256/apps" "$STAGE/usr/share/pixmaps" "$STAGE/usr/share/doc/colitu-vpn"
rsync -a "$APP/" "$STAGE/opt/colitu-vpn/"

cat > "$STAGE/usr/bin/colitu-vpn" <<'EOF'
#!/bin/sh
exec /opt/colitu-vpn/ColituVPN "$@"
EOF
chmod 0755 "$STAGE/usr/bin/colitu-vpn"

cat > "$STAGE/usr/share/applications/colitu-vpn.desktop" <<'EOF'
[Desktop Entry]
Type=Application
Name=Colitu VPN
GenericName=VPN
Comment=Secure, private and fast VPN
Comment[ru]=Безопасный, приватный и быстрый VPN
Comment[tr]=Güvenli, gizli ve hızlı VPN
Exec=colitu-vpn
Icon=colitu-vpn
Terminal=false
Categories=Network;Security;
Keywords=vpn;proxy;colitu;
StartupWMClass=ColituVPN
EOF
chmod 0644 "$STAGE/usr/share/applications/colitu-vpn.desktop"

ICON="$ROOT/src/ColituVPN/Assets/Colitu/colitu-icon.png"
install -m 0644 "$ICON" "$STAGE/usr/share/icons/hicolor/256x256/apps/colitu-vpn.png"
install -m 0644 "$ICON" "$STAGE/usr/share/pixmaps/colitu-vpn.png"
install -m 0644 "$ROOT/LICENSE" "$STAGE/usr/share/doc/colitu-vpn/copyright"

# ── .deb ────────────────────────────────────────────────────────────────
DEB="$WORK/deb"
rsync -a "$STAGE/" "$DEB/"

# Headless client: binaries, systemd unit, sysusers file.
HEADLESS_MODE="${COLITU_HEADLESS:-auto}"
HEADLESS_DEB=0
if [[ "$HEADLESS_MODE" != 0 ]]; then
  if [[ -n "${HEADLESS_BIN_DIR:-}" ]] || command -v "${GO:-go}" >/dev/null 2>&1; then
    bash "$ROOT/headless/packaging/stage-headless.sh" stage "$DEB" "$DEB_ARCH" "$VERSION"
    HEADLESS_DEB=1
  elif [[ "$HEADLESS_MODE" == 1 ]]; then
    echo "COLITU_HEADLESS=1 but Go was not found" >&2; exit 1
  else
    echo "[!] Go not found: the .deb is built without the headless client (colitud, colitu)" >&2
  fi
fi
SIZE_KB="$(du -sk "$DEB" | cut -f1)"
mkdir -p "$DEB/DEBIAN"
cat > "$DEB/DEBIAN/control" <<EOF
Package: colitu-vpn
Version: ${VERSION}
Architecture: ${DEB_ARCH}
Maintainer: Colitu <support@colitu.com>
Homepage: https://colitu.com
Section: net
Priority: optional
Installed-Size: ${SIZE_KB}
Depends: libc6, libgcc-s1, libstdc++6, zlib1g, libfontconfig1, libssl3t64 | libssl3, libx11-6, libice6, libsm6, libicu78 | libicu77 | libicu76 | libicu74 | libicu72 | libicu70 | libicu-dev, ca-certificates
Recommends: sudo, nftables, pkexec | policykit-1, libnotify-bin, xdg-utils
Description: Colitu VPN desktop client for Linux
 Sign in with your Colitu account and connect with one click. Proxy and
 TUN modes, kill switch (nftables), DNS leak protection, ad blocking and
 live support. Includes the Xray and sing-box cores.
EOF
if [[ "$HEADLESS_DEB" == 1 ]]; then
  cat >> "$DEB/DEBIAN/control" <<'EOF'
 The package also installs the headless client for servers and Raspberry Pi
 (the colitud service and the colitu command); it is not enabled automatically.
EOF
fi
cat > "$DEB/DEBIAN/postinst" <<'EOF'
#!/bin/sh
set -e
command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database -q /usr/share/applications || true
command -v gtk-update-icon-cache >/dev/null 2>&1 && gtk-update-icon-cache -q -f /usr/share/icons/hicolor || true
exit 0
EOF
cp "$DEB/DEBIAN/postinst" "$DEB/DEBIAN/postrm"
# The kill switch fails closed: its nftables table outlives a crashed or killed app.
# Removing the app (not upgrading) removes the table, its marker and an old (<= 1.1.2) watcher.
cat > "$DEB/DEBIAN/prerm" <<'EOF'
#!/bin/sh
set -e
if [ "$1" = remove ] || [ "$1" = purge ]; then
  command -v nft >/dev/null 2>&1 && nft delete table inet colitu_killswitch 2>/dev/null || true
  rm -f /run/colitu-killswitch.active
  if [ -r /run/colitu-killswitch.watch ]; then kill "$(cat /run/colitu-killswitch.watch)" 2>/dev/null || true; rm -f /run/colitu-killswitch.watch; fi
fi
exit 0
EOF
if [[ "$HEADLESS_DEB" == 1 ]]; then
  # Insert the headless fragments (group, daemon-reload, stop on removal) before each
  # script's closing "exit 0". The service is never enabled or started automatically.
  for f in postinst postrm prerm; do
    [[ "$(tail -n1 "$DEB/DEBIAN/$f")" == "exit 0" ]] || { echo "$f does not end with exit 0" >&2; exit 1; }
    sed -i '$d' "$DEB/DEBIAN/$f"
    cat "$ROOT/headless/packaging/maintainer-$f.sh" >> "$DEB/DEBIAN/$f"
    echo "exit 0" >> "$DEB/DEBIAN/$f"
  done
fi
chmod 0755 "$DEB/DEBIAN/postinst" "$DEB/DEBIAN/postrm" "$DEB/DEBIAN/prerm"
DEB_OUT="$OUT/colitu-vpn_${VERSION}_${DEB_ARCH}.deb"
dpkg-deb --root-owner-group -Zxz --build "$DEB" "$DEB_OUT"
echo "[+] $DEB_OUT"

# ── .rpm ────────────────────────────────────────────────────────────────
RPMTOP="$WORK/rpmbuild"
mkdir -p "$RPMTOP"/{BUILD,RPMS,SOURCES,SPECS,SRPMS,BUILDROOT}
cat > "$RPMTOP/SPECS/colitu-vpn.spec" <<EOF
Name:           colitu-vpn
Version:        ${VERSION}
Release:        1
Summary:        Colitu VPN desktop client for Linux
License:        GPL-3.0-only
URL:            https://colitu.com
BuildArch:      ${RPM_ARCH}
AutoReqProv:    no
Requires:       glibc, libgcc, libstdc++, zlib, fontconfig, libicu, ca-certificates
Recommends:     sudo, nftables, polkit, libnotify, xdg-utils

%global debug_package %{nil}
%global __strip /bin/true
%global __brp_strip %{nil}
%global __brp_check_rpaths %{nil}
%global _build_id_links none

%description
Sign in with your Colitu account and connect with one click. Proxy and
TUN modes, kill switch (nftables), DNS leak protection, ad blocking and
live support. Includes the Xray and sing-box cores.

%install
mkdir -p %{buildroot}
cp -a ${STAGE}/. %{buildroot}/

%post
command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database -q /usr/share/applications || :
command -v gtk-update-icon-cache >/dev/null 2>&1 && gtk-update-icon-cache -q -f /usr/share/icons/hicolor || :

%preun
if [ \$1 -eq 0 ]; then
  command -v nft >/dev/null 2>&1 && nft delete table inet colitu_killswitch 2>/dev/null || :
  rm -f /run/colitu-killswitch.active
  if [ -r /run/colitu-killswitch.watch ]; then kill "\$(cat /run/colitu-killswitch.watch)" 2>/dev/null || :; rm -f /run/colitu-killswitch.watch; fi
fi

%postun
command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database -q /usr/share/applications || :

%files
# Root-owned: TUN mode runs the cores in /opt/colitu-vpn/bin as root and refuses any other owner.
%defattr(-,root,root,-)
/opt/colitu-vpn
/usr/bin/colitu-vpn
/usr/share/applications/colitu-vpn.desktop
/usr/share/icons/hicolor/256x256/apps/colitu-vpn.png
/usr/share/pixmaps/colitu-vpn.png
/usr/share/doc/colitu-vpn
EOF
rpmbuild -bb --define "_topdir $RPMTOP" --target "$RPM_ARCH" "$RPMTOP/SPECS/colitu-vpn.spec" >/dev/null
RPM_BUILT="$(find "$RPMTOP/RPMS" -name '*.rpm' | head -n1)"
RPM_OUT="$OUT/colitu-vpn-${VERSION}-1.${RPM_ARCH}.rpm"
cp "$RPM_BUILT" "$RPM_OUT"
echo "[+] $RPM_OUT"

# ── .tar.gz (any distribution) ──────────────────────────────────────────
TARDIR="$WORK/colitu-vpn-${VERSION}-linux-${ARCH}"
mkdir -p "$TARDIR"
rsync -a "$APP/" "$TARDIR/"
install -m 0644 "$STAGE/usr/share/applications/colitu-vpn.desktop" "$TARDIR/colitu-vpn.desktop"
install -m 0644 "$ICON" "$TARDIR/colitu-vpn.png"
TAR_OUT="$OUT/colitu-vpn-${VERSION}-linux-${ARCH}.tar.gz"
tar -C "$WORK" --owner=0 --group=0 -czf "$TAR_OUT" "$(basename "$TARDIR")"
echo "[+] $TAR_OUT"
