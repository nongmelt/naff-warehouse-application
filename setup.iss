#define MyAppName      "Warehouse"
#define MyAppVersion   "dev-1.4.2"
#define MyAppPublisher "NAF Stationery"
#define MyAppExeName   "Warehouse.exe"
#define MyAppDir       "app\bin\Release\net10.0-windows10.0.19041.0\win-x64\publish"

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

; ── Custom wizard pages & config file generation ─────────────────────────────

[Code]

var
  // Backend API page
  ApiPage:     TWizardPage;
  ApiUrlEdit:  TEdit;

  // Video folder page
  VideoPage:   TWizardPage;
  VideoEdit:   TEdit;
  VideoBrowse: TButton;

  // Station ID page
  StationPage:   TWizardPage;
  StationIdEdit: TEdit;

  // MinIO sync page
  MinioPage:     TWizardPage;
  BucketEdit:    TEdit;
  AccessKeyEdit: TEdit;
  SecretKeyEdit: TEdit;
  EndpointEdit:  TEdit;

// ── Helpers ──────────────────────────────────────────────────────────────────

function EscapeJson(const S: string): string;
var
  I: Integer;
  C: Char;
  R: string;
begin
  R := '';
  for I := 1 to Length(S) do
  begin
    C := S[I];
    if C = '"'  then R := R + '\"'
    else if C = '\' then R := R + '\\'
    else R := R + C;
  end;
  Result := R;
end;

procedure AddLabel(Page: TWizardPage; const Caption: string; Top: Integer);
var
  Lbl: TLabel;
begin
  Lbl := TLabel.Create(Page);
  Lbl.Parent  := Page.Surface;
  Lbl.Caption := Caption;
  Lbl.Top     := Top;
  Lbl.Left    := 0;
  Lbl.Width   := Page.SurfaceWidth;
end;

function AddEdit(Page: TWizardPage; Top: Integer; const Default: string; IsPassword: Boolean): TEdit;
var
  Ed: TEdit;
begin
  Ed := TEdit.Create(Page);
  Ed.Parent    := Page.Surface;
  Ed.Top       := Top;
  Ed.Left      := 0;
  Ed.Width     := Page.SurfaceWidth;
  Ed.Text      := Default;
  if IsPassword then Ed.PasswordChar := '*';
  Result := Ed;
end;

// ── Browse button handler (must be declared before InitializeWizard) ──────────

procedure OnVideoBrowseClick(Sender: TObject);
var
  Dir: string;
begin
  Dir := VideoEdit.Text;
  if BrowseForFolder('Select video destination folder', Dir, False) then
    VideoEdit.Text := Dir;
end;

// ── Page creation ─────────────────────────────────────────────────────────────

procedure InitializeWizard;
var
  Btn: TButton;
begin
  // ── Station ID page ────────────────────────────────────────────────────────
  StationPage := CreateCustomPage(wpWelcome, 'Station Identity',
    'Give this station a unique name used in logs and workflow event records.');

  AddLabel(StationPage, 'Station ID (e.g. Station-01, QC-02):', 0);
  StationIdEdit := AddEdit(StationPage, 20, '', False);

  // ── Backend API page ───────────────────────────────────────────────────────
  ApiPage := CreateCustomPage(StationPage.ID, 'Backend API',
    'Enter the base URL of the backend service that this app connects to.');

  AddLabel(ApiPage, 'API URL (e.g. http://192.168.0.1:8080):', 0);
  ApiUrlEdit := AddEdit(ApiPage, 20, 'http://localhost:8080', False);

  // ── Video folder page ──────────────────────────────────────────────────────
  VideoPage := CreateCustomPage(ApiPage.ID, 'Video Storage',
    'Choose where recorded videos will be saved on this machine.');

  AddLabel(VideoPage, 'Destination folder:', 0);
  VideoEdit := AddEdit(VideoPage, 20, ExpandConstant('{userdocs}\..\Videos\Warehouse'), False);

  Btn := TButton.Create(VideoPage);
  Btn.Parent  := VideoPage.Surface;
  Btn.Caption := 'Browse...';
  Btn.Top     := 48;
  Btn.Left    := 0;
  Btn.Width   := 100;
  Btn.OnClick := @OnVideoBrowseClick;
  VideoBrowse := Btn;

  // ── MinIO Sync Configuration page ─────────────────────────────────────────
  MinioPage := CreateCustomPage(VideoPage.ID, 'MinIO Sync Configuration',
    'Configure the MinIO storage connection for automatic video synchronization.');

  AddLabel(MinioPage, 'MinIO Bucket:', 0);
  BucketEdit := AddEdit(MinioPage, 16, '', False);

  AddLabel(MinioPage, 'Access Key ID:', 44);
  AccessKeyEdit := AddEdit(MinioPage, 60, '', False);

  AddLabel(MinioPage, 'Secret Access Key:', 88);
  SecretKeyEdit := AddEdit(MinioPage, 104, '', True);

  AddLabel(MinioPage, 'Endpoint URL (e.g. http://192.168.0.1:9000):', 132);
  EndpointEdit := AddEdit(MinioPage, 148, 'http://', False);
end;

// ── Validation ────────────────────────────────────────────────────────────────

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;

  if CurPageID = StationPage.ID then
  begin
    if Trim(StationIdEdit.Text) = '' then
    begin
      MsgBox('Please enter a Station ID.', mbError, MB_OK);
      Result := False; Exit;
    end;
  end;

  if CurPageID = ApiPage.ID then
  begin
    if (Trim(ApiUrlEdit.Text) = '') or (Trim(ApiUrlEdit.Text) = 'http://') then
    begin
      MsgBox('Please enter the backend API URL.', mbError, MB_OK);
      Result := False; Exit;
    end;
  end;

  if CurPageID = VideoPage.ID then
  begin
    if Trim(VideoEdit.Text) = '' then
    begin
      MsgBox('Please enter a video destination folder.', mbError, MB_OK);
      Result := False; Exit;
    end;
  end;

  if CurPageID = MinioPage.ID then
  begin
    if Trim(BucketEdit.Text) = '' then
    begin
      MsgBox('Please enter the MinIO bucket name.', mbError, MB_OK);
      Result := False; Exit;
    end;
    if Trim(AccessKeyEdit.Text) = '' then
    begin
      MsgBox('Please enter the MinIO Access Key ID.', mbError, MB_OK);
      Result := False; Exit;
    end;
    if Trim(SecretKeyEdit.Text) = '' then
    begin
      MsgBox('Please enter the MinIO Secret Access Key.', mbError, MB_OK);
      Result := False; Exit;
    end;
    if (Trim(EndpointEdit.Text) = '') or (Trim(EndpointEdit.Text) = 'http://') then
    begin
      MsgBox('Please enter the MinIO endpoint URL.', mbError, MB_OK);
      Result := False; Exit;
    end;
  end;
end;

// ── Write config files after install ─────────────────────────────────────────

procedure CurStepChanged(CurStep: TSetupStep);
var
  AppDir, VideoFolder: string;
  Json: string;
begin
  if CurStep <> ssPostInstall then Exit;

  AppDir      := ExpandConstant('{app}');
  VideoFolder := Trim(VideoEdit.Text);

  // ── appsettings.json ───────────────────────────────────────────────────────
  Json :=
    '{' + #13#10 +
    '  "stationId": "'    + EscapeJson(Trim(StationIdEdit.Text))  + '",' + #13#10 +
    '  "videoFolder": "' + EscapeJson(VideoFolder)                + '",' + #13#10 +
    '  "apiUrl": "'      + EscapeJson(Trim(ApiUrlEdit.Text))      + '",' + #13#10 +
    '  "minio": {' + #13#10 +
    '    "bucket": "'    + EscapeJson(Trim(BucketEdit.Text))      + '",' + #13#10 +
    '    "accessKey": "' + EscapeJson(Trim(AccessKeyEdit.Text))   + '",' + #13#10 +
    '    "secretKey": "' + EscapeJson(Trim(SecretKeyEdit.Text))   + '",' + #13#10 +
    '    "endpoint": "'  + EscapeJson(Trim(EndpointEdit.Text))    + '"'  + #13#10 +
    '  }' + #13#10 +
    '}';
  SaveStringToFile(AppDir + '\appsettings.json', Json, False);
end;
