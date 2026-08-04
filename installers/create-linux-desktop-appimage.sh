#!/bin/bash
# Build Openhpsdr Zeus as TWO Linux AppImages — one per launch mode.
# Usage: ./create-linux-desktop-appimage.sh <version> [rid]
# Example: ./create-linux-desktop-appimage.sh 0.4.1
#          ./create-linux-desktop-appimage.sh 0.4.1 linux-arm64
#
# Output (x64):   OpenhpsdrZeus-<version>-linux-x86_64.AppImage
#                 OpenhpsdrZeus-Server-<version>-linux-x86_64.AppImage
# Output (arm64): OpenhpsdrZeus-<version>-linux-aarch64.AppImage
#                 OpenhpsdrZeus-Server-<version>-linux-aarch64.AppImage
#
# arm64 AppImages are cross-built from an x64 runner via ARCH=aarch64 —
# appimagetool downloads the aarch64 runtime automatically.
#
# Both wrap the same OpenhpsdrZeus binary; they differ only in the AppRun
# / .desktop Exec line (--desktop vs --server). Users grab whichever icon
# they want; if they install both into the same dir they get two
# distinct file-manager / launcher entries.
#
# Companion to create-linux-package.sh which packages the same binary
# plus all three launchers (--, --desktop, --server) as a tarball.
#
# AppImage was chosen over .deb / .rpm for v1 because it runs unchanged
# on any glibc 2.31+ distro (Debian 11, Ubuntu 22.04+, Fedora 36+, Arch,
# etc.) and operators don't need root to install it. .deb / .rpm can be
# layered on later if there is demand.
#
# Runtime dependency: libwebkit2gtk-4.1-0 (Photino's WebView2 equivalent
# on Linux). Bundling WebKitGTK in the AppImage would push the artifact
# from ~80 MB to ~250 MB and lock us to a specific WebKit version, so
# we leave it as a system package and document it in the AppDir README.

set -e

VERSION="${1:-0.0.0}"
RID="${2:-linux-x64}"

# Derive the AppImage architecture label from the RID.
# appimagetool uses these exact strings for ARCH= and output filename suffix.
case "${RID}" in
    linux-x64)   APPIMAGE_ARCH="x86_64"  ;;
    linux-arm64) APPIMAGE_ARCH="aarch64" ;;
    *) echo "Unsupported RID '${RID}' for AppImage build"; exit 1 ;;
esac

if [[ "$(uname -s)" != "Linux" ]]; then
    echo "Warning: AppImage build is intended to run on Linux. Continuing anyway"
    echo "         — the dotnet publish step will work, but appimagetool needs"
    echo "         a Linux kernel for the squashfs invocation."
fi

echo "Creating Openhpsdr Zeus AppImage v${VERSION} for ${RID} (ARCH=${APPIMAGE_ARCH})..."

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(dirname "$SCRIPT_DIR")"
PUBLISH_DIR="${REPO_ROOT}/OpenhpsdrZeus/bin/Release/net10.0/${RID}/publish"
OUTPUT_DIR="${SCRIPT_DIR}/output"
APPDIR="${OUTPUT_DIR}/OpenhpsdrZeus.AppDir"
ICON_SOURCE="${REPO_ROOT}/docs/pics/zeus.png"

# Self-contained publish fallback for local-dev use: only runs if PUBLISH_DIR
# is missing or empty. CI's release.yml runs a single shared `dotnet publish`
# step before any installer-script invocation, so this fallback is skipped
# there.
#
# SPA-FIRST, ALWAYS: MSBuild expands the wwwroot content globs at project
# EVALUATION, before any target runs — so on a tree where
# Zeus.Server.Hosting/wwwroot doesn't exist yet (fresh clone; wwwroot is a
# build artifact, not tracked), the csproj's BuildSpa target (BeforeTargets=
# "Publish") builds the frontend too late: npm runs, wwwroot appears on disk,
# but the publish file list was computed while it was empty and the output
# ships WITHOUT the web app. The backend then serves 404 at / — a working
# radio with no UI. Building the SPA before dotnet publish makes the glob see
# it. (Root-cause fix belongs in OpenhpsdrZeus.csproj; this keeps the
# installer correct meanwhile.)
SPA_INDEX="${REPO_ROOT}/Zeus.Server.Hosting/wwwroot/index.html"
if [ ! -d "${PUBLISH_DIR}" ] || [ -z "$(ls -A "${PUBLISH_DIR}" 2>/dev/null)" ]; then
    # ALWAYS rebuild the SPA before publish — not just when wwwroot is
    # missing. The glob trap has a second face: a STALE wwwroot at
    # evaluation time makes publish ship the old bundle even though the
    # csproj's BuildSpa regenerates it mid-publish (field-hit: a frontend
    # gate change silently absent from a version-stamped AppImage). The
    # pre-build makes the evaluated globs and the shipped bundle agree;
    # the csproj's own publish-time rebuild then finds nothing to change.
    echo "Building the React frontend (pre-publish, glob-evaluation ordering)..."
    ( cd "${REPO_ROOT}/zeus-web" && npm ci && npm run build )
    [ -f "${SPA_INDEX}" ] || { echo "ERROR: frontend build did not produce ${SPA_INDEX}"; exit 1; }
    echo "PUBLISH_DIR is missing — falling back to local publish for ${RID}..."
    # Stamp the assemblies with the release version so the server's
    # AssemblyInformationalVersion (what the Updates tab and the update
    # guards report as "installed") matches the tag instead of the
    # Directory.Build.props in-development fallback (field report: Updates
    # tab stuck on 0.10.9 across every fork release).
    dotnet publish "${REPO_ROOT}/OpenhpsdrZeus/OpenhpsdrZeus.csproj" \
        -p:VersionPrefix="${VERSION}" \
        -c Release \
        -r "${RID}" \
        --self-contained true \
        -p:PublishSingleFile=false \
        -p:UseAppHost=true \
        -o "${PUBLISH_DIR}"
fi

# HARD GATE: never assemble an AppImage whose backend has no web app. This is
# exactly the failure mode that reached hardware once (styled 404 at the
# radio) — cheap to catch here, expensive to catch at the rig.
if [ ! -f "${PUBLISH_DIR}/wwwroot/index.html" ]; then
    echo "ERROR: ${PUBLISH_DIR}/wwwroot/index.html is missing — the publish"
    echo "       output has no frontend. Refusing to build a UI-less AppImage."
    echo "       Build the SPA first (cd zeus-web && npm ci && npm run build),"
    echo "       then re-run 'dotnet publish' and this script."
    exit 1
fi

# Build AppDir layout per the AppImage convention
# (https://docs.appimage.org/packaging-guide/manual.html).
rm -rf "${APPDIR}"
mkdir -p "${APPDIR}/usr/bin"
mkdir -p "${APPDIR}/usr/share/applications"
mkdir -p "${APPDIR}/usr/share/icons/hicolor/512x512/apps"

echo "Staging publish output into AppDir..."
cp -r "${PUBLISH_DIR}"/* "${APPDIR}/usr/bin/"
chmod +x "${APPDIR}/usr/bin/OpenhpsdrZeus"

# Runtime dependency-check helper, sourced by AppRun to verify WebKitGTK
# (Photino's webview backend) before opening the native window. Lands next to
# the binary so AppRun can source it after cd'ing into usr/bin. The server-mode
# AppDir below is copied from this one, so it inherits the helper automatically.
cp "${SCRIPT_DIR}/linux-zeus-preflight.sh" "${APPDIR}/usr/bin/zeus-preflight.sh"
chmod +x "${APPDIR}/usr/bin/zeus-preflight.sh"

# Icon — top-level zeus.png is what AppImageLauncher / file managers show.
if [ -f "${ICON_SOURCE}" ]; then
    cp "${ICON_SOURCE}" "${APPDIR}/usr/share/icons/hicolor/512x512/apps/zeus.png"
    cp "${ICON_SOURCE}" "${APPDIR}/zeus.png"
else
    echo "Warning: ${ICON_SOURCE} not found — AppImage will ship without an icon."
fi

# Desktop entry. The top-level zeus.desktop is what appimagetool picks up;
# the share copy is what desktop-file integration tools install.
cat > "${APPDIR}/zeus.desktop" << EOF
[Desktop Entry]
Type=Application
Name=OpenHPSDR Zeus
GenericName=OpenHPSDR SDR Client
Comment=Cross-platform HPSDR client (Protocol-1 / Protocol-2)
Exec=OpenhpsdrZeus --desktop
Icon=zeus
Categories=AudioVideo;HamRadio;
Terminal=false
StartupWMClass=Zeus
EOF
cp "${APPDIR}/zeus.desktop" "${APPDIR}/usr/share/applications/zeus.desktop"

# AppRun — entry point that AppImage invokes. Pins LD_LIBRARY_PATH so the
# bundled libwdsp.so wins over /usr/lib copies (e.g. from a piHPSDR build),
# same reason as create-linux-package.sh's launcher. Always launches in
# desktop mode (--desktop) — the AppImage is the single-file Photino
# launcher; service mode lives in the tarball.
cat > "${APPDIR}/AppRun" << 'EOF'
#!/bin/bash
HERE="$(dirname "$(readlink -f "${0}")")"
export LD_LIBRARY_PATH="${HERE}/usr/bin/runtimes/linux-x64/native:${HERE}/usr/bin/runtimes/linux-arm64/native:${LD_LIBRARY_PATH}"
cd "${HERE}/usr/bin"
# Verify WebKitGTK before opening the Photino window; offer to install it /
# fall back to the browser UI if it's missing. ZEUS_FORCE_BROWSER=1 skips the
# native window entirely (escape hatch for platforms where WebKitGTK renders
# blank — e.g. Raspberry Pi OS Trixie/Wayland). On aarch64 the known render
# workarounds are exported automatically before Photino starts.
# shellcheck source=/dev/null
. "./zeus-preflight.sh"
if ! zeus_browser_forced && zeus_native_window_viable && zeus_ensure_webkit; then
    zeus_export_webview_render_workarounds
    exec ./OpenhpsdrZeus --desktop "$@"
fi
zeus_run_service_with_browser "$@"
EOF
chmod +x "${APPDIR}/AppRun"

# README inside the AppDir, surfaced as a sibling file in the squashfs.
cat > "${APPDIR}/README.txt" << EOF
Openhpsdr Zeus v${VERSION} for Linux (AppImage, ${APPIMAGE_ARCH})

USAGE
  chmod +x OpenhpsdrZeus-${VERSION}-linux-${APPIMAGE_ARCH}.AppImage
  ./OpenhpsdrZeus-${VERSION}-linux-${APPIMAGE_ARCH}.AppImage

  Optional: integrate with your desktop:
    sudo apt install appimagelauncher   # Debian/Ubuntu
    # then double-click the .AppImage

REQUIREMENTS
  - Linux ${APPIMAGE_ARCH}, glibc 2.31+ (Debian 11+, Ubuntu 22.04+, Fedora 36+, Arch, …)
  - libwebkit2gtk-4.1-0 (Photino's webview backend)

      Debian/Ubuntu:  sudo apt install libwebkit2gtk-4.1-0
      Fedora:         sudo dnf install webkit2gtk4.1
      Arch:           sudo pacman -S webkit2gtk-4.1

  The AppImage detects WebKitGTK automatically: if it's missing it offers to
  install it (when launched from a terminal) and otherwise falls back to the
  browser UI so Zeus still starts.

  RASPBERRY PI / ARM64 NOTE
  On aarch64 the AppImage opens as a chromeless browser (kiosk) window BY
  DEFAULT instead of the native Photino window: WebKitGTK renders blank on
  the Raspberry Pi GPU stack (verified on Pi OS Trixie / WebKitGTK 2.52 with
  every known workaround applied), while the kiosk window is functionally
  identical and renders correctly. No environment variable is needed.

  To re-try the native window (e.g. after a WebKitGTK upgrade):

      ZEUS_FORCE_NATIVE=1 ./OpenhpsdrZeus-${VERSION}-linux-${APPIMAGE_ARCH}.AppImage

  WebKitGTK is intentionally NOT bundled — at ~150 MB it would more than
  triple the AppImage size and lock us to a specific WebKit release. As a
  system library it picks up your distro's security patches automatically.

WHAT YOU GET
  A native window. Closing it stops Zeus completely — there is no separate
  server process. For a browser-based / remote-friendly install, see the
  service-mode tarball (openhpsdr-zeus-${VERSION}-linux-x64.tar.gz).

More info: https://github.com/OpenHPSDR-Zeus-org/openhpsdr-zeus
License:   GNU GPL v2 or later
EOF

# --- AppImage assembly ---------------------------------------------------

# Locate or download appimagetool. We prefer a version already on PATH or
# in OUTPUT_DIR; otherwise grab the upstream continuous release. CI runs
# with --appimage-extract-and-run so we don't need FUSE2 in the runner.
APPIMAGETOOL=""
# appimagetool must match the HOST arch (it's the tool that runs here; the
# TARGET runtime it embeds is chosen separately via ARCH= below). The old
# hardcoded x86_64 download exited 126 ('cannot execute') the first time this
# script ran on a native aarch64 runner (fork release CI).
TOOL_ARCH="$(uname -m)"
if command -v appimagetool &>/dev/null; then
    APPIMAGETOOL="$(command -v appimagetool)"
elif [ -x "${OUTPUT_DIR}/appimagetool-${TOOL_ARCH}.AppImage" ]; then
    APPIMAGETOOL="${OUTPUT_DIR}/appimagetool-${TOOL_ARCH}.AppImage"
else
    echo "Downloading appimagetool (${TOOL_ARCH})..."
    curl -fsSL -o "${OUTPUT_DIR}/appimagetool-${TOOL_ARCH}.AppImage" \
        "https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-${TOOL_ARCH}.AppImage"
    chmod +x "${OUTPUT_DIR}/appimagetool-${TOOL_ARCH}.AppImage"
    APPIMAGETOOL="${OUTPUT_DIR}/appimagetool-${TOOL_ARCH}.AppImage"
fi

OUTPUT_APPIMAGE="${OUTPUT_DIR}/OpenhpsdrZeus-${VERSION}-linux-${APPIMAGE_ARCH}.AppImage"

# --appimage-extract-and-run avoids the FUSE2 dependency that GitHub-hosted
# runners (and most container envs) don't satisfy out of the box.
# ARCH tells appimagetool which architecture runtime to embed — allows
# cross-building arm64 AppImages from an x64 runner.
echo "Building desktop-mode AppImage (ARCH=${APPIMAGE_ARCH})..."
ARCH="${APPIMAGE_ARCH}" "${APPIMAGETOOL}" --appimage-extract-and-run "${APPDIR}" "${OUTPUT_APPIMAGE}"

echo "Desktop AppImage created at ${OUTPUT_APPIMAGE}"

# --- Server-mode AppImage (--server) -----------------------------------
#
# Stage a parallel AppDir whose AppRun + .desktop point at OpenhpsdrZeus
# --server (backend + Photino status window with URLs and Stop button).
# The binary itself is the same; we just swap the launcher.
SERVER_APPDIR="${OUTPUT_DIR}/OpenhpsdrZeusServer.AppDir"
rm -rf "${SERVER_APPDIR}"
cp -r "${APPDIR}" "${SERVER_APPDIR}"
# Overwrite AppRun + .desktop for server mode. The icon stays the same so
# both AppImages render with the Zeus artwork — operators tell them
# apart by filename ("...-Server-...") and by the .desktop Name.
cat > "${SERVER_APPDIR}/AppRun" << 'EOF'
#!/bin/bash
HERE="$(dirname "$(readlink -f "${0}")")"
export LD_LIBRARY_PATH="${HERE}/usr/bin/runtimes/linux-x64/native:${HERE}/usr/bin/runtimes/linux-arm64/native:${LD_LIBRARY_PATH}"
cd "${HERE}/usr/bin"
# Server mode also opens a Photino (WebKitGTK) status window — same check,
# same browser-UI fallback as desktop mode, same ZEUS_FORCE_BROWSER=1
# override and aarch64 render workarounds.
# shellcheck source=/dev/null
. "./zeus-preflight.sh"
if ! zeus_browser_forced && zeus_native_window_viable && zeus_ensure_webkit; then
    zeus_export_webview_render_workarounds
    exec ./OpenhpsdrZeus --server "$@"
fi
zeus_run_service_with_browser "$@"
EOF
chmod +x "${SERVER_APPDIR}/AppRun"

cat > "${SERVER_APPDIR}/zeus.desktop" << EOF
[Desktop Entry]
Type=Application
Name=OpenHPSDR Zeus Server
GenericName=OpenHPSDR SDR Backend
Comment=LAN-bound HPSDR backend with status window and Stop button
Exec=OpenhpsdrZeus --server
Icon=zeus
Categories=AudioVideo;HamRadio;Network;
Terminal=false
StartupWMClass=Zeus Server
EOF
cp "${SERVER_APPDIR}/zeus.desktop" "${SERVER_APPDIR}/usr/share/applications/zeus.desktop"

OUTPUT_SERVER_APPIMAGE="${OUTPUT_DIR}/OpenhpsdrZeus-Server-${VERSION}-linux-${APPIMAGE_ARCH}.AppImage"
echo "Building server-mode AppImage (ARCH=${APPIMAGE_ARCH})..."
ARCH="${APPIMAGE_ARCH}" "${APPIMAGETOOL}" --appimage-extract-and-run "${SERVER_APPDIR}" "${OUTPUT_SERVER_APPIMAGE}"

echo "Server AppImage created at ${OUTPUT_SERVER_APPIMAGE}"
echo
echo "To run:"
echo "  chmod +x ${OUTPUT_APPIMAGE} ${OUTPUT_SERVER_APPIMAGE}"
echo "  ${OUTPUT_APPIMAGE}            # Photino window (--desktop)"
echo "  ${OUTPUT_SERVER_APPIMAGE}     # backend + status window (--server)"
