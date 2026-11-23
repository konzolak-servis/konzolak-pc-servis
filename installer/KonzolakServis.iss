; Inno Setup skript pro "Konzolak service app".
; Klasický instalátor: nainstaluje do Program Files, vytvoří zástupce na ploše i v nabídce Start,
; přidá položku do Přidat/Odebrat programy a umí tichou aktualizaci (/VERYSILENT).
; Aplikace běží jako správce (app.manifest), proto instalace per-machine do Program Files –
; aktualizace pak proběhne bez dalšího dotazu UAC (běžící aplikace je už spuštěná jako správce).
;
; Verze se předává na příkazové řádce: ISCC.exe /DMyAppVersion=0.25.0 installer\KonzolakServis.iss
; Zdrojový .exe se čeká v publish\KonzolakServis.exe (výstup z dotnet publish).

#ifndef MyAppVersion
  #define MyAppVersion "0.0.0"
#endif
#define MyAppName "Konzolak service app"
#define MyAppExe "KonzolakServis.exe"
#define MyAppPublisher "Konzolák"

[Setup]
; Stabilní AppId – podle něj se pozná stávající instalace a aktualizuje se na místě.
AppId={{8F3C2A91-5B7E-4D2A-9E13-K0NZ0LAKSERV}}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL=https://servis.konzolak.com
; Všechny relativní cesty (Source, ikona, výstup) se berou od kořene repozitáře (nadřazená složka tohoto .iss).
SourceDir={#SourcePath}\..
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
; Per-machine (Program Files) – aplikace stejně běží jako správce.
PrivilegesRequired=admin
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
OutputDir=installer_out
OutputBaseFilename=KonzolakServis_Setup_v{#MyAppVersion}
SetupIconFile=src\KonzolakServis\Assets\konzolak.ico
UninstallDisplayIcon={app}\{#MyAppExe}
UninstallDisplayName={#MyAppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; Při (tiché) aktualizaci zavři běžící aplikaci, ať jde soubor přepsat. Restart si řeší aplikace sama.
CloseApplications=yes
CloseApplicationsFilter=KonzolakServis.exe
RestartApplications=no

[Languages]
Name: "czech"; MessagesFile: "compiler:Languages\Czech.isl"

[Files]
Source: "publish\{#MyAppExe}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"

[Run]
; Po běžné (nemlčící) instalaci nabídni spuštění. Při tiché aktualizaci se přeskočí – aplikace se spustí sama.
Filename: "{app}\{#MyAppExe}"; Description: "Spustit {#MyAppName}"; Flags: nowait postinstall skipifsilent
