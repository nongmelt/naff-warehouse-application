#define MyAppName      "Warehouse"
#define MyAppVersion   "1.1"
#define MyAppPublisher "NAF Stationery"
#define MyAppExeName   "app.exe"
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
OutputBaseFilename=WarehouseSetup
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

; ── Custom wizard pages & appsettings.json generation ───────────────────────

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
  VideoEdit := AddEdit(VideoPage, 20, 'C:\Videos\Warehouse', False);

  Btn := TButton.Create(VideoPage);
  Btn.Parent  := VideoPage.Surface;
  Btn.Caption := 'Browse...';
  Btn.Top     := 48;
  Btn.Left    := 0;
  Btn.Width   := 100;
  Btn.OnClick := @OnVideoBrowseClick;
  VideoBrowse := Btn;
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
end;

// ── Write appsettings.json after install ──────────────────────────────────────

procedure CurStepChanged(CurStep: TSetupStep);
var
  FilePath, Json: string;
begin
  if CurStep <> ssPostInstall then Exit;

  Json :=
    '{' + #13#10 +
    '  "webhookUrl": "' + EscapeJson(Trim(WebhookEdit.Text)) + '",' + #13#10 +
    '  "videoFolder": "' + EscapeJson(Trim(VideoEdit.Text))  + '",' + #13#10 +
    '  "apiUrl": "'      + EscapeJson(Trim(ApiUrlEdit.Text)) + '"'  + #13#10 +
    '}';

  FilePath := ExpandConstant('{app}\appsettings.json');
  SaveStringToFile(FilePath, Json, False);
end;
