#!/usr/bin/env bash
# Builds RELAY for Windows x64 from Linux/macOS/WSL:
#   artifacts/RELAY Setup.exe              - RELAY's own setup wizard (per-user, no admin, no Windows Installer)
#   artifacts/RELAY-win-x64-portable.zip   - extract and run, data kept next to Relay.exe
# Optional: MSI=1 ./publish.sh also builds artifacts/RELAY-setup-x64.msi (needs `wixl`).
# Requires the .NET 10 SDK.
set -euo pipefail
cd "$(dirname "$0")"
VERSION=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' Directory.Build.props)
OUT=artifacts/publish/RELAY
ZIP=artifacts/RELAY-win-x64-portable.zip
SETUP="artifacts/RELAY Setup.exe"
PAYLOAD=installer/payload/relay-payload.zip
rm -rf "$OUT" "$ZIP" "$SETUP" artifacts/publish/setup artifacts/RELAY-setup-x64.msi
mkdir -p installer/payload

dotnet test tests/Relay.Tests -c Release
dotnet publish src/Relay.App/Relay.App.csproj -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=true -o "$OUT"
find "$OUT" \( -name '*.pdb' -o -name '*.xml' \) -delete

# 1. Setup.exe: the app files (no portable marker; installed copies keep data in %LOCALAPPDATA%\RELAY)
#    are zipped and embedded into a single self-contained wizard executable.
rm -f "$PAYLOAD"
(cd "$OUT" && zip -qr -9 "$OLDPWD/$PAYLOAD" .)
dotnet publish src/Relay.Setup/Relay.Setup.csproj -c Release -r win-x64 -o artifacts/publish/setup
cp artifacts/publish/setup/RelaySetup.exe "$SETUP"
echo "Setup: $SETUP"

# 2. Optional MSI (Windows Installer may be blocked on managed PCs; Setup.exe is the recommended installer).
if [[ "${MSI:-0}" == "1" ]] && command -v wixl >/dev/null; then
  FILES=$(mktemp --suffix .wxs)
  (cd artifacts/publish && find RELAY -type f | sort | \
     wixl-heat -p RELAY/ --component-group RelayFiles --var var.SourceDir --directory-ref INSTALLDIR --win64) > "$FILES"
  wixl -a x64 -D Version="$VERSION" -D Win64=yes -D SourceDir="$OUT" -o artifacts/RELAY-setup-x64.msi installer/Relay.wxs "$FILES"
  rm -f "$FILES"
fi

# 3. Portable ZIP: same files plus the marker that keeps data next to Relay.exe.
touch "$OUT/relay.portable"
(cd artifacts/publish && zip -qr "../$(basename "$ZIP")" RELAY)
echo "Portable build: $ZIP"
