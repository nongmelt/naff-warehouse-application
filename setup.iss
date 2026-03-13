#define MyAppName      "Warehouse"
#define MyAppVersion   "1.1"
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

[Dirs]
Name: "{app}\Scripts"; Permissions: users-modify

[Files]
Source: "{#MyAppDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "app\Scripts\cleanup_videos.ps1"; DestDir: "{app}\Scripts"; Flags: ignoreversion
Source: "app\Scripts\cleanup_videos.bat"; DestDir: "{app}\Scripts"; Flags: ignoreversion

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

  // Webhook page
  WebhookPage: TWizardPage;
  WebhookEdit: TEdit;

  // Video folder page
  VideoPage:   TWizardPage;
  VideoEdit:   TEdit;
  VideoBrowse: TButton;

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
  // ── Backend API page ───────────────────────────────────────────────────────
  ApiPage := CreateCustomPage(wpWelcome, 'Backend API',
    'Enter the base URL of the backend service that this app connects to.');

  AddLabel(ApiPage, 'API URL (e.g. http://192.168.1.10:8080):', 0);
  ApiUrlEdit := AddEdit(ApiPage, 20, 'http://localhost:8080', False);

  // ── Webhook page ───────────────────────────────────────────────────────────
  WebhookPage := CreateCustomPage(ApiPage.ID, 'Webhook Configuration',
    'Enter the n8n webhook URL that receives completed recording notifications.');

  AddLabel(WebhookPage, 'Webhook URL:', 0);
  WebhookEdit := AddEdit(WebhookPage, 20, 'http://', False);

  // ── Video folder page ──────────────────────────────────────────────────────
  VideoPage := CreateCustomPage(WebhookPage.ID, 'Video Storage',
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

  AddLabel(MinioPage, 'Endpoint URL (e.g. http://192.168.1.191:9000):', 132);
  EndpointEdit := AddEdit(MinioPage, 148, 'http://', False);
end;

// ── Validation ────────────────────────────────────────────────────────────────

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;

  if CurPageID = ApiPage.ID then
  begin
    if (Trim(ApiUrlEdit.Text) = '') or (Trim(ApiUrlEdit.Text) = 'http://') then
    begin
      MsgBox('Please enter the backend API URL.', mbError, MB_OK);
      Result := False; Exit;
    end;
  end;

  if CurPageID = WebhookPage.ID then
  begin
    if (Trim(WebhookEdit.Text) = '') or (Trim(WebhookEdit.Text) = 'http://') then
    begin
      MsgBox('Please enter the webhook URL.', mbError, MB_OK);
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

// ── Download rclone during install ────────────────────────────────────────────

procedure DownloadRclone(AppDir: string);
var
  PsFile, Script: string;
  ResultCode: Integer;
begin
  WizardForm.StatusLabel.Caption   := 'Downloading rclone (required for video sync)...';
  WizardForm.FilenameLabel.Caption := 'Connecting to downloads.rclone.org — this may take a minute.';

  PsFile := ExpandConstant('{tmp}\dl_rclone.ps1');
  Script :=
    '$zip  = Join-Path $env:TEMP "rclone.zip"' + #13#10 +
    '$dir  = Join-Path $env:TEMP "rclone_extracted"' + #13#10 +
    '$dest = "' + AppDir + '\Scripts\rclone.exe"' + #13#10 +
    'Invoke-WebRequest -Uri "https://downloads.rclone.org/rclone-current-windows-amd64.zip" -OutFile $zip -UseBasicParsing' + #13#10 +
    'Expand-Archive -Path $zip -DestinationPath $dir -Force' + #13#10 +
    '$exe = Get-ChildItem $dir -Filter rclone.exe -Recurse | Select-Object -First 1' + #13#10 +
    'Copy-Item $exe.FullName $dest -Force' + #13#10 +
    'Remove-Item $zip, $dir -Recurse -Force -ErrorAction SilentlyContinue';
  SaveStringToFile(PsFile, Script, False);

  Exec('powershell.exe',
    '-ExecutionPolicy Bypass -NonInteractive -File "' + PsFile + '"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  WizardForm.ProgressGauge.Position := WizardForm.ProgressGauge.Max;

  if ResultCode <> 0 then
  begin
    WizardForm.StatusLabel.Caption   := 'rclone download failed — MinIO sync will not work.';
    WizardForm.FilenameLabel.Caption := AppDir + '\Scripts\rclone.exe must be placed manually.';
    MsgBox('Warning: rclone could not be downloaded.' + #13#10 +
           'MinIO sync will not work until rclone.exe is placed in:' + #13#10 +
           AppDir + '\Scripts\', mbError, MB_OK);
  end
  else
  begin
    WizardForm.StatusLabel.Caption   := 'rclone downloaded successfully.';
    WizardForm.FilenameLabel.Caption := AppDir + '\Scripts\rclone.exe';
  end;
end;

// ── Write config files after install ─────────────────────────────────────────

procedure CurStepChanged(CurStep: TSetupStep);
var
  AppDir, VideoFolder, Bucket: string;
  Json, Conf, Bat: string;
begin
  if CurStep <> ssPostInstall then Exit;

  AppDir      := ExpandConstant('{app}');
  VideoFolder := Trim(VideoEdit.Text);
  Bucket      := Trim(BucketEdit.Text);

  ForceDirectories(AppDir + '\Scripts');

  // ── appsettings.json ───────────────────────────────────────────────────────
  Json :=
    '{' + #13#10 +
    '  "webhookUrl": "' + EscapeJson(Trim(WebhookEdit.Text))      + '",' + #13#10 +
    '  "videoFolder": "' + EscapeJson(VideoFolder)                + '",' + #13#10 +
    '  "apiUrl": "'      + EscapeJson(Trim(ApiUrlEdit.Text))      + '",' + #13#10 +
    '  "minio": {' + #13#10 +
    '    "bucket": "'    + EscapeJson(Bucket)                     + '",' + #13#10 +
    '    "accessKey": "' + EscapeJson(Trim(AccessKeyEdit.Text))   + '",' + #13#10 +
    '    "secretKey": "' + EscapeJson(Trim(SecretKeyEdit.Text))   + '",' + #13#10 +
    '    "endpoint": "'  + EscapeJson(Trim(EndpointEdit.Text))    + '"'  + #13#10 +
    '  }' + #13#10 +
    '}';
  SaveStringToFile(AppDir + '\appsettings.json', Json, False);

  // ── rclone.conf ────────────────────────────────────────────────────────────
  Conf :=
    '[minio]' + #13#10 +
    'type = s3' + #13#10 +
    'provider = Minio' + #13#10 +
    'access_key_id = '     + Trim(AccessKeyEdit.Text) + #13#10 +
    'secret_access_key = ' + Trim(SecretKeyEdit.Text) + #13#10 +
    'endpoint = '          + Trim(EndpointEdit.Text)  + #13#10 +
    'acl = private' + #13#10;
  SaveStringToFile(AppDir + '\Scripts\rclone.conf', Conf, False);

  // ── sync_to_minio.bat ──────────────────────────────────────────────────────
  Bat :=
    '@echo off' + #13#10#13#10 +
    ':: --- CONFIGURATION ---' + #13#10 +
    'set LOCAL_VIDEO_FOLDER=' + VideoFolder + #13#10 +
    'set MINIO_BUCKET=' + Bucket + #13#10 +
    'set RCLONE_CONFIG=' + AppDir + '\Scripts\rclone.conf' + #13#10 +
    'set RCLONE_EXE=' + AppDir + '\Scripts\rclone.exe' + #13#10#13#10 +
    'for /f "tokens=2" %%D in ("%date%") do set DATE_CLEAN=%%D' + #13#10 +
    'set DATE_CLEAN=%DATE_CLEAN:/=-%' + #13#10#13#10 +
    'set LOG_DIR=' + AppDir + '\Scripts\logs\sync' + #13#10 +
    'set LOG_FILE=%LOG_DIR%\%COMPUTERNAME%_%DATE_CLEAN%.log' + #13#10#13#10 +
    'echo Starting MinIO sync for %COMPUTERNAME% at [%date% %time%]...' + #13#10 +
    'echo From folder: %LOCAL_VIDEO_FOLDER% to bucket: %MINIO_BUCKET%/%COMPUTERNAME%/' + #13#10#13#10 +
    'if not exist "%LOG_DIR%" mkdir "%LOG_DIR%"' + #13#10#13#10 +
    'echo [%date% %time%] Starting upload for %COMPUTERNAME% >> "%LOG_FILE%"' + #13#10#13#10 +
    '"%RCLONE_EXE%" copy "%LOCAL_VIDEO_FOLDER%" minio:%MINIO_BUCKET%/%COMPUTERNAME%/ ^' + #13#10 +
    '  --config "%RCLONE_CONFIG%" ^' + #13#10 +
    '  --min-age 1h ^' + #13#10 +
    '  --transfers 4 ^' + #13#10 +
    '  --checkers 8 ^' + #13#10 +
    '  --log-file "%LOG_FILE%" ^' + #13#10 +
    '  --log-level INFO ^' + #13#10 +
    '  --stats 30s ^' + #13#10 +
    '  --progress ^' + #13#10 +
    '  2>&1' + #13#10#13#10 +
    'if %ERRORLEVEL% NEQ 0 (' + #13#10 +
    '    echo [ERROR] rclone failed with exit code %ERRORLEVEL%' + #13#10 +
    '    echo [%date% %time%] [ERROR] Upload failed with exit code %ERRORLEVEL% >> "%LOG_FILE%"' + #13#10 +
    ') else (' + #13#10 +
    '    echo [OK] Upload complete.' + #13#10 +
    '    echo [%date% %time%] Upload complete >> "%LOG_FILE%"' + #13#10 +
    ')' + #13#10;
  SaveStringToFile(AppDir + '\Scripts\sync_to_minio.bat', Bat, False);

  // ── rclone.exe ─────────────────────────────────────────────────────────────
  DownloadRclone(AppDir);
end;
