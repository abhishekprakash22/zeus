#!/bin/bash
# SPDX-License-Identifier: GPL-2.0-or-later
#
# Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
# Copyright (C) 2025-2026 Brian Keating (EI6LF),
#                         Douglas J. Cerrato (KB2UKA),
#                         Christian Suarez (N9WAR), and contributors.
#
# Build the shipped macOS .pkg installer from the .app bundles produced by
# create-macos-app.sh. This script does not publish or assemble the .app payload;
# it packages the already-built bundles into a click-through installer.
#
# Usage: ./create-macos-pkg.sh <version> <arch>

set -euo pipefail

VERSION="${1:-0.0.0}"
ARCH="${2:-arm64}"

if [ "$ARCH" != "arm64" ] && [ "$ARCH" != "x64" ]; then
    echo "Error: ARCH must be 'arm64' or 'x64'" >&2
    exit 1
fi

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
OUTPUT_DIR="${SCRIPT_DIR}/output"
APP_NAME="OpenHPSDR Zeus.app"
SERVER_APP_NAME="OpenHPSDR Zeus Server.app"
APP_BUNDLE="${OUTPUT_DIR}/${APP_NAME}"
SERVER_APP_BUNDLE="${OUTPUT_DIR}/${SERVER_APP_NAME}"
WORK_DIR="${OUTPUT_DIR}/pkgbuild"
ROOT_DIR="${WORK_DIR}/root"
SCRIPTS_DIR="${WORK_DIR}/scripts"
RESOURCES_DIR="${WORK_DIR}/resources"
PKG_ID="com.ei6lf.zeus.pkg"
COMPONENT_PKG="${WORK_DIR}/openhpsdr-zeus-component.pkg"
PKG_NAME="openhpsdr-zeus-${VERSION}-macos-${ARCH}.pkg"
PKG_PATH="${OUTPUT_DIR}/${PKG_NAME}"

if [ ! -d "${APP_BUNDLE}" ]; then
    echo "ERROR: ${APP_BUNDLE} not found. Run create-macos-app.sh first." >&2
    exit 1
fi
if [ ! -d "${SERVER_APP_BUNDLE}" ]; then
    echo "ERROR: ${SERVER_APP_BUNDLE} not found. Run create-macos-app.sh first." >&2
    exit 1
fi

echo "Creating macOS package ${PKG_NAME} from existing app bundles..."
rm -rf "${WORK_DIR}"
rm -f "${PKG_PATH}"
mkdir -p "${ROOT_DIR}" "${SCRIPTS_DIR}" "${RESOURCES_DIR}"

cp -R "${APP_BUNDLE}" "${ROOT_DIR}/"
cp -R "${SERVER_APP_BUNDLE}" "${ROOT_DIR}/"

cat > "${SCRIPTS_DIR}/preinstall" << 'EOF'
#!/bin/bash
set -e

for app in \
    "/Applications/OpenHPSDR Zeus.app" \
    "/Applications/OpenHPSDR Zeus Server.app" \
    "/Applications/Zeus.app" \
    "/Applications/Zeus Server.app" \
    "/Applications/Openhpsdr Zeus.app" \
    "/Applications/Openhpsdr Zeus Server.app"
do
    if [ -e "$app" ]; then
        rm -rf "$app"
    fi
done

exit 0
EOF
chmod +x "${SCRIPTS_DIR}/preinstall"

cat > "${RESOURCES_DIR}/welcome.html" << 'EOF'
<!doctype html>
<html>
  <body>
    <p>Please click Install to continue.</p>
  </body>
</html>
EOF

cat > "${WORK_DIR}/distribution.xml" << EOF
<?xml version="1.0" encoding="utf-8"?>
<installer-gui-script minSpecVersion="2">
  <title>OpenHPSDR Zeus</title>
  <welcome file="welcome.html" mime-type="text/html"/>
  <!-- Future license pane stub:
  <license file="license.html" mime-type="text/html"/>
  -->
  <options customize="never" require-scripts="true"/>
  <domains enable_localSystem="true"/>
  <choices-outline>
    <line choice="default"/>
  </choices-outline>
  <choice id="default" visible="false" title="OpenHPSDR Zeus">
    <pkg-ref id="${PKG_ID}"/>
  </choice>
  <pkg-ref id="${PKG_ID}" version="${VERSION}" onConclusion="none">openhpsdr-zeus-component.pkg</pkg-ref>
</installer-gui-script>
EOF

pkgbuild \
    --root "${ROOT_DIR}" \
    --install-location /Applications \
    --identifier "${PKG_ID}" \
    --version "${VERSION}" \
    --scripts "${SCRIPTS_DIR}" \
    "${COMPONENT_PKG}"

PRODUCTBUILD_ARGS=(
    --distribution "${WORK_DIR}/distribution.xml"
    --package-path "${WORK_DIR}"
    --resources "${RESOURCES_DIR}"
)
if [ -n "${PKG_SIGN_IDENTITY:-}" ]; then
    echo "Signing package with: ${PKG_SIGN_IDENTITY}"
    if [ -n "${KEYCHAIN_PATH:-}" ]; then
        PRODUCTBUILD_ARGS+=(--keychain "${KEYCHAIN_PATH}")
    fi
    PRODUCTBUILD_ARGS+=(--sign "${PKG_SIGN_IDENTITY}")
else
    echo "WARNING: PKG_SIGN_IDENTITY is not set; building an unsigned macOS package." >&2
fi

productbuild "${PRODUCTBUILD_ARGS[@]}" "${PKG_PATH}"

echo "Package created at ${PKG_PATH}"
if [ -n "${PKG_SIGN_IDENTITY:-}" ]; then
    pkgutil --check-signature "${PKG_PATH}"
else
    echo "Package is unsigned; CI notarization should be skipped for this artifact."
fi
