#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
WINUI_IMAGE="docker.io/shepherdjerred/windows-cross-compiler-winui@sha256:976070135674e22399d7384418fe975584de36fbdddceeefc09ecff3ddff2cef"
NUGET_VOL="${PLYPH_NUGET_VOL:-plyph-windows-cross-nuget}"

if [ -z "${WindowsCrossTargets:-}" ]; then
  if ! command -v docker >/dev/null 2>&1; then
    echo "Error: docker is required to cross-compile Plyph WinUI 3 deliverables on Linux." >&2
    exit 1
  fi

  NUGET_MOUNT_ARGS=()
  if [ -n "${NUGET_HOST_DIR:-}" ]; then
    mkdir -p "$NUGET_HOST_DIR"
    NUGET_MOUNT_ARGS=(-v "$NUGET_HOST_DIR:/root/.nuget/packages")
  else
    NUGET_MOUNT_ARGS=(-v "$NUGET_VOL:/root/.nuget/packages")
  fi

  exec docker run --rm \
    -e DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    -e DOTNET_NOLOGO=1 \
    -e DOTNET_ROLL_FORWARD=Major \
    -e PLYPH_HOST_UID="$(id -u)" \
    -e PLYPH_HOST_GID="$(id -g)" \
    "${NUGET_MOUNT_ARGS[@]}" \
    -v "$ROOT_DIR:/workspace" \
    -w /workspace \
    "$WINUI_IMAGE" \
    /bin/bash /workspace/scripts/build-linux.sh
fi

fix_ownership() {
  if [ -n "${PLYPH_HOST_UID:-}" ] && [ -n "${PLYPH_HOST_GID:-}" ]; then
    chown -R "${PLYPH_HOST_UID}:${PLYPH_HOST_GID}" \
      "$ROOT_DIR/dist" "$ROOT_DIR/src" "$ROOT_DIR/tests" 2>/dev/null || true
  fi
}
trap fix_ownership EXIT

STAGE_ROOT="$(mktemp -d /tmp/plyph-linux-build-XXXXXX)"
cleanup() {
  rm -rf "$STAGE_ROOT"
  fix_ownership
}
trap cleanup EXIT

DIST_DIR="$ROOT_DIR/dist"
PORTABLE_DIR="$DIST_DIR/Plyph-Portable"
PORTABLE_APP_DIR="$PORTABLE_DIR/app"
PORTABLE_ZIP="$DIST_DIR/Plyph-Portable-win-x64.zip"
INSTALLED_STAGE="$STAGE_ROOT/installed"
PAYLOAD_ZIP="$STAGE_ROOT/Plyph.Payload.zip"
SETUP_STAGE="$STAGE_ROOT/setup"

rm -rf "$DIST_DIR" \
  "$ROOT_DIR/src/Plyph.Core/bin" "$ROOT_DIR/src/Plyph.Core/obj" \
  "$ROOT_DIR/src/Plyph.Windows/bin" "$ROOT_DIR/src/Plyph.Windows/obj" \
  "$ROOT_DIR/src/Plyph.Installer/bin" "$ROOT_DIR/src/Plyph.Installer/obj" \
  "$ROOT_DIR/tests/Plyph.Core.Tests/bin" "$ROOT_DIR/tests/Plyph.Core.Tests/obj"

mkdir -p "$PORTABLE_APP_DIR" "$INSTALLED_STAGE" "$SETUP_STAGE"

echo "[1/4] Running Plyph.Core unit tests..."
dotnet test "$ROOT_DIR/tests/Plyph.Core.Tests/Plyph.Core.Tests.csproj" -c Release -p:NuGetAudit=false --nologo

echo ""
echo "[2/4] Publishing WinUI 3 Non-Install (Portable) folder & ZIP..."
dotnet publish "$ROOT_DIR/src/Plyph.Windows/Plyph.Windows.csproj" \
  -c Release \
  -r win-x64 \
  -p:Platform=x64 \
  -p:PortableBuild=true \
  -p:SelfContained=true \
  -p:WindowsPackageType=None \
  -p:WindowsAppSDKSelfContained=true \
  -p:NuGetAudit=false \
  --nologo \
  -o "$PORTABLE_APP_DIR"

touch "$PORTABLE_APP_DIR/portable.marker" "$PORTABLE_APP_DIR/portable-parent-data.marker"
cp "$ROOT_DIR/LICENSE" "$PORTABLE_APP_DIR/LICENSE.txt"
cp "$ROOT_DIR/README.md" "$PORTABLE_APP_DIR/README.md"

cat > "$STAGE_ROOT/launcher.rc" <<EOF
1 ICON "$ROOT_DIR/src/Plyph.Windows/Assets/Plyph.ico"
EOF
llvm-rc /fo "$STAGE_ROOT/launcher.res" "$STAGE_ROOT/launcher.rc"
cp "$ROOT_DIR/scripts/portable-launcher.c" "$STAGE_ROOT/launcher.c"
(cd "$STAGE_ROOT" && \
  x86_64-pc-windows-msvc-cl /nologo /O2 /GS- launcher.c /Fe:Plyph-Portable.exe \
    /link launcher.res /SUBSYSTEM:WINDOWS /ENTRY:PlyphLauncherEntry /NODEFAULTLIB /MACHINE:X64 kernel32.lib user32.lib)
cp "$STAGE_ROOT/Plyph-Portable.exe" "$PORTABLE_DIR/Plyph-Portable.exe"

python3 -c '
import os, sys, zipfile
src, out = sys.argv[1], sys.argv[2]
with zipfile.ZipFile(out, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=6) as zf:
    for dp, _, fns in os.walk(src):
        for fn in sorted(fns):
            full = os.path.join(dp, fn)
            rel = os.path.relpath(full, src).replace(os.sep, "/")
            zf.write(full, rel)
' "$PORTABLE_DIR" "$PORTABLE_ZIP"

echo ""
echo "[3/4] Publishing WinUI 3 Installed payload..."
dotnet publish "$ROOT_DIR/src/Plyph.Windows/Plyph.Windows.csproj" \
  -c Release \
  -r win-x64 \
  -p:Platform=x64 \
  -p:PortableBuild=false \
  -p:SelfContained=true \
  -p:WindowsPackageType=None \
  -p:WindowsAppSDKSelfContained=true \
  -p:NuGetAudit=false \
  --nologo \
  -o "$INSTALLED_STAGE"

python3 -c '
import os, sys, zipfile
src, out = sys.argv[1], sys.argv[2]
with zipfile.ZipFile(out, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=6) as zf:
    for dp, _, fns in os.walk(src):
        for fn in sorted(fns):
            full = os.path.join(dp, fn)
            rel = os.path.relpath(full, src).replace(os.sep, "/")
            zf.write(full, rel)
' "$INSTALLED_STAGE" "$PAYLOAD_ZIP"
rm -rf "$INSTALLED_STAGE"

echo ""
echo "[4/4] Building self-extracting Windows Installer (Plyph-Setup.exe)..."
dotnet publish "$ROOT_DIR/src/Plyph.Installer/Plyph.Installer.csproj" \
  -c Release \
  -r win-x64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true \
  -p:PayloadZipPath="$PAYLOAD_ZIP" \
  -p:NuGetAudit=false \
  -p:DebugType=none \
  --nologo \
  -o "$SETUP_STAGE"

cp "$SETUP_STAGE/Plyph-Setup.exe" "$DIST_DIR/Plyph-Setup.exe"

echo ""
echo "========================================================"
echo "Linux cross-build succeeded! Output files in: $DIST_DIR"
echo "========================================================"
echo "  1. Non-Install Portable Folder : $PORTABLE_DIR/Plyph-Portable.exe"
echo "  2. Non-Install Portable ZIP    : $PORTABLE_ZIP"
echo "  3. Windows Installer EXE       : $DIST_DIR/Plyph-Setup.exe"
