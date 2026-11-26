# Mesh Control

*[English](README.md) · Français*

Application Windows native pour piloter un serveur
[MeshCentral](https://meshcentral.com) : bureau à distance, terminal, transfert
de fichiers et actions d'alimentation.

Écrite en C# / WPF sur .NET 10. Seule dépendance externe :
[Velopack](https://github.com/velopack/velopack), pour l'installation et les
mises à jour.

## Fonctionnalités

**Appareils**
- Liste groupée par dossier, repliable, avec état en ligne en temps réel
- Recherche instantanée (nom, dossier, système, adresse IP, utilisateur
  MeshCentral connecté), par Ctrl+F depuis toute la fenêtre ou en tapant
  directement dans la liste
- Filtres rapides en pastilles, avec leur compteur, une par catégorie et
  cumulables d'une catégorie à l'autre (Windows + en ligne) : état (en ligne,
  hors ligne, en session), favoris ou récents, système (Windows, Linux,
  macOS) ; les compteurs suivent le dossier choisi, la recherche en cours et
  les autres pastilles actives
- Volet des dossiers à gauche, au choix : compteur en ligne / total par
  dossier (répondant aux filtres / total quand des pastilles sont actives),
  champ de filtre, largeur réglable, dossiers épinglés en tête ou
  masqués ; clic droit sur un dossier pour réveiller ses machines, leur
  envoyer des fichiers ou être prévenu de leur retour
- Sélection multiple (Ctrl+clic, Maj+clic, Ctrl+A) et actions groupées :
  réveiller, redémarrer, éteindre, envoyer des fichiers, mettre en favoris
- « Me prévenir à son retour » : une notification Windows dès qu'une machine
  hors ligne se reconnecte ; un clic dessus ouvre le bureau à distance
- Clavier dans la liste : Entrée ouvre le bureau à distance, Ctrl+T le
  terminal, Ctrl+E les fichiers
- Tri retenu d'une session à l'autre : par nom, en ligne d'abord, dernière
  présence ou système, à l'intérieur de chaque dossier
- Chaque ligne montre l'adresse IP et l'utilisateur MeshCentral connecté à la
  machine (« vous » pour ses propres sessions, détail par type de session en
  info-bulle) ; au survol, bureau, terminal et fichiers en un clic, ou le
  réveil d'une machine hors ligne
- Thème clair ou sombre pour toute l'application, au choix dans les réglages
  (appliqué au redémarrage, proposé d'un clic)
- Favoris épinglés et machines récemment utilisées
- Palette de recherche rapide par raccourci clavier global, utilisable même
  lorsque l'application est en arrière-plan ; elle s'ouvre sur les dernières
  connexions

**Bureau à distance**
- Décodage natif du protocole KVM (tuiles JPEG, trames jumbo)
- Cadence réglable de 5 à 40 images/s, trois niveaux de qualité
- Mode dépannage pour les liaisons très lentes : compression maximale,
  5 images/s au plus, image ramenée à environ 1 280 pixels de large et écran
  unique sur un poste à plusieurs moniteurs. « Image nette » (Ctrl+Maj+R)
  envoie une fois l'écran en bonne qualité : le texte reste lisible tant qu'il
  ne change pas. Quand la liaison sature, un bandeau propose de passer dans ce
  mode
- Mode fluide pour suivre une vidéo : 40 images/s en bonne qualité, sur une
  bonne connexion
- Souris, molette, boutons latéraux et clavier complet (codes VK Windows). La
  touche Windows n'est pas transmise (le poste local l'intercepte de toute
  façon) et s'envoie par le menu « Raccourcis »
- Raccourcis prédéfinis adaptés à Windows ou Linux
- Raccourcis locaux : Ctrl+Maj+R redemande une image complète, Ctrl+Maj+A
  bascule l'ajustement à la fenêtre, Ctrl+Maj+D débloque le clavier distant et
  Ctrl+Maj+Suppr envoie Ctrl+Alt+Suppr
- Autres outils de la machine sans quitter le bureau : terminal (Ctrl+Maj+T),
  fichiers (Ctrl+Maj+E), processus (Ctrl+Maj+P), page web (Ctrl+Maj+W)
- Plein écran par Ctrl+Maj+F : barres et bandeau d'onglets masqués. La souris
  portée en haut de l'écran fait descendre une barre flottante : coller, taper,
  synchronisation du presse-papiers, raccourcis, Ctrl+Alt+Suppr, vue seule,
  capture et sortie du plein écran
- Ouverture des sessions au choix dans les réglages : fenêtre habituelle,
  agrandie ou plein écran
- Vue seule (bouton œil ou Ctrl+Maj+O) : l'image reste à jour mais ni souris ni
  clavier ne partent vers la machine ; un badge le rappelle sur l'écran
- Barre d'outils qui se resserre sur une fenêtre étroite : libellés réduits à
  leur icône, puis outils secondaires repliés dans le menu « ⋯ »
- Loupe (bouton ou Ctrl+Maj+Z) : on trace un rectangle sur l'écran distant et
  la zone remplit la fenêtre, la souris restant utilisable. Le même geste
  revient à la vue entière ; Maj+clic sur la loupe agrandit encore
- Redémarrage, veille ou extinction depuis la barre d'outils (redémarrage :
  Ctrl+Maj+B). Après un redémarrage, la session reprend d'elle-même au retour
  de la machine, même au-delà de la reprise automatique
- Fichiers et dossiers glissés depuis l'Explorateur sur l'écran distant : une
  boîte demande le dossier d'arrivée et propose les emplacements courants de
  la machine : bureau (OneDrive compris), téléchargements et documents de
  chaque utilisateur, bureau public, disques ; sous Linux, les dossiers
  personnels, `/tmp` et `/`. « Parcourir… » permet de naviguer dans les
  dossiers de la machine pour en choisir un autre ; le chemin reste aussi
  modifiable à la main, et le dernier dossier choisi est reproposé.
  Progression dans la barre d'état
- Le flux s'interrompt dès que la session n'est plus regardée (onglet passé à
  l'arrière-plan, fenêtre réduite) et reprend sur une image complète
- Presse-papiers dans les deux sens. Le collage écrit le presse-papiers de la
  machine distante puis envoie Ctrl+V : instantané, quelle que soit la longueur
  du texte. Si le serveur refuse l'écriture (`clipboardset`) ou si l'agent ne la
  confirme pas, la saisie caractère par caractère prend le relais ; elle reste
  accessible à la demande (bouton « Taper » ou Ctrl+Maj+Alt+V), pour les
  applications qui ignorent Ctrl+V
- Synchronisation automatique du presse-papiers (bouton ⇄, désactivée par
  défaut) : ce qui est copié d'un côté est disponible de l'autre, en texte

**Terminal**
- Via l'agent : invite de commandes, PowerShell, shell utilisateur, et sur Linux
  un *login shell* demandant identifiant et mot de passe
- Via SSH lorsque le serveur l'autorise
- Émulateur ANSI/VT100 intégré (couleurs, curseur, zones de défilement)
- Historique de 5 000 lignes : molette, Maj+Page précédente et Maj+Page
  suivante, Ctrl+Maj+Début et Ctrl+Maj+Fin pour aller d'un bout à l'autre. Une
  glissière paraît sur le bord droit dès que l'on remonte, et toute frappe
  ramène en bas
- Sélection à la souris : glisser pour sélectionner, double-clic pour un mot
  (chemins et adresses compris), triple-clic pour une ligne. Ctrl+C copie la
  sélection (sans sélection, il interrompt la commande), Ctrl+Maj+C copie la
  sélection ou tout le terminal ; clic du milieu pour coller
- Clic droit : copier, coller, tout copier, rechercher, effacer, taille du
  texte, reconnecter
- Collage sûr : si le shell gère le collage protégé (bash, zsh, PSReadLine),
  le texte lui arrive d'un bloc et rien ne s'exécute avant Entrée ; sinon, un
  collage de plusieurs lignes est confirmé d'abord
- Session fermée : Entrée la rouvre. Les messages de l'agent (attente de
  l'accord de l'utilisateur, refus) s'affichent dans le terminal, et un
  terminal resté muet le signale au bout de quelques secondes
- Menu « Commandes » (Ctrl+Maj+K), écrit pour le shell ouvert (cmd,
  PowerShell ou Linux) : dossiers courants (bureau, téléchargements, documents,
  AppData de l'utilisateur connecté, même depuis un shell SYSTEM et avec
  OneDrive), informations (utilisateurs, disques, processus, services,
  logiciels, mises à jour, erreurs système), réseau et maintenance (SFC, DISM,
  spouleur, file d'impression, redémarrage programmé). Ce qui modifie la
  machine est seulement tapé, à valider par Entrée
- Commandes personnelles dans ce même menu, en tête : libellé, shell visé
  (tous, cmd, PowerShell, Linux), exécution directe ou simple frappe, plusieurs
  lignes possibles. « Ajouter la sélection » reprend le texte sélectionné dans
  le terminal. Enregistrées dans les réglages de l'utilisateur
- Répond aux requêtes du shell (position du curseur, identification) : la
  console Windows en a besoin pour s'afficher sur certains postes
- Recherche dans l'historique par Ctrl+Maj+R : les occurrences sont surlignées,
  Entrée et Maj+Entrée passent de l'une à l'autre, Échap referme
- Zoom par Ctrl+molette, Ctrl+Maj+Plus et Ctrl+Maj+Moins ; Ctrl+Maj+0 revient à
  la taille d'origine. La taille choisie vaut pour les terminaux suivants
- Plein écran par Ctrl+Maj+F, comme le bureau à distance
- Bureau à distance sans quitter le terminal (bouton « Bureau », Ctrl+Maj+D) :
  en vignette ancrée en bas à droite, pour suivre l'écran en tapant, ou dans un
  onglet. Le clavier va au terminal ou au bureau selon le dernier clic (cadre
  bleu quand c'est le bureau). La vignette se redimensionne par son coin
  supérieur gauche, se réduit à son en-tête (le flux d'images est alors
  suspendu), et passe en onglet sans couper la connexion

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
  seuls, ils gardent tout leur sens sur la machine distante. Quand les onglets
  débordent, le bandeau défile (molette ou flèches) et l'onglet actif reste
  visible
- Page web MeshCentral d'une machine par Ctrl+Maj+W, où que l'on se trouve :
  sur la machine sélectionnée dans la liste, sur celle en surbrillance dans la
  palette de recherche rapide, et sur celle affichée par une session de bureau
  à distance
- Code à deux facteurs demandé une seule fois : le poste est ensuite mémorisé
  auprès du serveur, pour la durée qu'il autorise ou sans limite au moyen d'un
  jeton de connexion
- Fonctionnement dans la zone de notification, démarrage automatique possible
- Reconnexion automatique après coupure réseau. Le canal de contrôle est
  surveillé : un websocket resté ouvert mais muet (veille du poste, coupure
  Wi-Fi, proxy qui oublie la session) est repéré en moins de deux minutes.
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

Produit une application autonome (aucun prérequis .NET sur les postes cibles),
puis l'empaquette pour Velopack :

```powershell
.\publish.ps1            # publie et empaquette
.\publish.ps1 -Upload    # envoie aussi la release, en brouillon
```

Les fichiers de l'application vont dans `dist/`, les paquets dans `Releases/` :
`Setup.exe` pour une première installation, et un paquet delta qui ne contient
que les différences avec la version précédente : quelques centaines de
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
réglages. Les identifiants sont chiffrés par DPAPI (liés à la session Windows
de l'utilisateur) et stockés dans `%APPDATA%\Mesh Control\`.

Lorsque le serveur impose un second facteur, le code n'est demandé qu'une fois :
l'application obtient du serveur un cookie d'appareil de confiance, valable
`twoFactorCookieDurationDays` jours (30 par défaut). Le volet des réglages
permet d'aller plus loin et de créer un **jeton de connexion** propre au poste,
qui n'expire pas : c'est ce qu'il faut pour un démarrage automatique et des
reconnexions sans intervention. Le jeton se révoque depuis l'application, ou
depuis « Mon compte » sur le serveur ; se déconnecter le retire également.

Avec un certificat auto-signé, l'option correspondante retient le certificat
présenté à la première connexion et refuse ensuite tout certificat différent :
un intermédiaire sur le réseau ne peut pas se faire passer pour le serveur.

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

## Licence

Mesh Control est distribué sous [licence MIT](LICENSE). Les composants tiers
fournis avec l'application sont listés dans
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## Mentions

Mesh Control est un client indépendant pour MeshCentral. Il n'est ni affilié ni
approuvé par le projet MeshCentral. MeshCentral est © Intel Corporation, créé
par Ylian Saint-Hilaire, sous licence Apache 2.0. Cette application ne contient
aucun code MeshCentral : elle communique avec les serveurs via leurs interfaces
publiées.

Windows est une marque du groupe Microsoft. Linux® est une marque déposée de
Linus Torvalds. Les autres marques appartiennent à leurs propriétaires
respectifs.

## Un mot sur l'IA

Une partie de ce code a été écrite avec l'aide d'un assistant d'intelligence
artificielle (Claude, d'Anthropic) ; les commits concernés le mentionnent. Je
préfère le dire clairement.

Il n'a pas été généré à l'aveugle. J'ai conçu les fonctions, décidé de chaque
modification et testé l'application sur les machines réelles que je gère avec
elle au quotidien. Le code est aussi passé par un audit de sécurité dédié, dont
les points relevés ont été corrigés dans la version 1.2.15. Tout est ici, à
découvert : vous pouvez le lire, le compiler vous-même et vérifier que les
binaires publiés y correspondent.

Si le code écrit avec l'aide d'une IA vous pose problème, je le comprends tout
à fait : sans rancune, passez votre chemin. Pour tous les autres, retours,
signalements de bugs et suggestions sont les bienvenus.
