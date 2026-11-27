#!/bin/sh
# Construit « Mesh Control.app » pour macOS. À lancer SUR UN MAC (ou un
# exécuteur CI macOS) : la signature de code n'existe que là.
#
#   ./build-macos.sh            # Apple Silicon (osx-arm64)
#   ./build-macos.sh osx-x64    # Mac Intel
#
# Sans identité de signature, l'application est signée « ad hoc » : elle se
# lance sur le Mac qui l'a construite, mais Gatekeeper la bloquera ailleurs.
# Pour la diffuser, définir SIGN_IDENTITY (« Developer ID Application: … »)
# puis la faire notariser (xcrun notarytool) — voir README.md.
set -eu

RID="${1:-osx-arm64}"
HERE="$(cd "$(dirname "$0")" && pwd)"
OUT="$HERE/dist/$RID"
APP="$OUT/Mesh Control.app"

rm -rf "$OUT"
dotnet publish "$HERE/MeshControl.Mac.csproj" -c Release -r "$RID" \
    --self-contained true -p:UseAppHost=true -o "$OUT/publish"

mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp -R "$OUT/publish/." "$APP/Contents/MacOS/"
cp "$HERE/Info.plist" "$APP/Contents/Info.plist"
if [ -f "$HERE/AppIcon.icns" ]; then
    cp "$HERE/AppIcon.icns" "$APP/Contents/Resources/AppIcon.icns"
fi

if [ -n "${SIGN_IDENTITY:-}" ]; then
    codesign --force --deep --options runtime --timestamp \
             --entitlements "$HERE/Entitlements.plist" \
             --sign "$SIGN_IDENTITY" "$APP"
else
    codesign --force --deep --sign - "$APP"
fi

echo "Application prête : $APP"
