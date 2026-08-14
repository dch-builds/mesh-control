; Installeur Mesh Control — Inno Setup
;
; Prérequis : exécuter d'abord ..\publish.ps1 pour produire le dossier ..\dist
; Compilation : ouvrir ce fichier dans Inno Setup Compiler, ou en ligne de commande :
;   & "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" installer\MeshControl.iss

#define AppName "Mesh Control"
#define AppVersion "1.0.0"
#define AppPublisher "Mesh Control"
#define AppExeName "Mesh Control.exe"
; Identifiant unique de l'application : ne JAMAIS le modifier entre deux versions,
; sinon Windows considérerait qu'il s'agit d'un logiciel différent et les mises à
; jour s'installeraient en double.
#define AppId "{{8F3A6C21-4B7E-4D19-9E52-2C1A7F0B3D84}"

[Setup]
AppId={#AppId}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
; Installation pour tous les utilisateurs du poste (nécessite l'élévation).
; Passer à "lowest" + {localappdata} pour une installation sans droits admin.
PrivilegesRequired=admin
OutputDir=..\installer\output
OutputBaseFilename=MeshControl-Setup-{#AppVersion}
SetupIconFile=..\app.ico
UninstallDisplayIcon={app}\{#AppExeName}
; Compression maximale : le dossier publié pèse ~150 Mo, l'installeur descend
; nettement en dessous.
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; L'application est compilée pour x64 uniquement.
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "french"; MessagesFile: "compiler:Languages\French.isl"

[Tasks]
Name: "desktopicon"; Description: "Créer un raccourci sur le Bureau"; \
    GroupDescription: "Raccourcis :"
Name: "startup"; Description: "Lancer {#AppName} au démarrage de Windows (dans la zone de notification)"; \
    GroupDescription: "Options :"; Flags: unchecked

[Files]
; Tout le contenu du dossier publié. recursesubdirs prend le sous-dossier "fr".
Source: "..\dist\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\Désinstaller {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Registry]
; Démarrage automatique, si la tâche correspondante est cochée. L'argument
; --minimized fait démarrer l'application directement dans la zone de notification.
; Clé HKCU : propre à l'utilisateur qui installe, et sans droits supplémentaires.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; \
    ValueType: string; ValueName: "{#AppName}"; \
    ValueData: """{app}\{#AppExeName}"" --minimized"; \
    Flags: uninsdeletevalue; Tasks: startup

[Run]
Filename: "{app}\{#AppExeName}"; \
    Description: "Lancer {#AppName}"; \
    Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Les journaux sont créés à l'exécution : Inno Setup ne les connaît pas et les
; laisserait derrière lui.
Type: filesandordirs; Name: "{localappdata}\Mesh Control\logs"

[Code]
// Empêche l'installation par-dessus une instance en cours d'exécution : les
// fichiers seraient verrouillés et la mise à jour échouerait à mi-parcours.
function InitializeSetup(): Boolean;
var
  ResultCode: Integer;
begin
  Result := True;
  if Exec('taskkill.exe', '/IM "Mesh Control.exe" /F', '', SW_HIDE,
          ewWaitUntilTerminated, ResultCode) then
  begin
    // Laisser Windows libérer les fichiers avant de copier.
    Sleep(800);
  end;
end;

// Même précaution avant la désinstallation.
function InitializeUninstall(): Boolean;
var
  ResultCode: Integer;
begin
  Result := True;
  Exec('taskkill.exe', '/IM "Mesh Control.exe" /F', '', SW_HIDE,
       ewWaitUntilTerminated, ResultCode);
  Sleep(800);
end;
