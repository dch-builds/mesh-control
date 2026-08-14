# Publication de Mesh Control en fichiers séparés, prête à empaqueter avec Inno Setup.
#
# Le résultat est autonome : aucun runtime .NET n'est requis sur les postes.
# On n'utilise PAS le mode fichier unique, qui réextrait tout dans un dossier
# temporaire à chaque lancement et ralentit donc le démarrage.
#
# Usage :
#   .\publish.ps1                 → publie dans .\dist
#   .\publish.ps1 -Output D:\tmp  → publie ailleurs

param(
    [string]$Output = "$PSScriptRoot\dist"
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'MeshControl.csproj'

Write-Host "Publication de Mesh Control..." -ForegroundColor Cyan

# L'exécutable peut être verrouillé par une instance en cours.
Get-Process 'Mesh Control' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500

if (Test-Path $Output) { Remove-Item $Output -Recurse -Force }

dotnet publish $project -c Release -o $Output --nologo
if ($LASTEXITCODE -ne 0) { throw "La publication a échoué." }

$exe = Join-Path $Output 'Mesh Control.exe'
if (-not (Test-Path $exe)) { throw "Exécutable introuvable dans $Output" }

$files = Get-ChildItem $Output -Recurse -File
$totalMb = [math]::Round(($files | Measure-Object -Property Length -Sum).Sum / 1MB, 1)
$version = (Get-Item $exe).VersionInfo.FileVersion

Write-Host ""
Write-Host "Publication terminée." -ForegroundColor Green
Write-Host "  Dossier : $Output"
Write-Host "  Version : $version"
Write-Host "  Fichiers: $($files.Count)"
Write-Host "  Taille  : $totalMb Mo"
Write-Host ""
Write-Host "Étape suivante : compiler l'installeur avec Inno Setup." -ForegroundColor Yellow
Write-Host "  installer\MeshControl.iss" -ForegroundColor Yellow
