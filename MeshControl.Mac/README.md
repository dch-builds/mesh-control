# Mesh Control pour macOS — prototype

Interface [Avalonia](https://avaloniaui.net) au-dessus du cœur partagé avec la
version Windows. Avalonia est multiplateforme : l'application se lance aussi
sous Windows, ce qui permet de la développer sans Mac.

## Architecture

```
MeshControlPC/
├── Core/, Desktop/, Files/, SSH/   sources de l'application Windows (WPF)
├── MeshControl.Core/               bibliothèque net10.0 sans WPF : elle compile
│                                   par lien les sources partagées ci-dessus
└── MeshControl.Mac/                ce projet (Avalonia)
```

Les sources partagées restent à leur place : l'application Windows les compile
toujours directement, sans changement de structure. Ce qui dépend du socle
graphique passe par trois interfaces, implémentées de chaque côté :

| Interface | Windows (WPF) | macOS (Avalonia) |
|---|---|---|
| `IUiDispatcher` — thread de l'interface | `UI/WpfUiDispatcher.cs` | `AvaloniaUiDispatcher.cs` |
| `IKvmFramebuffer` — décodage et surface du bureau distant | `Desktop/KVMFramebuffer.cs` | `AvaloniaFramebuffer.cs` (Skia) |
| `ICertificatePinStore` — certificats retenus | `UI/AppSettingsPinStore.cs` | `MacSettings.cs` |

## Contenu du prototype

L'interface reprend celle de la version Windows (palette, styles, barre de
titre intégrée, dispositions) : `Theme/` en est le portage.

- Choix du serveur, connexion, second facteur, cookie « appareil de confiance »
- Secrets dans le **Trousseau** macOS (DPAPI quand l'application tourne sous Windows)
- Liste des appareils : volet des dossiers, pastilles de filtre, tri, favoris,
  récentes, recherche surlignée, liste en colonnes, sélection multiple,
  actions d'alimentation, renommage, volet des réglages
- **Bureau à distance** : barre d'outils logée dans la barre de titre (repli
  progressif comme sous Windows), qualité, cadence, écrans, ajustement,
  raccourcis, coller / taper, presse-papiers distant, capture, plein écran,
  alimentation, échelle de l'image, barre d'état

### Clavier

- Les caractères partent en **Unicode** : é, @, [ ou € arrivent tels que tapés,
  quelle que soit la disposition du clavier distant.
- Les touches de commande (flèches, Entrée, F1…) et les combinaisons avec
  Ctrl ou **Cmd** partent en codes VK Windows. **Cmd est traduit en Ctrl** :
  Cmd+C copie sur la machine distante.
- Option ne bascule pas en mode commande : il sert sur Mac à composer des
  caractères (@, [, | sur un clavier français).

## Lancer

Sous Windows ou macOS, avec le SDK .NET 10 :

```sh
dotnet run --project MeshControl.Mac
```

## Construire l'application macOS

Sur un Mac (la signature de code n'existe que là) :

```sh
cd MeshControl.Mac
./build-macos.sh            # Apple Silicon
./build-macos.sh osx-x64    # Mac Intel
```

Le résultat, `dist/<rid>/Mesh Control.app`, est signé « ad hoc » : il se lance
sur le Mac qui l'a construit. Pour le diffuser :

1. Compte Apple Developer (99 $/an) et certificat « Developer ID Application ».
2. `SIGN_IDENTITY="Developer ID Application: …" ./build-macos.sh` — active le
   hardened runtime avec `Entitlements.plist` (droits JIT requis par .NET).
3. Notarisation : `xcrun notarytool submit … --wait`, puis
   `xcrun stapler staple "Mesh Control.app"`.

Remplacer auparavant `CFBundleIdentifier` dans `Info.plist`, et ajouter une
icône `AppIcon.icns`.

## Publier une version et tester les mises à jour

Les mises à jour automatiques reposent sur Velopack, comme la version Windows.
Windows et macOS partagent **un seul numéro de version**, dans
`../Directory.Build.props`, et **une seule release** par version dans
`dch-builds/mesh-control-releases` : les paquets Windows (canal `win`) et
macOS (canaux `osx-arm64`, `osx-x64`) y sont déposés côte à côte.

### Depuis GitHub (sans Mac)

Le workflow `.github/workflows/release.yml` compile Windows sur un runner
Windows, puis macOS sur un runner Mac, et envoie le tout dans la même release,
en **brouillon**.

1. Une seule fois : créer un jeton GitHub à accès fin, limité au dépôt
   `mesh-control-releases`, permission *Contents* en lecture et écriture, et
   l'enregistrer comme secret `RELEASES_TOKEN` du dépôt du code
   (Settings › Secrets and variables › Actions).
2. Augmenter `<Version>` dans `Directory.Build.props`, commiter, pousser.
3. Actions › Release › Run workflow. Cocher « Premier paquet macOS » la toute
   première fois (aucune version Mac publiée à comparer). Le choix
   « macos » seul ajoute les paquets Mac à une release Windows déjà publiée.
4. Vérifier le brouillon dans `mesh-control-releases`, puis le publier.

Le workflow refuse de partir si la version contient déjà les paquets d'une
des plateformes demandées : c'est le signe d'un oubli à l'étape 2.

### En local

`publish.ps1` (Windows) et `publish-macos.sh` (sur un Mac) font la même chose
à la main ; prérequis : SDK .NET 10, `dotnet tool install -g vpk --version
1.2.161`, et `gh auth login` pour l'envoi.

```sh
cd MeshControl.Mac
./publish-macos.sh --first-release   # tout premier paquet de chaque canal
./publish-macos.sh --upload          # versions suivantes : delta + envoi en brouillon
```

### Tester les mises à jour

1. publier une version et installer son `MeshControl-osx-arm64-Setup.pkg` —
   seule une installation Velopack sait se mettre à jour ;
2. augmenter la version, publier à nouveau ;
3. relancer l'application installée, ou Réglages › Mises à jour › Rechercher
   maintenant : elle propose la nouvelle version, la télécharge et redémarre.

Tant que l'application n'est pas signée avec un certificat Developer ID et
notarisée, macOS bloque l'installateur téléchargé : clic droit › Ouvrir, ou
Réglages Système › Confidentialité et sécurité › Ouvrir quand même.

## Non testé à ce stade

Le prototype a été compilé et lancé sous Windows uniquement. À vérifier en
priorité sur un Mac :

1. **Saisie de texte** : que l'évènement `TextInput` d'Avalonia arrive bien
   sur l'écran distant (contrôle non textuel) — sinon, les caractères ne
   partiraient pas.
2. **Trousseau** : l'API « generic password » est dépréciée ; elle fonctionne
   encore, mais il faudra migrer vers `SecItemAdd` / `SecItemCopyMatching`.
3. **Raccourcis réservés** : macOS garde Cmd+Q, Cmd+H, Cmd+Tab et Cmd+Espace,
   qui n'atteignent donc pas la machine distante.

## Reste à faire

Terminal (agent et SSH), fichiers, presse-papiers, choix de l'écran,
plein écran, processus, informations système, partage, icône de la barre de
menus, raccourci global, démarrage automatique (`SMAppService`), reprise
automatique de la connexion au serveur, mises à jour (Velopack prend en charge
macOS).
