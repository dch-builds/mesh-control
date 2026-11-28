# Mesh Control — consignes de travail

## Deux interfaces, une seule application

Mesh Control existe en deux interfaces qui doivent rester identiques, en
fonctions comme en apparence :

- **WPF** (version Windows) : `UI/`, `Desktop/`, `Files/`, `SSH/`, projet `MeshControl.csproj`.
- **Avalonia** (version macOS, qui tourne aussi sous Windows) : `MeshControl.Mac/`.

Le cœur commun (connexion, sessions, protocole, modèles) est dans `Core/` et
dans les fichiers liés par `MeshControl.Core/MeshControl.Core.csproj`.

**Règle : toute fonction ajoutée ou modifiée dans l'une des deux interfaces
l'est aussi dans l'autre, dans le même lot de travail.** Cela vaut pour les
fenêtres, menus, raccourcis, réglages, messages et comportements.

- Mettre la logique dans le cœur partagé chaque fois que c'est possible, pour
  ne l'écrire qu'une fois.
- Si une fonction ne peut pas exister sur l'une des plateformes (API propre à
  Windows ou à macOS), prévoir son équivalent le plus proche et le signaler.
- Si le portage vers l'autre interface ne peut pas être fait tout de suite, le
  dire explicitement à l'utilisateur à la fin du travail, avec ce qui reste à faire.
