# Publication de Mesh Control et empaquetage Velopack.
#
# Le résultat est autonome : aucun runtime .NET n'est requis sur les postes.
# On n'utilise PAS le mode fichier unique, qui réextrait tout dans un dossier
# temporaire à chaque lancement et ralentit donc le démarrage.
#
# Deux sorties distinctes :
#   dist\      les fichiers de l'application, tels quels
#   Releases\  les paquets Velopack (Setup.exe, .nupkg, manifeste de canal)
#
# Les mises à jour sont distribuées par un dépôt public dédié, distinct de celui
# du code. Aucun jeton n'est donc embarqué dans l'application ; celui que réclame
# l'envoi est lu à la volée auprès de gh.
#
# Usage :
#   .\publish.ps1                  → publie et empaquette
#   .\publish.ps1 -Upload          → envoie aussi la release, en brouillon
#   .\publish.ps1 -FirstRelease    → tout premier paquet (dépôt encore vide)
#
# La version, commune à Windows et macOS, fait foi dans Directory.Build.props.
# Les deux plateformes partagent la même release GitHub : l'envoi fusionne ses
# fichiers avec ceux que publish-macos.sh y dépose (canaux distincts).
# Le workflow .github/workflows/release.yml lance ce script sur GitHub ; le
# jeton y vient du secret RELEASES_TOKEN.

param(
    [string]$Output   = "$PSScriptRoot\dist",
    [string]$Releases = "$PSScriptRoot\Releases",
    [string]$RepoUrl  = "https://github.com/dch-builds/mesh-control-releases",
    [switch]$Upload,
    [switch]$FirstRelease
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'MeshControl.csproj'

# Identité du paquet. Le packId nomme les fichiers de release et le dossier
# d'installation (%LOCALAPPDATA%\<packId>).
#
# Deux écueils :
#  - ne pas l'écrire « Mesh Control » : le dossier d'installation entrerait en
#    collision avec celui des journaux, utilisé par Log.cs ;
#  - ne plus le changer ensuite. Velopack identifie l'application par lui : une
#    installation posée sous un autre packId ne verra jamais les releases
#    publiées sous celui-ci, et restera indéfiniment à sa version.
$packId    = 'MeshControl'
$packTitle = 'Mesh Control'
$authors   = 'David CHIESA'
$channel   = 'win'
$mainExe   = 'Mesh Control.exe'

# L'outil vpk doit suivre la version du paquet NuGet Velopack référencé par le
# projet : les deux moitiés d'un même produit n'ont pas vocation à diverger.
$vpkVersion = '1.2.161'

if (-not (Get-Command vpk -ErrorAction SilentlyContinue)) {
    throw "L'outil vpk est introuvable. Installez-le :`n" +
          "  dotnet tool install -g vpk --version $vpkVersion"
}

Write-Host "Publication de Mesh Control..." -ForegroundColor Cyan

# L'exécutable peut être verrouillé par une instance en cours.
Get-Process 'Mesh Control' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500

if (Test-Path $Output) { Remove-Item $Output -Recurse -Force }

dotnet publish $project -c Release -o $Output --nologo
if ($LASTEXITCODE -ne 0) { throw "La publication a échoué." }

$exe = Join-Path $Output $mainExe
if (-not (Test-Path $exe)) { throw "Exécutable introuvable dans $Output" }

# La version fait foi dans Directory.Build.props, commune à Windows et macOS.
# MSBuild la résout comme pour la compilation ; la relire sur l'exécutable
# publié donnerait « 1.1.14.0 », alors que Velopack attend une version sémantique.
$version = (dotnet msbuild $project -getProperty:Version -nologo | Select-Object -Last 1).Trim()
if (-not $version) { throw "Version introuvable (Directory.Build.props)" }

$files   = Get-ChildItem $Output -Recurse -File
$totalMb = [math]::Round(($files | Measure-Object -Property Length -Sum).Sum / 1MB, 1)

Write-Host ""
Write-Host "Publication terminée." -ForegroundColor Green
Write-Host "  Dossier : $Output"
Write-Host "  Version : $version"
Write-Host "  Fichiers: $($files.Count)"
Write-Host "  Taille  : $totalMb Mo"
Write-Host ""

New-Item -ItemType Directory -Force $Releases | Out-Null

# Jeton GitHub : secret RELEASES_TOKEN sur GitHub Actions, sinon celui de gh.
# Il reste sur le poste de compilation et n'entre jamais dans un binaire.
function Get-ReleaseToken {
    if ($env:RELEASES_TOKEN) { return $env:RELEASES_TOKEN }
    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { return $null }
    $t = gh auth token 2>$null
    if ($LASTEXITCODE -eq 0) { return $t }
    return $null
}
$token = Get-ReleaseToken

# Récupération de la version déjà publiée. Sans elle, vpk n'a rien à quoi
# comparer et produit un paquet complet — 44 Mo à télécharger pour une correction
# d'une ligne, ce que la migration vise précisément à supprimer.
#
# « vpk download github » n'a pas d'option tolérant un dépôt vide : le tout
# premier paquet doit donc être demandé explicitement avec -FirstRelease. C'est
# voulu — un échec ici doit interrompre la publication plutôt que produire en
# silence un paquet sans delta.
if ($FirstRelease) {
    Write-Host "Premier paquet (-FirstRelease) : aucun delta à calculer." -ForegroundColor Yellow
} else {
    Write-Host "Récupération de la version publiée (pour le delta)..." -ForegroundColor Cyan
    # Le jeton, facultatif ici (dépôt public), évite la limite des requêtes anonymes.
    $tokenArgs = if ($token) { @('--token', $token) } else { @() }
    vpk download github --repoUrl $RepoUrl --outputDir $Releases --channel $channel @tokenArgs
    if ($LASTEXITCODE -ne 0) {
        throw "Téléchargement de la release publiée impossible. Si le dépôt est " +
              "encore vide, relancez avec -FirstRelease."
    }
}

Write-Host ""
Write-Host "Empaquetage Velopack..." -ForegroundColor Cyan

# --runtime n'est pas optionnel ici : sans lui, vpk suppose x86 et marque le
# paquet comme tel, alors que l'application est publiée en win-x64. Le paquet
# s'installerait quand même, mais les contrôles de compatibilité de l'installeur
# porteraient sur la mauvaise architecture.
vpk pack `
    --packId      $packId `
    --packVersion $version `
    --packDir     $Output `
    --packTitle   $packTitle `
    --packAuthors $authors `
    --mainExe     $mainExe `
    --icon        (Join-Path $PSScriptRoot 'app.ico') `
    --runtime     win-x64 `
    --channel     $channel `
    --outputDir   $Releases `
    --shortcuts   Desktop,StartMenuRoot
if ($LASTEXITCODE -ne 0) { throw "L'empaquetage a échoué." }

$setup = Join-Path $Releases "$packId-$channel-Setup.exe"
$delta = Get-ChildItem $Releases -Filter "*$version*-delta.nupkg" -ErrorAction SilentlyContinue

Write-Host ""
Write-Host "Empaquetage terminé." -ForegroundColor Green
Write-Host "  Dossier : $Releases"
if (Test-Path $setup) {
    Write-Host "  Setup   : $([math]::Round((Get-Item $setup).Length / 1MB, 1)) Mo"
}
if ($delta) {
    Write-Host "  Delta   : $([math]::Round($delta.Length / 1MB, 1)) Mo" -ForegroundColor Green
} elseif (-not $FirstRelease) {
    # Anormal hors première version : le plus souvent, la release publiée n'a pas
    # pu être récupérée et la comparaison s'est faite dans le vide.
    Write-Host "  Delta   : aucun — vérifiez que la version publiée a bien été récupérée" -ForegroundColor Yellow
}
Write-Host ""

if (-not $Upload) {
    Write-Host "Étape suivante : envoyer la release." -ForegroundColor Yellow
    Write-Host "  .\publish.ps1 -Upload" -ForegroundColor Yellow
    return
}

# Sans --publish, la release part en brouillon — elle ne devient visible des
# clients qu'une fois publiée depuis GitHub. --merge : la release de cette
# version peut déjà exister, créée par l'envoi macOS ; les fichiers s'y ajoutent.
if ([string]::IsNullOrWhiteSpace($token)) {
    throw "Jeton GitHub indisponible. Connectez-vous avec « gh auth login » " +
          "(ou définissez RELEASES_TOKEN)."
}

Write-Host "Envoi de la release (brouillon)..." -ForegroundColor Cyan
vpk upload github --repoUrl $RepoUrl --token $token `
                  --outputDir $Releases --channel $channel `
                  --merge --tag $version --releaseName "$packTitle $version"
if ($LASTEXITCODE -ne 0) { throw "L'envoi a échoué." }

Write-Host ""
Write-Host "Release envoyée en brouillon." -ForegroundColor Green
Write-Host "  $RepoUrl/releases" -ForegroundColor Yellow
Write-Host "  Les clients ne la verront qu'une fois publiée." -ForegroundColor Yellow
