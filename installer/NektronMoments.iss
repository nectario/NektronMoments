#ifndef InstallerRoot
  #define InstallerRoot "."
#endif
#include "shared\NektronMoments.Branding.iss"
#ifndef AppVersion
  #define AppVersion "0.1.31"
#endif
#ifndef SourcePublishDir
  #error SourcePublishDir is required. Use scripts/build-installer.ps1.
#endif
#ifndef OutputDir
  #define OutputDir InstallerRoot
#endif
#ifndef WorkspaceHint
  #define WorkspaceHint ""
#endif
[Setup]
AppId={code:GetMomentsAppId}
AppName=Nektron Moments
AppVersion={#AppVersion}
AppVerName=Nektron Moments {#AppVersion}
AppPublisher=Nektron, Inc.
AppPublisherURL=https://nektron.ai
AppSupportURL=https://nektron.ai
DefaultDirName={localappdata}\Programs\Nektron Moments
DefaultGroupName=Nektron Moments
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
DisableWelcomePage=no
DisableDirPage=yes
DisableProgramGroupPage=yes
UsePreviousAppDir=yes
UsePreviousTasks=yes
; Required for a code-based AppId; this installer has a single English language.
UsePreviousLanguage=no
AllowNoIcons=yes
WizardStyle=modern light hidebevels includetitlebar
DefaultDialogFontName=Segoe UI
SetupIconFile={#NektronMomentsSetupIconFile}
WizardImageFile={#NektronMomentsWizardImageFile}
WizardSmallImageFile={#NektronMomentsSmallImageFile}
WizardBackImageFile={#NektronMomentsBackImageFile}
WizardBackColor=#F7FAFC
WizardImageBackColor=#FFFFFF
WizardSmallImageBackColor=none
Compression=lzma2/max
SolidCompression=yes
OutputDir={#OutputDir}
OutputBaseFilename=NektronMoments.Setup.{#AppVersion}
UninstallDisplayIcon={app}\NektronMoments.exe
CloseApplications=yes
CloseApplicationsFilter=NektronMoments.exe
RestartApplications=no
ChangesAssociations=no
ChangesEnvironment=no
SetupLogging=yes
VersionInfoVersion={#AppVersion}.0
VersionInfoCompany=Nektron, Inc.
VersionInfoDescription=Nektron Moments Installer
VersionInfoProductName=Nektron Moments
VersionInfoProductVersion={#AppVersion}
#ifdef SignArtifacts
SignTool=nektronmoments
SignedUninstaller=yes
#endif

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Messages]
WelcomeLabel1=Welcome to Nektron Moments
WelcomeLabel2=Find. Relive. Remember.%n%nA beautiful home for your photos and videos.%n%nThis Windows preview reconnects to your existing Ubuntu / CLI library. Your originals, metadata and sign-in remain in place.%n%nThe Windows runtime is included. No developer tools are installed.
FinishedLabel=Nektron Moments is ready.%n%nYour existing library connection is preserved. Browse, search and rediscover your photos.%n%nNo originals were uploaded and no paid enrichment was started.

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "{#SourcePublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Registry]
Root: HKCU; Subkey: "Software\Nektron\NektronMoments"; ValueType: string; ValueName: "Workspace"; ValueData: "{code:GetWorkspace}"; Check: IntegrateWithUser
Root: HKCU; Subkey: "Software\Nektron\NektronMoments"; ValueType: string; ValueName: "InstallerOwner"; ValueData: "Nektron, Inc."; Check: IntegrateWithUser

[Icons]
Name: "{autoprograms}\Nektron Moments"; Filename: "{app}\NektronMoments.exe"; WorkingDir: "{app}"; Check: IntegrateWithUser
Name: "{autoprograms}\Uninstall Nektron Moments"; Filename: "{uninstallexe}"; Check: IntegrateWithUser
Name: "{autodesktop}\Nektron Moments"; Filename: "{app}\NektronMoments.exe"; WorkingDir: "{app}"; Tasks: desktopicon; Check: IntegrateWithUser

[Run]
Filename: "{app}\NektronMoments.exe"; Description: "Open Nektron Moments"; Flags: nowait postinstall skipifsilent; Check: IntegrateWithUser

[Code]
#include "shared\WindowFocus.iss"
const
  MomentsKey = 'Software\Nektron\NektronMoments';
  WM_SETREDRAW = $000B;
  PBM_SETBKCOLOR = $2001;
  PBM_SETBARCOLOR = $0409;
var
  WorkspacePage: TInputDirWizardPage;
  WorkspaceDetected: Boolean;
  ProgressImage: TBitmapImage;

function IsCanaryInstall: Boolean;
begin
  Result := ExpandConstant('{param:ASSETCANARY|0}') = '1';
end;

function IntegrateWithUser: Boolean;
begin
  Result := not IsCanaryInstall;
end;

function GetMomentsAppId(Param: String): String;
begin
  if IsCanaryInstall then
    Result := '{5DA57AF9-23F2-4B19-9950-0AA974C3E07D}'
  else
    Result := '{7A13CB58-5B03-4B35-A2F4-7BC06610ED38}';
end;

function InitializeSetup: Boolean;
var
  Target, Prefix: String;
begin
  Result := True;
  if IsCanaryInstall then begin
    Target := ExpandFileName(ExpandConstant('{param:DIR|}'));
    Prefix := AddBackslash(GetTempDir) + 'NektronMoments-InstallCheck-';
    Result := WizardSilent and (CompareText(Copy(Target, 1, Length(Prefix)), Prefix) = 0);
    if not Result then Log('Asset canary requires silent setup and a dedicated temporary destination.');
  end;
end;

function DwmSetWindowAttribute(hWnd: HWND; dwAttribute: Integer; var pvAttribute: Integer; cbAttribute: Integer): Integer;
  external 'DwmSetWindowAttribute@dwmapi.dll stdcall delayload';
function SetWindowTheme(hWnd: HWND; pszSubAppName: String; pszSubIdList: String): Integer;
  external 'SetWindowTheme@uxtheme.dll stdcall delayload';
function SendMessage(hWnd: HWND; Msg: UINT; wParam, lParam: LongInt): LongInt;
  external 'SendMessageW@user32.dll stdcall';

function ValidWorkspace(const Value: String): Boolean;
begin
  Result := (Length(Value) >= 3) and (Copy(Value, 2, 2) = ':\') and
    FileExists(AddBackslash(Value) + 'pyproject.toml') and
    FileExists(AddBackslash(Value) + 'cli\nektron_moments_cli\desktop_bridge.py') and
    FileExists(AddBackslash(Value) + '.venv\pyvenv.cfg');
end;

function GetWorkspace(Param: String): String;
begin
  Result := RemoveBackslashUnlessRoot(WorkspacePage.Values[0]);
end;

function ToWslPath(const Value: String): String;
begin
  Result := '/mnt/' + Lowercase(Copy(Value, 1, 1)) + Copy(Value, 3, Length(Value));
  StringChangeEx(Result, '\', '/', True);
end;

procedure ThemeWindow(Handle: HWND);
var
  LightMode, CaptionColor, TextColor, BorderColor: Integer;
begin
  LightMode := 0;
  CaptionColor := $00FCFAF7;
  TextColor := $00392710;
  BorderColor := $00EFE5D7;
  DwmSetWindowAttribute(Handle, 20, LightMode, 4);
  DwmSetWindowAttribute(Handle, 35, CaptionColor, 4);
  DwmSetWindowAttribute(Handle, 36, TextColor, 4);
  DwmSetWindowAttribute(Handle, 34, BorderColor, 4);
end;

procedure InitializeWizard;
var
  Existing: String;
begin
  WizardForm.OnShow := @WizardFormShown;
  ThemeWindow(WizardForm.Handle);
  WizardForm.Color := $00FCFAF7;
  WizardForm.MainPanel.Color := $00FCFAF7;
  WizardForm.InstallingPage.Color := $00FCFAF7;
  WizardForm.ReadyMemo.Color := clWhite;
  WizardForm.ReadyMemo.Font.Color := $00392710;
  WizardForm.TasksList.Color := clWhite;
  WizardForm.TasksList.Font.Color := $00392710;
  WizardForm.Bevel.Visible := False;
  WizardForm.Bevel1.Visible := False;
  WorkspacePage := CreateInputDirPage(wpWelcome, 'Connect your library',
    'Choose the existing Nektron Moments workspace.',
    'This preview uses the Ubuntu / CLI library you already set up. Choose the project folder, not your Pictures folder. No credentials or photos are copied.',
    False, '');
  WorkspacePage.Add('Library workspace:');
  Existing := ExpandConstant('{param:WORKSPACE|}');
  if not ValidWorkspace(Existing) then
    RegQueryStringValue(HKCU, MomentsKey, 'Workspace', Existing);
  if not ValidWorkspace(Existing) then
    Existing := GetEnv('NEKTRON_MOMENTS_WORKSPACE');
  if not ValidWorkspace(Existing) then
    Existing := '{#WorkspaceHint}';
  WorkspacePage.Values[0] := Existing;
  WorkspaceDetected := ValidWorkspace(Existing);
  ProgressImage := TBitmapImage.Create(WizardForm);
  ProgressImage.Parent := WizardForm.InstallingPage;
  ProgressImage.Visible := False;
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := (PageID = WorkspacePage.ID) and WorkspaceDetected;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID = WorkspacePage.ID) and not ValidWorkspace(GetWorkspace('')) then begin
    MsgBox('Choose the existing project folder containing the configured .venv and CLI. This installer does not create or replace a library.', mbError, MB_OK);
    Result := False;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Wsl, Arguments: String;
  ExitCode: Integer;
begin
  Result := '';
  if not ValidWorkspace(GetWorkspace('')) then begin
    Result := 'The existing CLI workspace is unavailable. Reconnect its drive or choose the correct project folder.';
    Exit;
  end;
  Wsl := ExpandConstant('{sys}\wsl.exe');
  if not FileExists(Wsl) then begin
    Result := 'Ubuntu WSL is required for this preview. Configure the existing CLI first, then rerun this installer.';
    Exit;
  end;
  Arguments := '-d Ubuntu --cd "' + ToWslPath(GetWorkspace('')) +
    '" --exec .venv/bin/python -B -c "import cli.nektron_moments_cli.desktop_bridge"';
  if not Exec(Wsl, Arguments, '', SW_HIDE, ewWaitUntilTerminated, ExitCode) then
    Result := 'Ubuntu could not be started. No application files have been installed.'
  else if ExitCode <> 0 then
    Result := 'The Ubuntu CLI environment is not ready. Run scripts/setup.sh and sign in to the CLI, then try again.';
end;

function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo,
  MemoComponentsInfo, MemoGroupInfo, MemoTasksInfo: String): String;
begin
  Result := 'Nektron Moments {#AppVersion}' + NewLine + NewLine +
    'Install for your Windows account:' + NewLine + Space + ExpandConstant('{app}') +
    NewLine + NewLine + 'Reconnect your library:' + NewLine + Space + GetWorkspace('') +
    NewLine + NewLine + 'Originals and credentials stay where they are.' + NewLine +
    'No uploads or paid enrichment are started by setup.' + NewLine + NewLine + MemoTasksInfo;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  ProgressImage.Visible := CurPageID = wpInstalling;
  if CurPageID = wpInstalling then begin
    WizardForm.ProgressGauge.Visible := False;
    ProgressImage.Left := WizardForm.ProgressGauge.Left;
    ProgressImage.Top := WizardForm.ProgressGauge.Top;
    ProgressImage.Width := WizardForm.ProgressGauge.Width;
    ProgressImage.Height := ScaleY(14);
    ProgressImage.Bitmap.Width := ProgressImage.Width;
    ProgressImage.Bitmap.Height := ProgressImage.Height;
  end;
end;

procedure CurInstallProgressChanged(CurProgress, MaxProgress: Integer);
var
  FillWidth: Integer;
begin
  WizardForm.ProgressGauge.Visible := False;
  if (ProgressImage = nil) or (ProgressImage.Width = 0) then Exit;
  FillWidth := 0;
  if MaxProgress > 0 then FillWidth := Round(ProgressImage.Width * (CurProgress / MaxProgress));
  ProgressImage.Bitmap.Canvas.Pen.Style := psClear;
  ProgressImage.Bitmap.Canvas.Brush.Color := $00F1E8D9;
  ProgressImage.Bitmap.Canvas.Rectangle(0, 0, ProgressImage.Width, ProgressImage.Height);
  ProgressImage.Bitmap.Canvas.Brush.Color := $00F39715;
  ProgressImage.Bitmap.Canvas.Rectangle(0, 0, FillWidth, ProgressImage.Height);
  ProgressImage.Repaint;
end;

procedure InitializeUninstallProgressForm;
begin
  ThemeWindow(UninstallProgressForm.Handle);
  UninstallProgressForm.Color := $00FCFAF7;
  SetWindowTheme(UninstallProgressForm.ProgressBar.Handle, '', '');
  SendMessage(UninstallProgressForm.ProgressBar.Handle, PBM_SETBKCOLOR, 0, $00F1E8D9);
  SendMessage(UninstallProgressForm.ProgressBar.Handle, PBM_SETBARCOLOR, 0, $00F39715);
end;
