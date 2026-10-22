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
# Usage :
#   .\publish.ps1                         → publie et empaquette
#   .\publish.ps1 -NoPack                 → publication seule (installeur Inno)
#   .\publish.ps1 -UpdateUrl https://...  → autre source de mises à jour

param(
    [string]$Output    = "$PSScriptRoot\dist",
    [string]$Releases  = "$PSScriptRoot\Releases",
    [string]$UpdateUrl = "https://meshcentral.example.com/updates/",
    [switch]$NoPack
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'MeshControl.csproj'

# Identité du paquet. Le packId sert de nom au dossier d'installation
# (%LOCALAPPDATA%\<packId>) : il ne doit surtout pas valoir « Mesh Control », qui
# entrerait en collision avec le dossier de journaux déjà utilisé par Log.cs.
$packId    = 'MeshControl'
$packTitle = 'Mesh Control'
$authors   = 'Mesh Control'
$channel   = 'win'
$mainExe   = 'Mesh Control.exe'

# L'outil vpk doit suivre la version du paquet NuGet Velopack référencé par le
# projet : les deux moitiés d'un même produit n'ont pas vocation à diverger.
$vpkVersion = '1.2.161'

if (-not $NoPack -and -not (Get-Command vpk -ErrorAction SilentlyContinue)) {
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

# La version fait foi dans le .csproj : la relire sur l'exécutable publié
# donnerait « 1.1.14.0 », alors que Velopack attend une version sémantique.
$version = ([xml](Get-Content $project)).Project.PropertyGroup.Version |
           Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw "Version introuvable dans $project" }

$files   = Get-ChildItem $Output -Recurse -File
$totalMb = [math]::Round(($files | Measure-Object -Property Length -Sum).Sum / 1MB, 1)

Write-Host ""
Write-Host "Publication terminée." -ForegroundColor Green
Write-Host "  Dossier : $Output"
Write-Host "  Version : $version"
Write-Host "  Fichiers: $($files.Count)"
Write-Host "  Taille  : $totalMb Mo"
Write-Host ""

if ($NoPack) {
    # Chemin conservé le temps de la migration : la dernière version Inno
    # (1.1.15) reste nécessaire pour faire basculer le parc existant.
    Write-Host "Empaquetage Velopack ignoré (-NoPack)." -ForegroundColor Yellow
    Write-Host "Étape suivante : compiler l'installeur avec Inno Setup." -ForegroundColor Yellow
    Write-Host "  installer\MeshControl.iss" -ForegroundColor Yellow
    return
}

New-Item -ItemType Directory -Force $Releases | Out-Null

# Récupération de la version déjà publiée. Sans elle, vpk n'a rien à quoi
# comparer et produit un paquet complet — 44 Mo à télécharger pour une
# correction d'une ligne, ce que la migration vise précisément à supprimer.
# --allowEmptyChannel couvre le tout premier paquet, quand la source est encore
# vide : la commande sort alors sans erreur au lieu d'interrompre le script.
Write-Host "Récupération de la version publiée (pour le delta)..." -ForegroundColor Cyan
vpk download http --url $UpdateUrl --outputDir $Releases --channel $channel --allowEmptyChannel
if ($LASTEXITCODE -ne 0) { throw "Le téléchargement des releases existantes a échoué." }

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
} else {
    # Normal pour la toute première version, anormal ensuite : le plus souvent,
    # la source n'était pas joignable et le delta n'a pas pu être calculé.
    Write-Host "  Delta   : aucun (première version, ou source injoignable)" -ForegroundColor Yellow
}
Write-Host ""
Write-Host "Étape suivante : synchroniser le dossier vers la source de mises à jour." -ForegroundColor Yellow
Write-Host "  $Releases  ->  $UpdateUrl" -ForegroundColor Yellow
