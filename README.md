# Mesh Control

Application Windows native pour piloter un serveur
[MeshCentral](https://meshcentral.com) : bureau à distance, terminal, transfert
de fichiers et actions d'alimentation.

Écrite en C# / WPF sur .NET 9, sans dépendance externe hors bibliothèque
standard.

## Fonctionnalités

**Appareils**
- Liste groupée par dossier, repliable, avec état en ligne en temps réel
- Recherche instantanée et filtre « en ligne uniquement »
- Favoris épinglés et machines récemment utilisées
- Palette de recherche rapide par raccourci clavier global, utilisable même
  lorsque l'application est en arrière-plan

**Bureau à distance**
- Décodage natif du protocole KVM (tuiles JPEG, trames jumbo)
- Cadence réglable jusqu'à 60 images/s, trois niveaux de qualité
- Souris, molette et clavier complet (codes VK Windows)
- Raccourcis prédéfinis adaptés à Windows ou Linux
- Presse-papiers dans les deux sens

**Terminal**
- Via l'agent : invite de commandes, PowerShell, shell utilisateur, et sur Linux
  un *login shell* demandant identifiant et mot de passe
- Via SSH lorsque le serveur l'autorise
- Émulateur ANSI/VT100 intégré (couleurs, curseur, zones de défilement)

**Fichiers**
- Navigation, téléchargement, envoi avec fenêtre de fragments en vol
- Création de dossier, renommage, suppression

**Administration**
- Alimentation : réveil Wake-on-LAN, redémarrage, veille, extinction
- Liste et arrêt des processus distants
- Informations système : processeur, mémoire, disques, BIOS
- Ouverture d'une URL sur la machine distante
- Génération de liens de partage publics temporaires, avec code d'intégration

**Application**
- Fonctionnement dans la zone de notification, démarrage automatique possible
- Reconnexion automatique après coupure réseau
- Journal d'erreurs pour le diagnostic

## Compilation

Nécessite le [SDK .NET 9](https://dotnet.microsoft.com/download).

```bash
dotnet build MeshControl.csproj -c Release
```

## Publication

Produit une application autonome, sans prérequis .NET sur les postes cibles :

```powershell
.\publish.ps1
```

Le résultat est dans `dist/`. Un script
[Inno Setup](https://jrsoftware.org/isinfo.php) est fourni pour construire un
installeur : `installer/MeshControl.iss`.

> Windows affiche un avertissement SmartScreen au premier lancement,
> l'exécutable n'étant pas signé numériquement. Cliquer sur « Informations
> complémentaires » puis « Exécuter quand même ».

## Configuration

L'adresse du serveur se règle au premier lancement ou dans le volet des
réglages. Les identifiants sont chiffrés par DPAPI — liés à la session Windows
de l'utilisateur — et stockés dans `%APPDATA%\Mesh Control\`.

Certaines fonctions dépendent de la configuration du serveur MeshCentral :

| Fonction | Prérequis serveur |
|---|---|
| Terminal SSH | `"ssh": true` dans la section domaine |
| Presse-papiers | `clipboardget` / `clipboardset` non désactivés |
| Processus, informations système | droit « Console » sur le groupe d'appareils |
| Intégration en iframe | `allowFraming` et `allowedFramingOrigins` |

## Organisation du code

```
Core/       Connexion au serveur, protocoles, réglages, journal
Desktop/    Bureau à distance : session KVM, framebuffer, fenêtre
SSH/        Terminaux (agent et SSH), émulateur ANSI
Files/      Navigation et transfert de fichiers
UI/         Fenêtre principale, volet de réglages, boîtes de dialogue
```

## Mentions

Mesh Control est un client indépendant pour MeshCentral. Il n'est ni affilié ni
approuvé par le projet MeshCentral. MeshCentral est © Intel Corporation, créé
par Ylian Saint-Hilaire, sous licence Apache 2.0. Cette application ne contient
aucun code MeshCentral : elle communique avec les serveurs via leurs interfaces
publiées.

Windows est une marque du groupe Microsoft. Linux® est une marque déposée de
Linus Torvalds. Les autres marques appartiennent à leurs propriétaires
respectifs.
