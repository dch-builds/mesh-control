<#
.SYNOPSIS
    Construit, depuis Windows, les paquets de test macOS de Mesh Control.

.DESCRIPTION
    Pour chaque processeur (Apple Silicon et Intel) :
      1. publication autonome (aucun runtime .NET requis sur le Mac) ;
      2. assemblage de « Mesh Control.app » (Info.plist, icône) ;
      3. archive .tar.gz avec un script d'installation.

    L'archive est un .tar.gz et non un .zip : elle conserve les droits
    d'exécution Unix, que les outils d'archivage de Windows perdent.

    Le .app n'est PAS signé (la signature n'existe que sur un Mac) : le script
    d'installation retire la quarantaine et le signe « ad hoc » sur place, ce
    qu'exige macOS sur Apple Silicon. Pour une diffusion, utiliser
    build-macos.sh sur un Mac, avec un certificat Developer ID et la
    notarisation (voir README.md).

    Résultat dans MeshControl.Mac/dist/ (exclu de git).

.EXAMPLE
    .\package-macos.ps1
    .\package-macos.ps1 -Runtimes osx-arm64
#>
param(
    [string[]] $Runtimes = @('osx-arm64', 'osx-x64')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$here = $PSScriptRoot
$project = Join-Path $here 'MeshControl.Mac.csproj'
$dist = Join-Path $here 'dist'
$version = ([xml](Get-Content $project -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { $version = '0.1.0' }

# ---------------------------------------------------------------------------
# Icône : AppIcon.icns, assemblée à partir de Assets/app.png. Le format icns
# accepte des PNG tels quels : un en-tête, puis une entrée par taille.
# ---------------------------------------------------------------------------
function New-Icns([string] $pngPath, [string] $outPath) {
    $source = [System.Drawing.Image]::FromFile($pngPath)
    $entries = New-Object System.Collections.Generic.List[byte[]]
    foreach ($pair in @(@('ic07', 128), @('ic08', 256), @('ic09', 512))) {
        $size = $pair[1]
        $bitmap = New-Object System.Drawing.Bitmap $size, $size
        $g = [System.Drawing.Graphics]::FromImage($bitmap)
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
        $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $g.DrawImage($source, 0, 0, $size, $size)
        $g.Dispose()
        $ms = New-Object System.IO.MemoryStream
        $bitmap.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $bitmap.Dispose()
        $png = $ms.ToArray()
        $entry = New-Object byte[] (8 + $png.Length)
        [System.Text.Encoding]::ASCII.GetBytes($pair[0]).CopyTo($entry, 0)
        $len = [BitConverter]::GetBytes([int](8 + $png.Length)); [Array]::Reverse($len); $len.CopyTo($entry, 4)
        $png.CopyTo($entry, 8)
        $entries.Add($entry)
    }
    $source.Dispose()

    $total = 8 + ($entries | Measure-Object -Property Length -Sum).Sum
    $out = [System.IO.File]::Create($outPath)
    $out.Write([System.Text.Encoding]::ASCII.GetBytes('icns'), 0, 4)
    $len = [BitConverter]::GetBytes([int]$total); [Array]::Reverse($len); $out.Write($len, 0, 4)
    foreach ($e in $entries) { $out.Write($e, 0, $e.Length) }
    $out.Dispose()
}

# ---------------------------------------------------------------------------
# Archive .tar.gz avec droits Unix explicites.
# ---------------------------------------------------------------------------
$executableMode = [System.IO.UnixFileMode]'UserRead, UserWrite, UserExecute, GroupRead, GroupExecute, OtherRead, OtherExecute'
$fileMode = [System.IO.UnixFileMode]'UserRead, UserWrite, GroupRead, OtherRead'

function Write-TarGz([string] $sourceDir, [string] $rootName, [string] $outPath, [string[]] $executables) {
    $file = [System.IO.File]::Create($outPath)
    $gzip = New-Object System.IO.Compression.GZipStream($file, [System.IO.Compression.CompressionLevel]::Optimal)
    $tar = New-Object System.Formats.Tar.TarWriter($gzip, [System.Formats.Tar.TarEntryFormat]::Pax, $false)

    $dirEntry = New-Object System.Formats.Tar.PaxTarEntry([System.Formats.Tar.TarEntryType]::Directory, "$rootName/")
    $dirEntry.Mode = $executableMode
    $tar.WriteEntry($dirEntry)

    $base = (Resolve-Path $sourceDir).Path.TrimEnd('\')
    foreach ($item in Get-ChildItem $base -Recurse | Sort-Object FullName) {
        $relative = $item.FullName.Substring($base.Length + 1).Replace('\', '/')
        $name = "$rootName/$relative"
        if ($item.PSIsContainer) {
            $entry = New-Object System.Formats.Tar.PaxTarEntry([System.Formats.Tar.TarEntryType]::Directory, "$name/")
            $entry.Mode = $executableMode
            $tar.WriteEntry($entry)
            continue
        }
        $entry = New-Object System.Formats.Tar.PaxTarEntry([System.Formats.Tar.TarEntryType]::RegularFile, $name)
        $isExecutable = $executables -contains $relative -or $item.Extension -in @('.command', '.dylib') -or $item.Name -eq 'createdump'
        $entry.Mode = if ($isExecutable) { $executableMode } else { $fileMode }
        $stream = [System.IO.File]::OpenRead($item.FullName)
        $entry.DataStream = $stream
        $tar.WriteEntry($entry)
        $stream.Dispose()
    }
    $tar.Dispose(); $gzip.Dispose(); $file.Dispose()
}

# ---------------------------------------------------------------------------
# Script d'installation, exécuté sur le Mac.
# ---------------------------------------------------------------------------
$installScript = @'
#!/bin/sh
# Installe Mesh Control dans le dossier Applications (version de test).
#
# L'application n'est pas signée par un développeur identifié : ce script
# retire la quarantaine posée au téléchargement et la signe « ad hoc » sur
# ce Mac, ce qu'exige macOS pour l'ouvrir sur Apple Silicon.
set -e
HERE="$(cd "$(dirname "$0")" && pwd)"
SOURCE="$HERE/Mesh Control.app"
TARGET="/Applications/Mesh Control.app"

echo "Installation de Mesh Control dans le dossier Applications…"
if [ -d "$TARGET" ]; then rm -rf "$TARGET"; fi
cp -R "$SOURCE" "$TARGET"
xattr -cr "$TARGET"
codesign --force --deep --sign - "$TARGET"
echo "Terminé. Lancement…"
open "$TARGET"
'@ -replace "`r`n", "`n"

$readme = @'
Mesh Control pour macOS — version de test
==========================================

Installation
------------
1. Faites un clic droit (ou Ctrl+clic) sur « Installer Mesh Control.command »,
   puis choisissez « Ouvrir », et confirmez.
   Le double-clic simple est refusé par macOS pour un script non signé.
2. Le Terminal s'ouvre, copie l'application dans le dossier Applications,
   la prépare (retrait de la quarantaine, signature locale) et la lance.

Si macOS bloque encore le script : ouvrez le Terminal et tapez
   sh « chemin du dossier »/Installer\ Mesh\ Control.command
(glissez le fichier dans la fenêtre du Terminal pour obtenir son chemin).

Prérequis
---------
- macOS 13 (Ventura) ou plus récent.
- Archive « arm64 » pour les Mac Apple Silicon (M1, M2, M3…),
  archive « x64 » pour les Mac Intel. Menu  > À propos de ce Mac.

Ce prototype n'est ni signé par un développeur identifié ni notarisé : il
est destiné aux essais, pas à la diffusion.
'@ -replace "`r`n", "`n"

New-Item -ItemType Directory -Force $dist | Out-Null
$icns = Join-Path $dist 'AppIcon.icns'
New-Icns (Join-Path $here 'Assets\app.png') $icns

foreach ($rid in $Runtimes) {
    $arch = if ($rid -eq 'osx-arm64') { 'Apple Silicon' } else { 'Intel' }
    Write-Host "== $rid ($arch)" -ForegroundColor Cyan
    $work = Join-Path $dist $rid
    if (Test-Path $work) { Remove-Item $work -Recurse -Force }
    $publish = Join-Path $work 'publish'

    dotnet publish $project -c Release -r $rid --self-contained true -p:UseAppHost=true -o $publish -nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "Échec de la publication pour $rid" }

    $stage = Join-Path $work 'stage'
    $app = Join-Path $stage 'Mesh Control.app'
    $macos = Join-Path $app 'Contents\MacOS'
    $resources = Join-Path $app 'Contents\Resources'
    New-Item -ItemType Directory -Force $macos, $resources | Out-Null
    Copy-Item (Join-Path $publish '*') $macos -Recurse
    Copy-Item $icns (Join-Path $resources 'AppIcon.icns')

    # Info.plist : version du projet, macOS 13 au minimum.
    $plist = (Get-Content (Join-Path $here 'Info.plist') -Raw) -replace "`r`n", "`n"
    $plist = $plist -replace '(<key>CFBundleShortVersionString</key>\s*<string>)[^<]*', "`${1}$version"
    $plist = $plist -replace '(<key>CFBundleVersion</key>\s*<string>)[^<]*', "`${1}$version"
    $plist = $plist -replace '(<key>LSMinimumSystemVersion</key>\s*<string>)[^<]*', '${1}13.0'
    [System.IO.File]::WriteAllText((Join-Path $app 'Contents\Info.plist'), $plist, (New-Object System.Text.UTF8Encoding $false))
    [System.IO.File]::WriteAllText((Join-Path $app 'Contents\PkgInfo'), 'APPL????', (New-Object System.Text.UTF8Encoding $false))

    [System.IO.File]::WriteAllText((Join-Path $stage 'Installer Mesh Control.command'), $installScript, (New-Object System.Text.UTF8Encoding $false))
    [System.IO.File]::WriteAllText((Join-Path $stage 'LISEZMOI.txt'), $readme, (New-Object System.Text.UTF8Encoding $false))

    $short = $rid.Substring(4)
    $root = "Mesh Control $version ($arch)"
    $archive = Join-Path $dist "MeshControl-macOS-$short-$version.tar.gz"
    Write-TarGz $stage $root $archive @('Mesh Control.app/Contents/MacOS/MeshControl.Mac')
    $sizeMb = [Math]::Round((Get-Item $archive).Length / 1MB, 1)
    Write-Host "   $archive ($sizeMb Mo)" -ForegroundColor Green
}
