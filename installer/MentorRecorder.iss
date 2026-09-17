; MentorRecorder / 导随记录器 — Inno Setup script.
;
; Built by scripts/package.ps1 (which passes AppVersion, StageDir and OutputDir) or by
; hand:  ISCC.exe /DAppVersion=<version> installer\MentorRecorder.iss   (no default exists)
;
; What the installer bundles: the staged release directory (Desktop + self-contained
; Collector, Qt and MinGW runtimes, licences, docs). What it does NOT bundle: Npcap. The
; Npcap free licence forbids redistribution, so when Npcap is missing the installer
; downloads the official installer from npcap.com (pinned version + SHA-256) and runs it;
; the user completes the Npcap wizard themselves. See docs/build-and-package.md.

; The version has exactly one source: Directory.Build.props/<Version>. There is deliberately
; no fallback, because a literal here would drift from it. scripts/package.ps1 passes
; /DAppVersion; a hand run must pass it too.
#ifndef AppVersion
  #error AppVersion is not defined. Run scripts/package.ps1, or pass /DAppVersion=x.y.z
#endif
#ifndef StageDir
  #define StageDir "..\artifacts\MentorRecorder-" + AppVersion + "-win-x64"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts"
#endif

#define AppName "导随记录器"
#define AppNameEn "MentorRecorder"
#define AppExe "MentorRecorder.Desktop.exe"
#define RepoUrl "https://github.com/Harendotes-Tang/MentorRouletteRecorder"
#define NpcapVersion "1.88"
#define NpcapUrl "https://npcap.com/dist/npcap-1.88.exe"
#define NpcapSha256 "a2f4ec1e5ea353ff67efd24b2ebf081ba44532410fae8d5e146af0310aa4f56b"
#define SetupBaseName "MentorRecorder-" + AppVersion + "-setup"
; The floor the runtimes set: Qt 6.11 supports Windows 10 from version 1809 (build 17763)
; and the self-contained .NET 8 runtime needs 1607, so 1809 is the requirement. Checked in
; InitializeSetup with a message that names the version, rather than through MinVersion
; alone, whose refusal does not say what would be enough.
#define MinWindowsBuild 17763
#define MinWindowsName "Windows 10 1809"

[Setup]
AppId={{7D5B1C2E-6A3F-4C0B-9E1D-2F8A4B6C9D01}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=MentorRecorder contributors
AppPublisherURL={#RepoUrl}
AppSupportURL={#RepoUrl}/issues
AppUpdatesURL={#RepoUrl}/releases
; Default to D:\MentorRecorder when D: is a fixed local disk with room for the package (the
; user's preference), otherwise Program Files - see DefaultInstallDir in [Code]. The
; directory page is always shown, so the location remains a choice.
DefaultDirName={code:DefaultInstallDir}
DisableDirPage=no
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
OutputDir={#OutputDir}
OutputBaseFilename={#SetupBaseName}
SetupIconFile=..\src\Desktop\resources\app\app.ico
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
LicenseFile={#StageDir}\LICENSE
; The Restart Manager finds MentorRecorder.Desktop.exe / MentorRecorder.Collector.exe
; under {app} and Setup asks before closing them (CloseApplicationsFilter defaults to
; *.exe,*.dll,*.chm, and "yes" - not "force" - means the user is prompted, never killed
; behind their back). They are not restarted afterwards: the [Run] entry below is how
; the application comes back, and only if the user asks for it.
CloseApplications=yes
RestartApplications=no
MinVersion=10.0
ShowLanguageDialog=no
VersionInfoVersion={#AppVersion}
VersionInfoProductName={#AppNameEn}
VersionInfoDescription={#AppNameEn} Setup
VersionInfoCopyright=GPL-3.0-or-later

[Languages]
Name: "chinesesimplified"; MessagesFile: "ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
chinesesimplified.NpcapPageTitle=需要安装 Npcap
chinesesimplified.NpcapPageSubtitle=本软件依赖 Npcap 被动读取网卡上的游戏流量
chinesesimplified.NpcapPageText=检测到本机没有安装 Npcap。%n%nNpcap 的免费许可证不允许随本软件一起分发，所以安装程序会从官网 npcap.com 下载 Npcap {#NpcapVersion} 的官方安装程序（已校验 SHA-256）并启动它。%n%n请在 Npcap 的安装向导里保持默认选项，特别是勾选“Install Npcap in WinPcap API-compatible Mode”，完成后回到这里继续。%n%n如果稍后想手动安装，也可以随时从 https://npcap.com 下载。
chinesesimplified.NpcapDownloadFailed=下载 Npcap 失败：%1%n%n安装会继续，但在你手动安装 Npcap 之前软件无法抓包。请到 https://npcap.com 下载并安装。
chinesesimplified.NpcapStillMissing=Npcap 仍未安装。软件已经装好，但在安装 Npcap 之前无法抓包。请到 https://npcap.com 下载并安装。
chinesesimplified.RemoveUserData=是否同时删除本机的记录数据（%1）？%n%n选择“否”会保留你的导随记录、备份和设置，以便重新安装后继续使用。
chinesesimplified.LaunchAfterInstall=启动 {#AppName}
chinesesimplified.WindowsTooOld=这台电脑的 Windows 版本太旧（%1）。%n%n{#AppName} 需要 {#MinWindowsName}（内部版本 {#MinWindowsBuild}）或更新的 64 位 Windows 10 / Windows 11。请先通过 Windows 更新升级系统，再运行本安装程序。
chinesesimplified.Arm64Windows10=这台电脑是 ARM 处理器上的 Windows 10。%n%n{#AppName} 是 64 位 x86 程序，Windows 10 on ARM 不能运行它；需要 Windows 11 on ARM 或 x64 电脑。
chinesesimplified.Arm64Notice=这台电脑是 ARM 处理器上的 Windows 11。%n%n{#AppName} 是 64 位 x86 程序，将通过系统的 x64 模拟运行；界面与手动记录可用，但通过 Npcap 抓包尚未在 ARM 设备上验证。
chinesesimplified.MediaFoundationMissing=这台电脑的 Windows 没有 Media Foundation（通常是“N”版本）。%n%n软件可以正常安装和记录，但语音播报无法出声。需要播报的话，请在“设置 → 应用 → 可选功能”里安装“媒体功能包（Media Feature Pack）”。
english.NpcapPageTitle=Npcap is required
english.NpcapPageSubtitle=This application reads game traffic passively through Npcap
english.NpcapPageText=Npcap is not installed on this computer.%n%nThe Npcap free licence does not allow it to be redistributed with this application, so Setup will download the official Npcap {#NpcapVersion} installer from npcap.com (SHA-256 verified) and start it.%n%nKeep the defaults in the Npcap wizard, in particular "Install Npcap in WinPcap API-compatible Mode", then return here to continue.%n%nYou can also install it later from https://npcap.com.
english.NpcapDownloadFailed=Downloading Npcap failed: %1%n%nSetup will continue, but capture cannot work until Npcap is installed. Please download it from https://npcap.com.
english.NpcapStillMissing=Npcap is still not installed. The application is installed, but capture cannot work until Npcap is present. Please download it from https://npcap.com.
english.RemoveUserData=Also delete the recorded data on this computer (%1)?%n%nChoose "No" to keep your records, backups and settings for a later reinstall.
english.LaunchAfterInstall=Launch {#AppNameEn}
english.WindowsTooOld=This version of Windows is too old (%1).%n%n{#AppNameEn} needs {#MinWindowsName} (build {#MinWindowsBuild}) or later, 64-bit Windows 10 or Windows 11. Please update Windows first, then run Setup again.
english.Arm64Windows10=This is Windows 10 on an ARM processor.%n%n{#AppNameEn} is a 64-bit x86 application, which Windows 10 on ARM cannot run; it needs Windows 11 on ARM or an x64 PC.
english.Arm64Notice=This is Windows 11 on an ARM processor.%n%n{#AppNameEn} is a 64-bit x86 application and will run through the system's x64 emulation. The interface and manual records work; capturing through Npcap has not been verified on ARM devices.
english.MediaFoundationMissing=This Windows has no Media Foundation (usually an "N" edition).%n%nThe application installs and records normally, but voice announcements will be silent. To hear them, install the "Media Feature Pack" under Settings > Apps > Optional features.

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[InstallDelete]
; Every directory below is filled with loose files by the staging step, so an upgrade
; that simply copies over the top would leave a withdrawn protocol profile, a Qt plugin
; or a QML module from the previous version behind - and a stale protocol profile is a
; profile the selector will still offer. Clear them first, then reinstall the current
; contents. Nothing here is user data: records, backups and settings live under
; %LOCALAPPDATA%\MentorRecorder and are never touched by an upgrade.
Type: filesandordirs; Name: "{app}\protocol-profiles"
Type: filesandordirs; Name: "{app}\qml"
Type: filesandordirs; Name: "{app}\plugins"
Type: filesandordirs; Name: "{app}\docs"
Type: filesandordirs; Name: "{app}\generic"
Type: filesandordirs; Name: "{app}\iconengines"
Type: filesandordirs; Name: "{app}\imageformats"
Type: filesandordirs; Name: "{app}\multimedia"
Type: filesandordirs; Name: "{app}\networkinformation"
Type: filesandordirs; Name: "{app}\platforminputcontexts"
Type: filesandordirs; Name: "{app}\platforms"
Type: filesandordirs; Name: "{app}\qmltooling"
Type: filesandordirs; Name: "{app}\styles"
Type: filesandordirs; Name: "{app}\texttospeech"
Type: filesandordirs; Name: "{app}\tls"
Type: filesandordirs; Name: "{app}\vectorimageformats"

[Files]
Source: "{#StageDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchAfterInstall}"; Flags: nowait postinstall skipifsilent runasoriginaluser

[Code]
const
  UserDataRegKey = 'SOFTWARE\MentorRecorder';
  UserDataRegValue = 'UserLocalAppData';
  { GetDriveType return values (winbase.h). Only a fixed local disk may be defaulted to. }
  DriveFixed = 3;
  { The staged package is ~150 MB and the database grows with play history; a drive with
    less than this free is not somewhere to silently propose installing. }
  RequiredFreeMegabytes = 512;

{ DirExists('D:\') is also true for a mounted CD, a card reader, a mapped network share and
  a USB stick, any of which would fail at the copy step or install onto removable media.
  GetDriveType asks the question that is actually meant: is D: a fixed local disk? }
function GetDriveTypeW(lpRootPathName: String): Cardinal;
  external 'GetDriveTypeW@kernel32.dll stdcall setuponly';

var
  NpcapPage: TOutputMsgWizardPage;
  DownloadPage: TDownloadWizardPage;
  NpcapHandled: Boolean;
  UninstallUserDataDir: String;

function DriveTypeOf(const Root: String): Cardinal;
begin
  { An unreachable import must not abort setup at the directory page. Answering
    DRIVE_UNKNOWN sends DefaultInstallDir down the Program Files branch, which is the safe
    answer for a machine we could not ask about. }
  try
    Result := GetDriveTypeW(Root);
  except
    Result := 0;
  end;
end;

function IsUsableFixedDisk(const Root: String): Boolean;
var
  FreeBytes, TotalBytes, Required: Int64;
begin
  Result := False;
  if DriveTypeOf(Root) <> DriveFixed then
    Exit;
  if not DirExists(Root) then
    Exit;
  if not GetSpaceOnDisk64(Root, FreeBytes, TotalBytes) then
    Exit;
  Required := RequiredFreeMegabytes;
  Required := Required * 1024 * 1024;
  Result := FreeBytes >= Required;
end;

function DefaultInstallDir(Param: String): String;
begin
  { The user's preference is D:\MentorRecorder, but only when D: is a fixed local disk with
    room for the package. Anything else - no D:, an optical or removable drive, a network
    mapping, a full disk - falls back to Program Files. The directory page is always shown,
    so this is a proposal, never a decision. }
  if IsUsableFixedDisk('D:\') then
    Result := 'D:\MentorRecorder'
  else
    Result := ExpandConstant('{autopf}\MentorRecorder');
end;

function NpcapInstalled: Boolean;
begin
  Result := FileExists(ExpandConstant('{sys}\Npcap\wpcap.dll'))
    or RegKeyExists(HKLM, 'SOFTWARE\Npcap')
    or RegKeyExists(HKLM, 'SOFTWARE\WOW6432Node\Npcap');
end;

function OnDownloadProgress(const Url, FileName: String; const Progress, ProgressMax: Int64): Boolean;
begin
  if ProgressMax <> 0 then
    Log(Format('Npcap download: %d of %d bytes', [Progress, ProgressMax]));
  Result := True;
end;

{ Minimum requirements, checked before any page is shown. Each refusal names the
  requirement that is not met; each warning says what will not work and continues. }
function InitializeSetup(): Boolean;
var
  Version: TWindowsVersion;
  Build: String;
begin
  Result := True;
  GetWindowsVersionEx(Version);
  Build := Format('%d.%d.%d', [Version.Major, Version.Minor, Version.Build]);
  Log(Format('System check: Windows %s, ARM64=%d', [Build, Ord(IsArm64)]));

  { Qt 6.11 and the .NET 8 runtime: Windows 10 version 1809 (build 17763) or later. }
  if (Version.Major < 10) or ((Version.Major = 10) and (Version.Build < {#MinWindowsBuild})) then
  begin
    SuppressibleMsgBox(FmtMessage(CustomMessage('WindowsTooOld'), [Build]), mbCriticalError, MB_OK, IDOK);
    Result := False;
    exit;
  end;

  { An x64 build on Windows on ARM: Windows 11 emulates it, Windows 10 does not. }
  if IsArm64 then
  begin
    if Version.Build < 22000 then
    begin
      SuppressibleMsgBox(CustomMessage('Arm64Windows10'), mbCriticalError, MB_OK, IDOK);
      Result := False;
      exit;
    end;
    SuppressibleMsgBox(CustomMessage('Arm64Notice'), mbInformation, MB_OK, IDOK);
  end;

  { Windows "N" editions ship without Media Foundation, which QSoundEffect and the
    text-to-speech backend play through; recording works, announcements stay silent. }
  if not FileExists(ExpandConstant('{sys}\mfplat.dll')) then
    SuppressibleMsgBox(CustomMessage('MediaFoundationMissing'), mbInformation, MB_OK, IDOK);
end;

procedure InitializeWizard;
begin
  NpcapPage := CreateOutputMsgPage(wpSelectTasks,
    CustomMessage('NpcapPageTitle'), CustomMessage('NpcapPageSubtitle'), CustomMessage('NpcapPageText'));
  DownloadPage := CreateDownloadPage(SetupMessage(msgWizardPreparing), SetupMessage(msgPreparingDesc), @OnDownloadProgress);
  NpcapHandled := False;
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := False;
  if PageID = NpcapPage.ID then
    Result := NpcapInstalled;
end;

procedure RunNpcapInstaller;
var
  ResultCode: Integer;
  Installer: String;
begin
  Installer := ExpandConstant('{tmp}\npcap-{#NpcapVersion}.exe');
  DownloadPage.Clear;
  DownloadPage.Add('{#NpcapUrl}', 'npcap-{#NpcapVersion}.exe', '{#NpcapSha256}');
  DownloadPage.Show;
  try
    try
      DownloadPage.Download;
    except
      MsgBox(FmtMessage(CustomMessage('NpcapDownloadFailed'), [GetExceptionMessage]), mbError, MB_OK);
      Exit;
    end;
  finally
    DownloadPage.Hide;
  end;

  // The free edition of Npcap has no silent mode; the user completes its wizard.
  if not Exec(Installer, '', '', SW_SHOW, ewWaitUntilTerminated, ResultCode) then
    Log('Npcap installer could not be started')
  else
    Log(Format('Npcap installer exited with code %d', [ResultCode]));
end;

// Which profile holds the recorded data.
//
// PrivilegesRequired=admin means Setup runs elevated, and when UAC was satisfied with a
// *different* administrator account every {user...} constant - {localappdata} included -
// points at that administrator's profile rather than at the profile of the person who
// will actually use the application. The uninstaller would then offer to delete a
// directory nobody has, and leave the real records in place. So ask the original,
// unelevated user for its own %LOCALAPPDATA% at install time and record the answer.
//
// The answer travels through C:\Users\Public\Documents ({commondocs}) because that is
// the one directory both sides can reach: {tmp} lives under the elevated profile and the
// original user may have no access to it.
function OriginalUserLocalAppData: String;
var
  Marker: String;
  ResultCode: Integer;
  Lines: TArrayOfString;
begin
  Result := '';
  // {commondocs} is writable by every account on the machine, so the name is randomised:
  // a file planted there ahead of Setup must not be mistaken for the original user's
  // answer. Even so, the answer is only ever used to *name a directory in a prompt* -
  // nothing is deleted until the person running the uninstaller reads that path and
  // answers Yes.
  Marker := ExpandConstant('{commondocs}\MentorRecorder-setup-' +
    IntToStr(Random(100000000)) + '.txt');
  DeleteFile(Marker);
  if ExecAsOriginalUser(ExpandConstant('{cmd}'), '/C >"' + Marker + '" echo %LOCALAPPDATA%',
       ExpandConstant('{commondocs}'), SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    if LoadStringsFromFile(Marker, Lines) and (GetArrayLength(Lines) > 0) then
      Result := Trim(Lines[0]);
  end;
  DeleteFile(Marker);

  // cmd echoes the name back verbatim when the variable does not exist; an answer
  // without a path separator, or one that is not a directory, is no answer at all.
  if (Pos('\', Result) = 0) or (not DirExists(Result)) then
    Result := '';
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  DataRoot: String;
begin
  if CurStep = ssPostInstall then
  begin
    DataRoot := OriginalUserLocalAppData;
    if DataRoot = '' then
      DataRoot := ExpandConstant('{localappdata}');
    Log('Recorded data root: ' + DataRoot);
    // HKLM, not HKCU: the uninstaller is elevated too and would read the wrong hive.
    RegWriteStringValue(HKEY_LOCAL_MACHINE, UserDataRegKey, UserDataRegValue, DataRoot);
  end;
end;

function InitializeUninstall: Boolean;
begin
  Result := True;
  // Read it now: the value is removed further down, once it has been used.
  if not RegQueryStringValue(HKEY_LOCAL_MACHINE, UserDataRegKey, UserDataRegValue,
       UninstallUserDataDir) then
    UninstallUserDataDir := '';
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID = wpReady) and (not NpcapHandled) and (not NpcapInstalled) then
  begin
    NpcapHandled := True;
    RunNpcapInstaller;
    if not NpcapInstalled then
      MsgBox(CustomMessage('NpcapStillMissing'), mbInformation, MB_OK);
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataRoot: String;
  DataDir: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    // The profile the *installing* user named, not the one this uninstaller happens to
    // be elevated as. Falls back to this account only when nothing was recorded.
    DataRoot := UninstallUserDataDir;
    if DataRoot = '' then
      DataRoot := ExpandConstant('{localappdata}');
    DataDir := AddBackslash(DataRoot) + 'MentorRecorder';

    // Nothing is ever deleted without this prompt, and its default answer is "No".
    if DirExists(DataDir) then
      if MsgBox(FmtMessage(CustomMessage('RemoveUserData'), [DataDir]), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(DataDir, True, True, True);

    RegDeleteKeyIncludingSubkeys(HKEY_LOCAL_MACHINE, UserDataRegKey);
  end;
end;
