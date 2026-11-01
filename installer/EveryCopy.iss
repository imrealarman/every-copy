; Inno Setup script for EveryCopy. Build with:  ISCC.exe installer\EveryCopy.iss
; Expects the self-contained publish output in ..\publish (see build-installer.ps1).

#define AppName "EveryCopy"
#define AppVersion "1.0.0"
#define AppExe "EveryCopy.exe"

[Setup]
AppId={{6A1F3C52-9D47-4B8E-A2C0-5E7B14D93F68}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Arman Soltani
AppContact=imrealarman@gmail.com
AppCopyright=Copyright (C) 2026 Arman Soltani
VersionInfoCompany=Arman Soltani
VersionInfoDescription=EveryCopy Setup
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
; Installs for the current user without admin rights; the wizard offers "all users" if the user wants it.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
SetupIconFile=..\Assets\AppIcon.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
OutputDir=Output
OutputBaseFilename=EveryCopy-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"
Name: "contextmenu"; Description: "Add Copy / Move / Paste with EveryCopy to the Explorer right-click menu"; GroupDescription: "Explorer:"

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; IconFilename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; IconFilename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
; Registers the right-click entries for the installing user (HKCU, so no admin rights needed).
Filename: "{app}\{#AppExe}"; Parameters: "--install-menu"; Flags: runhidden waituntilterminated; Tasks: contextmenu
Filename: "{app}\{#AppExe}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{app}\{#AppExe}"; Parameters: "--uninstall-menu"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveExplorerMenu"

; Settings and the log (%AppData%\EveryCopy) are deliberately left in place on uninstall.
