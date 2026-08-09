#define MyAppName      "Warehouse"
#define MyAppVersion   "1.4.7"
#define MyAppPublisher "NAF Stationery"
#define MyAppExeName   "Warehouse.exe"
#define MyAppDir       "app\bin\Release\net10.0-windows10.0.19041.0\win-x64\publish"

; ── Zero-touch enrollment values — baked per station via /D defines ───────────
; Per-station build (the dashboard mints the key + name and downloads them as
; JSON; CI/build passes them through as defines):
;   iscc /DStationKey=nffwh_stn_... /DStationName=PACKING-RAM07 /DApiUrl=http://192.168.1.2:8080 setup.iss
; Defaults below keep a no-define build compiling; real per-station builds pass all three.
#ifndef StationKey
  #define StationKey ""
#endif
#ifndef StationName
  #define StationName ""
#endif
; Default left empty so a build that forgets /DApiUrl produces an explicitly
; unconfigured appsettings.json (AppSettings skips seeding a blank apiUrl and
; falls back to its own DefaultApiUrl) rather than silently baking localhost.
#ifndef ApiUrl
  #define ApiUrl ""
#endif

[Setup]
AppId={{A3F2C1D0-8B4E-4F7A-9C6D-2E1B0A5F3D8C}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=installer
OutputBaseFilename=warehouse-{#MyAppVersion}-x86_64-setup
Compression=lzma
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional icons:"

[Files]
Source: "{#MyAppDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}";       Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{commondesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent

; ── Config file generation ───────────────────────────────────────────────────
; No custom wizard pages — zero-touch. The installer is Next -> Finish; the
; station key/name/apiUrl are baked from /D defines (see top of file). MinIO
; credentials are NOT baked: the app obtains them at first launch from
; POST /enroll using the baked station key.

[Code]

procedure CurStepChanged(CurStep: TSetupStep);
var
  Json: string;
begin
  if CurStep <> ssPostInstall then Exit;

  Json :=
    '{' + #13#10 +
    '  "stationKey": "{#StationKey}",' + #13#10 +
    '  "stationName": "{#StationName}",' + #13#10 +
    '  "apiUrl": "{#ApiUrl}"';

  // Online builds (Cloudflare Access sites) bake the CF service token too:
  //   iscc /DCfAccessClientId=... /DCfAccessClientSecret=... ... setup.iss
  // Bangkok/LAN builds omit the defines -> no CF fields -> AppSettings reads them blank.
#if defined(CfAccessClientId)
  Json := Json + ',' + #13#10 +
    '  "cfAccessClientId": "{#CfAccessClientId}",' + #13#10 +
    '  "cfAccessClientSecret": "{#CfAccessClientSecret}"';
#endif

  Json := Json + #13#10 + '}';

  SaveStringToFile(ExpandConstant('{app}') + '\appsettings.json', Json, False);
end;
