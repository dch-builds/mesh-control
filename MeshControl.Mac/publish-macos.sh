#!/bin/sh
# Publication de Mesh Control pour macOS et empaquetage Velopack — l'équivalent
# de publish.ps1 pour la version Windows. À lancer SUR UN MAC : Velopack ne
# produit les paquets macOS (installateur .pkg, paquets de mise à jour) que là.
#
# Pour chaque processeur (Apple Silicon, Intel), un canal de mise à jour
# distinct : osx-arm64 et osx-x64. Une installation suit le canal avec lequel
# elle a été empaquetée.
#
# Prérequis sur le Mac :
#   - SDK .NET 10                      https://dotnet.microsoft.com/download
#   - vpk 1.2.161                      dotnet tool install -g vpk --version 1.2.161
#   - gh, connecté (pour l'envoi)      brew install gh && gh auth login
#
# Usage :
#   ./publish-macos.sh                  → construit et empaquette
#   ./publish-macos.sh --upload         → envoie aussi la release, en brouillon
#   ./publish-macos.sh --first-release  → tout premier paquet (pas de delta)
#   ./publish-macos.sh --rid osx-arm64  → un seul processeur
#
# La version, commune à Windows et macOS, fait foi dans ../Directory.Build.props.
# Les deux plateformes partagent la même release GitHub (même numéro) : l'envoi
# fusionne ses fichiers avec ceux de publish.ps1, chacun sur son canal.
# Le workflow .github/workflows/release.yml lance ce script sur un Mac de
# GitHub ; le jeton y vient du secret RELEASES_TOKEN.
set -eu

HERE="$(cd "$(dirname "$0")" && pwd)"
PROJECT="$HERE/MeshControl.Mac.csproj"
RELEASES="$HERE/Releases"
REPO_URL="${REPO_URL:-https://github.com/dch-builds/mesh-control-releases}"

# Même identité que la version Windows : Velopack distingue les plateformes
# par le canal. Ne plus la changer ensuite (voir publish.ps1).
PACK_ID="MeshControl"
PACK_TITLE="Mesh Control"
AUTHORS="David CHIESA"
MAIN_EXE="MeshControl.Mac"
BUNDLE_ID="com.example.meshcontrol"

UPLOAD=0
FIRST=0
RIDS="osx-arm64 osx-x64"
while [ $# -gt 0 ]; do
    case "$1" in
        --upload) UPLOAD=1 ;;
        --first-release) FIRST=1 ;;
        --rid) shift; RIDS="$1" ;;
        *) echo "Option inconnue : $1" >&2; exit 2 ;;
    esac
    shift
done

command -v vpk >/dev/null || { echo "vpk introuvable : dotnet tool install -g vpk --version 1.2.161" >&2; exit 1; }

# MSBuild résout la version comme pour la compilation (Directory.Build.props).
VERSION="$(dotnet msbuild "$PROJECT" -getProperty:Version -nologo | tail -n 1 | tr -d '[:space:]')"
[ -n "$VERSION" ] || { echo "Version introuvable (Directory.Build.props)" >&2; exit 1; }

# Jeton GitHub : secret RELEASES_TOKEN sur GitHub Actions, sinon celui de gh.
TOKEN="${RELEASES_TOKEN:-}"
if [ -z "$TOKEN" ] && command -v gh >/dev/null; then TOKEN="$(gh auth token 2>/dev/null || true)"; fi
echo "== Mesh Control $VERSION pour macOS"

# Icône : AppIcon.icns, tirée de Assets/app.png avec les outils du système.
ICNS="$HERE/dist/AppIcon.icns"
mkdir -p "$HERE/dist"
if [ ! -f "$ICNS" ]; then
    SET="$HERE/dist/AppIcon.iconset"
    rm -rf "$SET"; mkdir -p "$SET"
    for size in 16 32 128 256 512; do
        sips -z $size $size "$HERE/Assets/app.png" --out "$SET/icon_${size}x${size}.png" >/dev/null
        double=$((size * 2))
        sips -z $double $double "$HERE/Assets/app.png" --out "$SET/icon_${size}x${size}@2x.png" >/dev/null
    done
    iconutil -c icns "$SET" -o "$ICNS"
fi

mkdir -p "$RELEASES"

for RID in $RIDS; do
    CHANNEL="$RID"
    OUT="$HERE/dist/velopack-$RID"
    APP="$OUT/Mesh Control.app"
    echo ""
    echo "== $RID (canal $CHANNEL)"

    rm -rf "$OUT"
    dotnet publish "$PROJECT" -c Release -r "$RID" --self-contained true \
        -p:UseAppHost=true -o "$OUT/publish" --nologo

    # Paquet .app : Info.plist à la version publiée, icône.
    mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
    cp -R "$OUT/publish/." "$APP/Contents/MacOS/"
    cp "$HERE/Info.plist" "$APP/Contents/Info.plist"
    /usr/libexec/PlistBuddy -c "Set :CFBundleShortVersionString $VERSION" "$APP/Contents/Info.plist"
    /usr/libexec/PlistBuddy -c "Set :CFBundleVersion $VERSION" "$APP/Contents/Info.plist"
    cp "$ICNS" "$APP/Contents/Resources/AppIcon.icns"

    # Signature : Developer ID si fournie (diffusion), sinon « ad hoc » —
    # suffisant pour les essais, exigé par macOS sur Apple Silicon.
    if [ -n "${SIGN_IDENTITY:-}" ]; then
        SIGN_ARGS="--signAppIdentity $SIGN_IDENTITY"
    else
        codesign --force --deep --sign - "$APP"
        SIGN_ARGS=""
    fi

    # Version déjà publiée : base du paquet delta (seules les différences à télécharger).
    if [ "$FIRST" -eq 0 ]; then
        echo "Récupération de la version publiée (canal $CHANNEL)..."
        set -- --repoUrl "$REPO_URL" --outputDir "$RELEASES" --channel "$CHANNEL"
        if [ -n "$TOKEN" ]; then set -- "$@" --token "$TOKEN"; fi
        vpk download github "$@" || {
            echo "Téléchargement impossible. Premier paquet de ce canal ? Relancez avec --first-release." >&2
            exit 1
        }
    fi

    # shellcheck disable=SC2086
    vpk pack \
        --packId "$PACK_ID" \
        --packVersion "$VERSION" \
        --packDir "$APP" \
        --packTitle "$PACK_TITLE" \
        --packAuthors "$AUTHORS" \
        --mainExe "$MAIN_EXE" \
        --bundleId "$BUNDLE_ID" \
        --icon "$ICNS" \
        --channel "$CHANNEL" \
        --runtime "$RID" \
        --outputDir "$RELEASES" \
        $SIGN_ARGS

    echo "Paquets du canal $CHANNEL dans $RELEASES"
done

if [ "$UPLOAD" -eq 0 ]; then
    echo ""
    echo "Étape suivante : ./publish-macos.sh --upload (release en brouillon)."
    exit 0
fi

[ -n "$TOKEN" ] || { echo "Jeton GitHub indisponible : gh auth login (ou RELEASES_TOKEN)" >&2; exit 1; }
# --merge : la release de cette version existe déjà le plus souvent, créée par
# l'envoi Windows ; les paquets macOS s'y ajoutent, sous le même numéro.
for RID in $RIDS; do
    echo "Envoi du canal $RID (brouillon)..."
    vpk upload github --repoUrl "$REPO_URL" --token "$TOKEN" --outputDir "$RELEASES" --channel "$RID" \
        --merge --tag "$VERSION" --releaseName "$PACK_TITLE $VERSION"
done
echo ""
echo "Release envoyée en brouillon : $REPO_URL/releases"
echo "Les installations ne la verront qu'une fois publiée depuis GitHub."
