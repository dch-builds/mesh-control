# Mesh Control

Application Windows native pour piloter un serveur
[MeshCentral](https://meshcentral.com) : bureau à distance, terminal, transfert
de fichiers et actions d'alimentation.

Écrite en C# / WPF sur .NET 10, sans dépendance externe hors bibliothèque
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
- Souris, molette, boutons latéraux et clavier complet (codes VK Windows). La
  touche Windows n'est pas transmise — le poste local l'intercepte de toute
  façon — et s'envoie par le menu « Raccourcis »
- Raccourcis prédéfinis adaptés à Windows ou Linux
- Raccourcis locaux : Ctrl+Maj+R redemande une image complète, Ctrl+Maj+A
  bascule l'ajustement à la fenêtre, Ctrl+Maj+D débloque le clavier distant et
  Ctrl+Maj+Suppr envoie Ctrl+Alt+Suppr
- Plein écran par Ctrl+Maj+F : barres et bandeau d'onglets masqués
- Le flux s'interrompt dès que la session n'est plus regardée — onglet passé à
  l'arrière-plan, fenêtre réduite — et reprend sur une image complète
- Presse-papiers dans les deux sens. Le collage écrit le presse-papiers de la
  machine distante puis envoie Ctrl+V — instantané, quelle que soit la longueur
  du texte. Si le serveur refuse l'écriture (`clipboardset`) ou si l'agent ne la
  confirme pas, la saisie caractère par caractère prend le relais ; elle reste
  accessible à la demande, pour les applications qui ignorent Ctrl+V

**Terminal**
- Via l'agent : invite de commandes, PowerShell, shell utilisateur, et sur Linux
  un *login shell* demandant identifiant et mot de passe
- Via SSH lorsque le serveur l'autorise
- Émulateur ANSI/VT100 intégré (couleurs, curseur, zones de défilement)
- Historique de 5 000 lignes : molette, Maj+Page précédente et Maj+Page
  suivante, Ctrl+Maj+Début et Ctrl+Maj+Fin pour aller d'un bout à l'autre. Une
  glissière paraît sur le bord droit dès que l'on remonte, et toute frappe
  ramène en bas
- Sélection à la souris : glisser pour sélectionner, double-clic pour un mot —
  chemins et adresses compris —, triple-clic pour une ligne. Ctrl+Maj+C copie
  la sélection, ou tout le terminal à défaut ; clic du milieu pour coller
- Recherche dans l'historique par Ctrl+Maj+R : les occurrences sont surlignées,
  Entrée et Maj+Entrée passent de l'une à l'autre, Échap referme
- Zoom par Ctrl+molette, Ctrl+Maj+Plus et Ctrl+Maj+Moins ; Ctrl+Maj+0 revient à
  la taille d'origine. La taille choisie vaut pour les terminaux suivants
- Plein écran par Ctrl+Maj+F, comme le bureau à distance

**Fichiers**
- Navigation par fil d'Ariane cliquable, pastilles par type de fichier
- Vue double volet, retenue d'une session à l'autre : ce poste à gauche, la
  machine à droite. On glisse d'un volet à l'autre, ou l'on passe par les
  boutons → et ← ; un téléchargement arrive alors directement dans le dossier
  local affiché, sans boîte de dialogue
- Envoi avec fenêtre de fragments en vol, le fichier étant lu au fil de l'eau :
  ni plafond de taille, ni chargement complet en mémoire
- Glisser-déposer depuis l'Explorateur, envoi et téléchargement de plusieurs
  fichiers à la fois : les transferts s'enchaînent dans une file d'attente,
  avec débit et temps restant. Chaque ligne s'annule ou se relance seule ; les
  doublons locaux sont numérotés plutôt que remplacés
- Les dossiers s'envoient et se téléchargent avec toute leur arborescence. Le
  protocole ne transfère que des fichiers : les sous-dossiers sont recréés un à
  un, chaque création étant confirmée par une relecture du dossier
- Envoi vers plusieurs machines à la fois (menu contextuel, palette, bouton
  « Envoyer des fichiers », ou fichiers glissés sur une machine de la liste) :
  une session par machine, quatre en parallèle, une destination par famille de
  système, et une relance qui reprend après les fichiers déjà reçus
- Création de dossier, renommage, suppression

**Administration**
- Alimentation : réveil Wake-on-LAN, redémarrage, veille, extinction
- Liste et arrêt des processus distants
- Informations système : processeur, mémoire, disques, BIOS
- Ouverture d'une URL sur la machine distante
- Renommage d'un appareil (F2, menu contextuel ou palette) : le nom affiché
  dans MeshCentral change pour tous les utilisateurs, le nom réseau de la
  machine reste le sien
- Génération de liens de partage publics temporaires, avec code d'intégration

**Application**
- Sessions regroupées en onglets : Ctrl+Tab et Ctrl+Maj+Tab pour circuler,
  Ctrl+1 à Ctrl+9 pour atteindre une session, Ctrl+F4 ou clic du milieu pour la
  fermer. Ces raccourcis ne sont captés qu'à partir de deux sessions ouvertes :
  seuls, ils gardent tout leur sens sur la machine distante
- Page web MeshCentral d'une machine par Ctrl+Maj+W, où que l'on se trouve :
  sur la machine sélectionnée dans la liste, sur celle en surbrillance dans la
  palette de recherche rapide, et sur celle affichée par une session de bureau
  à distance
- Code à deux facteurs demandé une seule fois : le poste est ensuite mémorisé
  auprès du serveur, pour la durée qu'il autorise ou sans limite au moyen d'un
  jeton de connexion
- Fonctionnement dans la zone de notification, démarrage automatique possible
- Reconnexion automatique après coupure réseau. Le canal de contrôle est
  surveillé : un websocket resté ouvert mais muet — veille du poste, coupure
  Wi-Fi, proxy qui oublie la session — est repéré en moins de deux minutes.
  L'inventaire est relu à la sortie de veille, au retour du réseau, au retour
  dans la fenêtre et toutes les cinq minutes : l'état des machines ne peut plus
  rester figé
- Journal d'erreurs pour le diagnostic

## Compilation

Nécessite le [SDK .NET 10](https://dotnet.microsoft.com/download).

```bash
dotnet build MeshControl.csproj -c Release
```

## Publication

Produit une application autonome — aucun prérequis .NET sur les postes cibles —
puis l'empaquette pour Velopack :

```powershell
.\publish.ps1            # publie et empaquette
.\publish.ps1 -Upload    # envoie aussi la release, en brouillon
```

Les fichiers de l'application vont dans `dist/`, les paquets dans `Releases/` :
`Setup.exe` pour une première installation, et un paquet delta qui ne contient
que les différences avec la version précédente — quelques centaines de
kilooctets, là où l'installeur complet en fait 67 mégaoctets.

Les releases sont publiées sur
[mesh-control-releases](https://github.com/dch-builds/mesh-control-releases), un
dépôt public d'où l'application se met à jour seule. L'installation ne réclame
aucun droit administrateur : elle se fait dans le profil de l'utilisateur.

> Windows affiche un avertissement SmartScreen au premier lancement,
> l'exécutable n'étant pas signé numériquement. Cliquer sur « Informations
> complémentaires » puis « Exécuter quand même ».

## Configuration

L'adresse du serveur se règle au premier lancement ou dans le volet des
réglages. Les identifiants sont chiffrés par DPAPI — liés à la session Windows
de l'utilisateur — et stockés dans `%APPDATA%\Mesh Control\`.

Lorsque le serveur impose un second facteur, le code n'est demandé qu'une fois :
l'application obtient du serveur un cookie d'appareil de confiance, valable
`twoFactorCookieDurationDays` jours — 30 par défaut. Le volet des réglages
permet d'aller plus loin et de créer un **jeton de connexion** propre au poste,
qui n'expire pas : c'est ce qu'il faut pour un démarrage automatique et des
reconnexions sans intervention. Le jeton se révoque depuis l'application, ou
depuis « Mon compte » sur le serveur ; se déconnecter le retire également.

Certaines fonctions dépendent de la configuration du serveur MeshCentral :

| Fonction | Prérequis serveur |
|---|---|
| Terminal SSH | `"ssh": true` dans la section domaine |
| Presse-papiers | `clipboardget` / `clipboardset` non désactivés |
| Processus, informations système | droit « Console » sur le groupe d'appareils |
| Intégration en iframe | `allowFraming` et `allowedFramingOrigins` |
| Poste mémorisé pour le second facteur | `twoFactorCookieDurationDays` non nul |
| Jeton de connexion | `passwordRequirements.loginTokens` non désactivé |

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
