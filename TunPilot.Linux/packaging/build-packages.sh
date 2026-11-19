#!/bin/bash
# Builds TunPilot Linux packages from a published tree.
#   build-packages.sh <app-dir> <arch: x64|arm64> <version> <out-dir> <formats: deb,rpm,tar>
# <app-dir> = contents of /opt/tunpilot (dotnet publish output + cores/ + tunpilot-helper).
# deb needs dpkg-deb (Debian/Ubuntu), rpm needs rpmbuild (Fedora: dnf install rpm-build).
set -euo pipefail
APP="$1"; ARCH="$2"; VERSION="$3"; OUT="$4"; FORMATS="${5:-deb,rpm,tar}"
# folder with the packaging files (TP_PACKAGING when the script itself was copied elsewhere)
HERE="${TP_PACKAGING:-$(cd "$(dirname "$0")" && pwd)}"
case "$ARCH" in
    x64)   DEB_ARCH=amd64; RPM_ARCH=x86_64 ;;
    arm64) DEB_ARCH=arm64; RPM_ARCH=aarch64 ;;
    *) echo "unknown arch $ARCH"; exit 1 ;;
esac
mkdir -p "$OUT"
OUT="$(cd "$OUT" && pwd)"
WORK="$(mktemp -d /tmp/tunpilot-pkg.XXXXXX)"
trap 'rm -rf "$WORK"' EXIT

# ── file tree, shared by all formats ──
ROOT="$WORK/root"
install -d "$ROOT/opt/tunpilot" "$ROOT/usr/bin" "$ROOT/usr/share/applications" \
    "$ROOT/usr/share/icons/hicolor/256x256/apps" "$ROOT/usr/share/polkit-1/actions" "$ROOT/usr/lib/systemd/system"
cp -a "$APP/." "$ROOT/opt/tunpilot/"
# text files may come from a Windows checkout — force LF
tr -d '\r' < "$HERE/tunpilot-helper" > "$ROOT/opt/tunpilot/tunpilot-helper"
tr -d '\r' < "$HERE/tunpilot.desktop" > "$ROOT/usr/share/applications/tunpilot.desktop"
tr -d '\r' < "$HERE/io.github.dandfovs.tunpilot.policy" > "$ROOT/usr/share/polkit-1/actions/io.github.dandfovs.tunpilot.policy"
tr -d '\r' < "$HERE/tunpilot-killswitch.service" > "$ROOT/usr/lib/systemd/system/tunpilot-killswitch.service"
cp "$HERE/../Assets/icon.png" "$ROOT/usr/share/icons/hicolor/256x256/apps/tunpilot.png"
ln -sf /opt/tunpilot/TunPilot "$ROOT/usr/bin/tunpilot"
find "$ROOT" -type d -exec chmod 755 {} +
find "$ROOT" -type f -exec chmod 644 {} +
chmod 755 "$ROOT/opt/tunpilot/TunPilot" "$ROOT/opt/tunpilot/tunpilot-helper" \
    "$ROOT/opt/tunpilot/cores/sing-box/sing-box" "$ROOT/opt/tunpilot/cores/xray/xray"
find "$ROOT/opt/tunpilot" -name "*.so" -exec chmod 755 {} +
find "$ROOT/opt/tunpilot" -name "createdump" -exec chmod 755 {} +

# ── install / remove scripts (same logic for deb and rpm) ──
POSTINST='set -e
# sing-box creates the TUN device and marks its sockets: needs these capabilities, not root
setcap cap_net_admin,cap_net_raw,cap_net_bind_service+ep /opt/tunpilot/cores/sing-box/sing-box || true
if command -v systemctl >/dev/null 2>&1; then
    systemctl daemon-reload >/dev/null 2>&1 || true
    systemctl enable tunpilot-killswitch.service >/dev/null 2>&1 || true
fi
command -v gtk-update-icon-cache >/dev/null 2>&1 && gtk-update-icon-cache -q -t /usr/share/icons/hicolor || true
command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database -q /usr/share/applications || true
exit 0'
# on uninstall (not upgrade): lift the kill-switch and bypass routes so the network is not left blocked
PRERM_BODY='/opt/tunpilot/tunpilot-helper ks-disable >/dev/null 2>&1 || true
    /opt/tunpilot/tunpilot-helper bypass-clear >/dev/null 2>&1 || true
    if command -v systemctl >/dev/null 2>&1; then systemctl disable tunpilot-killswitch.service >/dev/null 2>&1 || true; fi
    rm -rf /var/lib/tunpilot'

NAME="tunpilot_${VERSION}_${DEB_ARCH}"
DESC="VPN client with TUN mode (sing-box + Xray)"
LONGDESC="Routes, exceptions and servers change without restarting TUN.
 Subscriptions, geoip/geosite sets, kill-switch (nftables), bypass routes for games."

if [[ ",$FORMATS," == *",deb,"* ]]; then
    D="$WORK/deb"; cp -a "$ROOT" "$D"; install -d "$D/DEBIAN"
    SIZE=$(du -sk "$D" | cut -f1)
    cat > "$D/DEBIAN/control" <<EOF
Package: tunpilot
Version: $VERSION
Architecture: $DEB_ARCH
Maintainer: dandfovs <345318126+dandfovs@users.noreply.github.com>
Installed-Size: $SIZE
Depends: nftables, iproute2, libcap2-bin, pkexec | policykit-1, libc6, libgcc-s1, libstdc++6, zlib1g, libfontconfig1, libx11-6, libice6, libsm6
Recommends: libnotify-bin
Section: net
Priority: optional
Homepage: https://github.com/dandfovs/tunpilot
Description: $DESC
 $LONGDESC
EOF
    printf '#!/bin/sh\n%s\n' "$POSTINST" > "$D/DEBIAN/postinst"
    printf '#!/bin/sh\nif [ "$1" = remove ] || [ "$1" = purge ]; then\n    %s\nfi\nexit 0\n' "$PRERM_BODY" > "$D/DEBIAN/prerm"
    chmod 755 "$D/DEBIAN/postinst" "$D/DEBIAN/prerm"
    dpkg-deb --root-owner-group -Zxz --build "$D" "$OUT/$NAME.deb" >/dev/null
    echo "deb: $OUT/$NAME.deb"
fi

if [[ ",$FORMATS," == *",rpm,"* ]]; then
    R="$WORK/rpm"; install -d "$R"/{BUILD,RPMS,SOURCES,SPECS,SRPMS}
    cat > "$R/SPECS/tunpilot.spec" <<EOF
Name:           tunpilot
Version:        $VERSION
Release:        1
Summary:        $DESC
License:        GPL-3.0-only
URL:            https://github.com/dandfovs/tunpilot
Requires:       nftables, iproute, libcap, polkit, fontconfig, libX11, libICE, libSM, zlib, libstdc++
Recommends:     libnotify
AutoReqProv:    no
%global debug_package %{nil}
%global source_date_epoch_from_changelog 0
%global __strip /bin/true
%global __brp_check_rpaths %{nil}
%global _build_id_links none

%description
$LONGDESC

%install
cp -a "$ROOT/." %{buildroot}/

%post
$POSTINST

%preun
if [ \$1 -eq 0 ]; then
    $PRERM_BODY
fi

%files
/opt/tunpilot
/usr/bin/tunpilot
/usr/share/applications/tunpilot.desktop
/usr/share/icons/hicolor/256x256/apps/tunpilot.png
/usr/share/polkit-1/actions/io.github.dandfovs.tunpilot.policy
/usr/lib/systemd/system/tunpilot-killswitch.service
EOF
    rpmbuild --quiet --define "_topdir $R" --target "$RPM_ARCH" -bb "$R/SPECS/tunpilot.spec"
    cp "$R"/RPMS/*/*.rpm "$OUT/tunpilot-$VERSION-1.$RPM_ARCH.rpm"
    echo "rpm: $OUT/tunpilot-$VERSION-1.$RPM_ARCH.rpm"
fi

if [[ ",$FORMATS," == *",tar,"* ]]; then
    # portable archive: unpack anywhere and run ./install.sh (or just ./TunPilot without kill-switch/bypass)
    T="$WORK/tar/tunpilot-$VERSION-linux-$ARCH"; install -d "$T"
    cp -a "$ROOT/opt/tunpilot/." "$T/"
    install -d "$T/packaging"
    cp "$ROOT/usr/share/applications/tunpilot.desktop" "$ROOT/usr/share/polkit-1/actions/io.github.dandfovs.tunpilot.policy" \
       "$ROOT/usr/lib/systemd/system/tunpilot-killswitch.service" "$ROOT/usr/share/icons/hicolor/256x256/apps/tunpilot.png" "$T/packaging/"
    cat > "$T/install.sh" <<EOF
#!/bin/sh
# Installs TunPilot to /opt/tunpilot (run with sudo). Uninstall: sudo /opt/tunpilot/install.sh --uninstall
set -e
[ "\$(id -u)" = 0 ] || { echo "run with sudo"; exit 1; }
if [ "\${1:-}" = "--uninstall" ]; then
    $PRERM_BODY
    rm -rf /opt/tunpilot /usr/bin/tunpilot /usr/share/applications/tunpilot.desktop /usr/share/icons/hicolor/256x256/apps/tunpilot.png \\
        /usr/share/polkit-1/actions/io.github.dandfovs.tunpilot.policy /usr/lib/systemd/system/tunpilot-killswitch.service
    echo "TunPilot removed"; exit 0
fi
SRC="\$(cd "\$(dirname "\$0")" && pwd)"
rm -rf /opt/tunpilot && mkdir -p /opt/tunpilot && cp -a "\$SRC/." /opt/tunpilot/
install -Dm644 "\$SRC/packaging/tunpilot.desktop" /usr/share/applications/tunpilot.desktop
install -Dm644 "\$SRC/packaging/tunpilot.png" /usr/share/icons/hicolor/256x256/apps/tunpilot.png
install -Dm644 "\$SRC/packaging/io.github.dandfovs.tunpilot.policy" /usr/share/polkit-1/actions/io.github.dandfovs.tunpilot.policy
install -Dm644 "\$SRC/packaging/tunpilot-killswitch.service" /usr/lib/systemd/system/tunpilot-killswitch.service
ln -sf /opt/tunpilot/TunPilot /usr/bin/tunpilot
$POSTINST
EOF
    chmod 755 "$T/install.sh"
    tar -C "$WORK/tar" --owner=0 --group=0 -czf "$OUT/tunpilot-$VERSION-linux-$ARCH.tar.gz" "tunpilot-$VERSION-linux-$ARCH"
    echo "tar: $OUT/tunpilot-$VERSION-linux-$ARCH.tar.gz"
fi
