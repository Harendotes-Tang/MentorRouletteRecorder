; MentorRecorder / 导随记录器 — Inno Setup script.
;
; Built by scripts/package.ps1 (which passes AppVersion, StageDir and OutputDir) or by
; hand:  ISCC.exe /DAppVersion=<version> installer\MentorRecorder.iss   (no default exists)
; The version may be a prerelease (1.4.0-beta.1); see the AppVersionNumeric note below.
;
; What the installer bundles: the staged release directory (Desktop + self-contained
; Collector, Qt and MinGW runtimes, licences, docs). What it does NOT bundle: Npcap. The
; Npcap free licence forbids redistribution, so when Npcap is missing the installer
; downloads the official installer from npcap.com (pinned version + SHA-256) and runs it;
; the user completes the Npcap wizard themselves. See docs/build-and-package.md.

; Inno Setup 6.7 or newer: RedirectionGuard in [Setup] first appears in 6.7.0, and an older
; compiler would reject it without saying that the compiler is what is too old.
#if Ver < EncodeVer(6,7,0)
  #error Inno Setup 6.7 or newer is required (RedirectionGuard). Update it: winget upgrade JRSoftware.InnoSetup
#endif

; The version has exactly one source: Directory.Build.props (<VersionPrefix> plus the
; optional <VersionSuffix>). There is deliberately no fallback, because a literal here would
; drift from it. scripts/package.ps1 passes /DAppVersion; a hand run must pass it too.
;
; AppVersion is the full string a person reads and may carry a prerelease suffix
; (1.4.0-beta.1). VersionInfoVersion cannot: it becomes the setup executable's Win32
; VERSIONINFO resource, whose fields are four numbers, and ISCC refuses anything else. The
; numeric part is therefore derived below rather than written out a second time.
#ifndef AppVersion
  #error AppVersion is not defined. Run scripts/package.ps1, or pass /DAppVersion=x.y.z[-beta.N]
#endif
#ifndef AppVersionNumeric
  #if Pos("-", AppVersion) > 0
    #define AppVersionNumeric Copy(AppVersion, 1, Pos("-", AppVersion) - 1)
  #else
    #define AppVersionNumeric AppVersion
  #endif
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
; Setup and Uninstall run elevated and work below a folder that, on a data drive such as D:,
; ordinary users may have created or filled before installation. RedirectionGuard (Windows 11
; and Windows 10 22H2) stops both from following a junction or symbolic link that an
; unelevated process created; ProtectInstallDirectory in [Code] refuses such links itself,
; which is what covers older Windows. Written out because it is a security setting.
RedirectionGuard=yes
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
; Use Chinese by default on every Windows locale and after an English install.
; An explicitly supplied /LANG can still choose a registered language.
LanguageDetectionMethod=none
UsePreviousLanguage=no
VersionInfoVersion={#AppVersionNumeric}
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
chinesesimplified.RemoveUserData=是否同时删除本机的记录数据（%1）？%n%n选择“否”会保留你的导随记录、备份和设置，以便重新安装后继续使用。%n%n备注图片不在其中：它们保存在安装文件夹的“%2”中，无论选择哪一项都会保留。如果不再需要，请在卸载完成后手动删除该文件夹。
chinesesimplified.InstallDirIsLink=所选的安装文件夹“%1”是一个链接（联接点或符号链接），实际指向别处的文件夹。%n%n为防止程序文件被安装或授权到链接所指的位置，请选择一个普通文件夹。
chinesesimplified.InstallDirNotEmpty=所选的文件夹“%1”中已有其他文件。%n%n为防止程序文件被篡改，安装程序会把安装文件夹设为只有管理员可以修改、其他用户只能读取和运行，这同样会作用到其中原有的文件。请选择一个新的或空的文件夹。
chinesesimplified.InstallDirIsDriveRoot=不能把软件直接安装到整个磁盘的根目录（“%1”）。%n%n磁盘根目录通常允许本机所有用户在其中新建和改名文件夹，安装程序无法像保护普通文件夹那样保护它。请选择该磁盘上的一个子文件夹（例如 D:\MentorRecorder），而不是磁盘根目录本身。
chinesesimplified.InstallDirCreateFailed=无法创建安装文件夹“%1”，安装已停止。请重新运行安装程序并选择其他位置。
chinesesimplified.InstallDirProtectFailed=无法为安装文件夹“%1”设置访问权限（错误码 %2），安装已停止。%n%n安装程序必须把该文件夹设为只有管理员可以修改，否则本机的其他用户可以篡改程序文件。该位置可能不在本机的 NTFS 磁盘上（例如 U 盘、FAT32 或 exFAT 分区、网络位置）。请重新运行安装程序，选择本机 NTFS 磁盘上的文件夹。
chinesesimplified.InstallDirFileIsLink=安装文件夹中的文件“%1”是指向别处文件的链接（硬链接或符号链接），或者安装程序无法确认它不是链接，安装已停止。%n%n安装程序会把安装文件夹中原有的文件设为只有管理员可以修改；如果这个文件是链接，这项改动就会落到与它相连的另一个文件上。请删除这个文件（如果它是链接，与它相连的文件不受影响），然后重新运行安装程序。
chinesesimplified.NoteImagesIsLink=安装文件夹中的“%1”是一个链接（联接点或符号链接），而不是普通文件夹，安装已停止。%n%n安装程序会允许本机所有用户在这个文件夹中保存备注图片；如果它是链接，这项写入权限就会落到链接所指的位置。请删除这个链接（链接所指的文件夹不受影响），然后重新运行安装程序。
chinesesimplified.NoteImagesGrantFailed=无法为安装文件夹中的“%1”设置访问权限（错误码 %2），安装已停止。%n%n这个文件夹用来保存备注图片，安装程序需要允许本机所有用户在其中保存和删除图片，但没有设置成功。请重新运行安装程序。
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
english.RemoveUserData=Also delete the recorded data on this computer (%1)?%n%nChoose "No" to keep your records, backups and settings for a later reinstall.%n%nNote images are not part of it: they are kept in "%2" in the installation folder whichever you choose. Delete that folder by hand after uninstalling if you no longer need them.
english.InstallDirIsLink=The selected folder "%1" is a link (a junction or symbolic link) to a folder elsewhere.%n%nSo that program files are never installed into, or granted on, the place it points to, please choose an ordinary folder.
english.InstallDirNotEmpty=The selected folder "%1" already contains other files.%n%nTo keep the program files from being tampered with, Setup makes the installation folder modifiable by administrators only and readable and executable by other users; that would apply to the files already there as well. Please choose a new or empty folder.
english.InstallDirIsDriveRoot=Setup cannot install into the root of an entire drive ("%1").%n%nA drive root usually lets every user of this computer create and rename folders in it, so Setup cannot protect it the way it protects an ordinary folder. Please choose a subfolder on the drive (for example D:\MentorRecorder) rather than the drive root itself.
english.InstallDirCreateFailed=The installation folder "%1" could not be created, so Setup has stopped. Please run Setup again and choose another location.
english.InstallDirProtectFailed=The access permissions of the installation folder "%1" could not be set (error code %2), so Setup has stopped.%n%nSetup must make this folder modifiable by administrators only; otherwise other users of this computer could tamper with the program files. The location may not be on a local NTFS disk (for example a USB drive, a FAT32 or exFAT partition, or a network location). Please run Setup again and choose a folder on a local NTFS disk.
english.InstallDirFileIsLink=The file "%1" in the installation folder is a link to a file elsewhere (a hard link or a symbolic link), or Setup could not confirm that it is not one, so Setup has stopped.%n%nSetup makes the files already in the installation folder modifiable by administrators only; if this file is a link, that change would land on the other file it is linked to. Please delete this file (if it is a link, the file it is linked to is not affected), then run Setup again.
english.NoteImagesIsLink=The "%1" folder in the installation folder is a link (a junction or symbolic link), not an ordinary folder, so Setup has stopped.%n%nSetup lets every user of this computer save note images in this folder; if it were a link, that write access would land wherever it points. Please delete the link (the folder it points to is not affected), then run Setup again.
english.NoteImagesGrantFailed=The access permissions of "%1" in the installation folder could not be set (error code %2), so Setup has stopped.%n%nThis folder holds note images, and Setup must allow every user of this computer to save and delete images in it, but could not. Please run Setup again.
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
Type: filesandordirs; Name: "{app}\ocr"
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
; A DLL loose in {app}, or a planted qt.conf, is loaded by the Desktop and the Collector from
; their own directory (DLL search order; Qt's settings file). The package ships neither a
; foreign DLL nor a qt.conf, so clearing every top-level *.dll and qt.conf before [Files]
; copies the real binaries removes anything an unprivileged user planted here before Setup ran
; (a fresh install into a folder someone pre-created, or an upgrade over 1.5.0, whose folder
; every local user could write). The DLLs we ship are re-copied immediately afterwards; this
; is files-only, so no directory is followed. Executables are not auto-loaded this way and are
; handled by ProtectInstallDirectory (ownership), so they are not blanket-deleted here - that
; would also catch the uninstaller's own unins*.exe. qt.conf is listed before *.dll only for
; readability; [InstallDelete] order does not matter.
Type: files; Name: "{app}\qt.conf"
Type: files; Name: "{app}\*.dll"
; 1.5.0 and earlier installed the application manifest as a side file. It is embedded in the
; executable now and no longer shipped, so an upgrade removes the old copy.
Type: files; Name: "{app}\MentorRecorder.Desktop.exe.manifest"

[Files]
Source: "{#StageDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Dirs]
; Note images are user data stored beside the application. The rest of {app} is writable by
; administrators only; this subtree alone lets ordinary users write, including in Program
; Files. Both are set by ProtectInstallDirectory in [Code], before this section runs, and
; without propagation: a Permissions parameter here would go through SetNamedSecurityInfo,
; which pushes the grant onto every file already inside - including a file that is merely a
; hard link to a file elsewhere. This entry is kept for its flag: the folder survives upgrades
; and uninstalls.
Name: "{app}\note-images"; Flags: uninsneveruninstall

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
  // The subfolder of {app} that holds note images (see [Dirs]): the only part of the
  // install folder ordinary users may write to.
  NoteImagesDirName = 'note-images';
  // Ownership and per-file resets, applied by ProtectInstallDirectory and ProtectTopLevelFiles.
  // Accounts are named by well-known SID, never by name, because Windows localises the names
  // ("Users" is "用户" on a Chinese system): S-1-5-32-544 is Administrators. /L makes icacls act
  // on a link itself, never on what a link points to. Neither propagates: an owner is not
  // inherited, and /reset on a single file has nothing below it to reach.
  IcaclsOwnerArgs = '/setowner *S-1-5-32-544 /L /Q';
  IcaclsResetArgs = '/reset /L /Q';
  // The DACLs of {app} and of note-images, set by ApplyDacl through SetFileSecurityW, which -
  // unlike icacls and SetNamedSecurityInfo - changes the named folder only and propagates
  // nothing to what already sits below it. SDDL: P protected (nothing inherited from the
  // parent), AI auto-inherit marker, every entry OICI (inherited by the files and folders created
  // inside afterwards); BA Administrators and SY SYSTEM full control (FA); BU Users read and
  // execute (0x1200a9) in {app}, modify (0x1301bf, the mask Inno's own "users-modify" grants) in
  // note-images. Well-known SID aliases, so no account name is localised.
  AppDirSddl = 'D:PAI(A;OICI;FA;;;BA)(A;OICI;FA;;;SY)(A;OICI;0x1200a9;;;BU)';
  NoteImagesSddl = 'D:PAI(A;OICI;FA;;;BA)(A;OICI;FA;;;SY)(A;OICI;0x1301bf;;;BU)';
  // SDDL_REVISION_1 (sddl.h); DACL_SECURITY_INFORMATION (4) combined with
  // PROTECTED_DACL_SECURITY_INFORMATION ($80000000) (winnt.h).
  SddlRevision1 = 1;
  ProtectedDaclInformation = $80000004;
  // CreateFileW arguments for LinkCountOf: ask for the attributes only (FILE_READ_ATTRIBUTES),
  // share everything (FILE_SHARE_READ, _WRITE and _DELETE), open an existing entry
  // (OPEN_EXISTING), and open a reparse point itself rather than what it names
  // (FILE_FLAG_OPEN_REPARSE_POINT). Values from the Windows SDK (winnt.h, fileapi.h).
  LinkProbeAccess = $80;
  LinkProbeShare = 7;
  LinkProbeDisposition = 3;
  LinkProbeFlags = $00200000;

type
  // BY_HANDLE_FILE_INFORMATION (fileapi.h): thirteen DWORDs, each FILETIME written out as its
  // two DWORDs. Pascal Script places record fields back to back with no padding, which matches
  // the C layout only because every field here is 32 bits wide - keep it that way (no Int64, no
  // nested FILETIME record). nNumberOfLinks is the eleventh DWORD, at byte offset 40.
  TByHandleFileInformation = record
    dwFileAttributes: Cardinal;
    ftCreationTimeLow: Cardinal;
    ftCreationTimeHigh: Cardinal;
    ftLastAccessTimeLow: Cardinal;
    ftLastAccessTimeHigh: Cardinal;
    ftLastWriteTimeLow: Cardinal;
    ftLastWriteTimeHigh: Cardinal;
    dwVolumeSerialNumber: Cardinal;
    nFileSizeHigh: Cardinal;
    nFileSizeLow: Cardinal;
    nNumberOfLinks: Cardinal;
    nFileIndexHigh: Cardinal;
    nFileIndexLow: Cardinal;
  end;

{ DirExists('D:\') is also true for a mounted CD, a card reader, a mapped network share and
  a USB stick, any of which would fail at the copy step or install onto removable media.
  GetDriveType asks the question that is actually meant: is D: a fixed local disk? }
function GetDriveTypeW(lpRootPathName: String): Cardinal;
  external 'GetDriveTypeW@kernel32.dll stdcall setuponly';

// For LinkCountOf. Setup is a 32-bit process (Inno Setup 6 ships only Setup.e32), so a HANDLE
// and a pointer are 32 bits: handles are Longint here (INVALID_HANDLE_VALUE is -1), the unused
// lpSecurityAttributes and hTemplateFile are passed as 0, and the BOOL results are read as
// Longint (non-zero is success). A String argument reaches a W function as a PWideChar, as in
// GetDriveTypeW above; a var record argument is passed as a pointer to the record.
function CreateFileW(lpFileName: String; dwDesiredAccess, dwShareMode: Cardinal;
  lpSecurityAttributes: Longint; dwCreationDisposition, dwFlagsAndAttributes: Cardinal;
  hTemplateFile: Longint): Longint;
  external 'CreateFileW@kernel32.dll stdcall setuponly';
function GetFileInformationByHandle(hFile: Longint;
  var lpFileInformation: TByHandleFileInformation): Longint;
  external 'GetFileInformationByHandle@kernel32.dll stdcall setuponly';
function CloseHandle(hObject: Longint): Longint;
  external 'CloseHandle@kernel32.dll stdcall setuponly';

// For ApplyDacl, with the same 32-bit conventions. The converter returns, through its var
// argument, a pointer to a self-relative security descriptor it allocated with LocalAlloc;
// that pointer is handed to SetFileSecurityW and released with LocalFree. The optional size
// output is not wanted and is passed as 0 (NULL). BOOL results: non-zero is success, and the
// Windows error of a failure is read with DLLGetLastError right after the call.
function ConvertStringSecurityDescriptorToSecurityDescriptorW(StringSecurityDescriptor: String;
  StringSDRevision: Cardinal; var SecurityDescriptor: Longint;
  SecurityDescriptorSize: Longint): Longint;
  external 'ConvertStringSecurityDescriptorToSecurityDescriptorW@advapi32.dll stdcall setuponly';
function SetFileSecurityW(lpFileName: String; SecurityInformation: Cardinal;
  pSecurityDescriptor: Longint): Longint;
  external 'SetFileSecurityW@advapi32.dll stdcall setuponly';
function LocalFree(hMem: Longint): Longint;
  external 'LocalFree@kernel32.dll stdcall setuponly';

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

// True when Path is an NTFS junction or symbolic link. FindFirst reads the entry from the
// parent folder, so it describes the link itself, not the folder the link points to.
function IsReparsePoint(const Path: String): Boolean;
var
  FindRec: TFindRec;
begin
  Result := False;
  if FindFirst(RemoveBackslashUnlessRoot(Path), FindRec) then
  begin
    try
      Result := (FindRec.Attributes and FILE_ATTRIBUTE_REPARSE_POINT) <> 0;
    finally
      FindClose(FindRec);
    end;
  end;
end;

// True when Path is the root of a whole drive ("D:\" or "D:"). Such a folder has no parent
// whose ACL Setup could rely on, it is normally world-writable, and its trailing backslash
// would also break the icacls argument quoting (a drive-root target "D:\" reads as an escaped
// quote). Setup refuses it rather than install into it.
function IsDriveRoot(const Path: String): Boolean;
begin
  Result := ((Length(Path) = 3) and (Path[2] = ':') and (Path[3] = '\'))
    or ((Length(Path) = 2) and (Path[2] = ':'));
end;

// True when Dir contains anything at all.
function DirectoryHasEntries(const Dir: String): Boolean;
var
  FindRec: TFindRec;
begin
  Result := False;
  if FindFirst(AddBackslash(Dir) + '*', FindRec) then
  begin
    try
      repeat
        Result := (FindRec.Name <> '.') and (FindRec.Name <> '..');
      until Result or (not FindNext(FindRec));
    finally
      FindClose(FindRec);
    end;
  end;
end;

// True when note-images is the only thing left in Dir - which is what an uninstall leaves
// behind ([Dirs] uninsneveruninstall). A folder that holds note-images AND other content is
// deliberately not this: it is a foreign folder with a note-images planted beside other
// files, and must not pass the non-empty-folder refusal on the strength of note-images alone.
function HoldsOnlyNoteImages(const Dir: String): Boolean;
var
  FindRec: TFindRec;
  HasNoteImages, HasOther: Boolean;
begin
  HasNoteImages := False;
  HasOther := False;
  if FindFirst(AddBackslash(Dir) + '*', FindRec) then
  begin
    try
      repeat
        if (FindRec.Name <> '.') and (FindRec.Name <> '..') then
        begin
          if CompareText(FindRec.Name, NoteImagesDirName) = 0 then
            HasNoteImages := True
          else
            HasOther := True;
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
  Result := HasNoteImages and (not HasOther);
end;

// An earlier installation of this application - or the lone note-images folder its uninstaller
// leaves behind - is the one kind of non-empty folder Setup may take over. A real executable is
// required: a bare note-images directory beside planted files is not enough (an attacker could
// create one to slip past the refusal). This is only a convenience for honest users; the folder
// is protected and swept at ssInstall regardless of what this returns (ProtectInstallDirectory).
function HoldsThisApplication(const Dir: String): Boolean;
begin
  Result := FileExists(AddBackslash(Dir) + '{#AppExe}')
    or FileExists(AddBackslash(Dir) + 'MentorRecorder.Collector.exe')
    or HoldsOnlyNoteImages(Dir);
end;

// Runs System32's icacls on Target; returns its exit code, or a non-zero Windows error code
// when it could not be started. The absolute path means an icacls.exe found earlier on PATH
// is never run, and in 64-bit install mode Exec reaches the 64-bit System32. Its output goes
// to the Setup log; SW_HIDE so that no console window flashes up for any call.
function RunIcacls(const Target, Args: String): Integer;
var
  ResultCode: Integer;
begin
  ResultCode := -1;
  Log(Format('icacls "%s" %s', [Target, Args]));
  try
    if not ExecAndLogOutput(ExpandConstant('{sys}\icacls.exe'), '"' + Target + '" ' + Args, '',
         SW_HIDE, ewWaitUntilTerminated, ResultCode, nil) then
    begin
      Log('icacls could not be started: ' + SysErrorMessage(ResultCode));
      if ResultCode = 0 then
        ResultCode := -1;
    end;
  except
    Log(GetExceptionMessage);
    ResultCode := -1;
  end;
  Result := ResultCode;
end;

// Writes the ACL of Path to the Setup log (icacls with no arguments only lists it), so the
// log of a real installation shows what was applied. A failure here changes nothing.
procedure LogAcl(const Path: String);
begin
  RunIcacls(Path, '');
end;

// Replaces the DACL of the folder Path with the protected one Sddl describes, through
// SetFileSecurityW. That call changes Path only: unlike icacls, SetNamedSecurityInfo and Inno's
// own [Dirs] Permissions, it does not re-derive the entries of anything already below Path - and
// a file below that is a hard link to a file elsewhere shares that file's security descriptor,
// so propagation would change the other file as well. Files and folders created inside Path
// afterwards still inherit the OICI entries, as Windows applies inheritance on creation. The
// descriptor the converter allocates is always released. Returns 0 on success, otherwise a
// non-zero Windows error code.
function ApplyDacl(const Path, Sddl: String): Integer;
var
  Descriptor: Longint;
begin
  Result := -1;
  Descriptor := 0;
  try
    if ConvertStringSecurityDescriptorToSecurityDescriptorW(Sddl, SddlRevision1, Descriptor, 0) = 0 then
    begin
      Result := DLLGetLastError;
      if Result = 0 then
        Result := -1;
    end
    else
      try
        if SetFileSecurityW(Path, ProtectedDaclInformation, Descriptor) <> 0 then
          Result := 0
        else
        begin
          Result := DLLGetLastError;
          if Result = 0 then
            Result := -1;
        end;
      finally
        LocalFree(Descriptor);
      end;
  except
    Log(GetExceptionMessage);
    Result := -1;
  end;
  if Result = 0 then
    Log(Format('DACL of "%s" set to %s', [Path, Sddl]))
  else
  begin
    if Result > 0 then
      Log(Format('Setting the DACL of "%s" failed: %s', [Path, SysErrorMessage(Result)]))
    else
      Log(Format('Setting the DACL of "%s" failed', [Path]));
  end;
end;

// True for the names [InstallDelete] removes right after ssInstall: every "{app}\*.dll" and
// "{app}\qt.conf". A long name ends in ".dll" exactly when it matches the *.dll wildcard, and
// both comparisons ignore case as Windows does, so every name skipped here is one that
// [InstallDelete] deletes. (The wildcard may also match a few more names through their short
// 8.3 forms; those are not skipped, so they are re-owned and then deleted - harmless.)
function IsRemovedByInstallDelete(const Name: String): Boolean;
begin
  Result := (CompareText(ExtractFileExt(Name), '.dll') = 0)
    or (CompareText(Name, 'qt.conf') = 0);
end;

// The number of names (hard links) the file at Path has, or 0 when it cannot be read. Also
// returns the attributes the open handle reports. The handle asks for the attributes only and
// opens a reparse point itself, never what it names. Hard links share one security descriptor,
// so re-owning or re-ACLing a name in {app} that is also a name of a file elsewhere would change
// that other file - Setup acting on someone else's file on an ordinary user's behalf.
function LinkCountOf(const Path: String; var Attributes: Cardinal): Cardinal;
var
  FileHandle: Longint;
  Info: TByHandleFileInformation;
begin
  Result := 0;
  Attributes := 0;
  try
    FileHandle := CreateFileW(Path, LinkProbeAccess, LinkProbeShare, 0, LinkProbeDisposition,
      LinkProbeFlags, 0);
    if FileHandle = -1 then
      Exit;
    try
      if GetFileInformationByHandle(FileHandle, Info) <> 0 then
      begin
        Result := Info.nNumberOfLinks;
        Attributes := Info.dwFileAttributes;
      end;
    finally
      CloseHandle(FileHandle);
    end;
  except
    Log(GetExceptionMessage);
    Result := 0;
  end;
end;

// Make every file sitting directly in Dir administrator-owned and inheriting Dir's ACL.
// Dir's own DACL is set without propagation (ApplyDacl), so a file another account placed here
// before Setup ran keeps its OWNER (an owner can always rewrite its own ACL), its explicit
// grants and its old inherited entries; each top-level file is therefore re-owned and /reset
// (which re-derives that one file's entries from Dir) one by one. Only
// files are touched: directories are left to [InstallDelete] (which removes the code folders
// without following a link) and to note-images (whose own grant and images must never change),
// so a junction planted in Dir is skipped, never traversed - there is no recursion and no /T.
//
// Names that [InstallDelete] removes right afterwards (*.dll, qt.conf) are skipped: an installed
// folder holds about 250 files directly in {app}, nearly all of them DLLs, and two icacls runs
// each before deleting them anyway would cost some 500 process launches on every upgrade. That
// is sound because those names are deleted, not kept: Setup re-copies our own DLLs as new files
// that inherit the protected ACL. What it leaves open: [InstallDelete] ignores a failed delete,
// so a planted DLL that some process holds open without delete sharing is then neither removed
// nor re-owned (a check for the real machine; see the install matrix).
//
// Before icacls touches a file, its link count is read: a name that is a reparse point, that has
// more than one hard link, or whose count cannot be read stops Setup and nothing is changed or
// deleted - the user removes that entry and runs Setup again. The skipped names need no such
// check: they are only deleted, and deleting a link removes that name, not the other file.
// Returns '' on success, else the stopping message.
function ProtectTopLevelFiles(const Dir: String): String;
var
  FindRec: TFindRec;
  Target: String;
  Code: Integer;
  Links, Attributes: Cardinal;
begin
  Result := '';
  if FindFirst(AddBackslash(Dir) + '*', FindRec) then
  begin
    try
      repeat
        if ((FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) = 0)
          and (not IsRemovedByInstallDelete(FindRec.Name)) then
        begin
          Target := AddBackslash(Dir) + FindRec.Name;
          Links := LinkCountOf(Target, Attributes);
          Log(Format('Link check: "%s" has %d name(s), attributes $%x', [Target, Links, Attributes]));
          if ((FindRec.Attributes and FILE_ATTRIBUTE_REPARSE_POINT) <> 0)
            or ((Attributes and FILE_ATTRIBUTE_REPARSE_POINT) <> 0)
            or (Links <> 1) then
          begin
            Result := FmtMessage(CustomMessage('InstallDirFileIsLink'), [Target]);
            Break;
          end;
          Code := RunIcacls(Target, IcaclsOwnerArgs);
          if Code = 0 then
            Code := RunIcacls(Target, IcaclsResetArgs);
          if Code <> 0 then
          begin
            Result := FmtMessage(CustomMessage('InstallDirProtectFailed'), [Target, IntToStr(Code)]);
            Break;
          end;
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

// The install folder must not be writable by ordinary users: Setup and the uninstaller run
// elevated, every account runs the executables, and the bundled protocol profiles outrank
// the user-writable local ones because they sit here (docs/privacy-boundary.md). Program
// Files gives that for free; a folder on a data drive such as D:\ inherits the drive root's
// ACL, which commonly lets every user modify everything below it. So, at ssInstall - before
// [InstallDelete], [Dirs] and [Files] run ("Installation Order" in the Inno Setup help):
//   0. refuse a drive root: it has no parent whose ACL we could trust and cannot be protected;
//   1. refuse a folder that is a link: everything below would land where it points;
//   2. make Administrators the owner (icacls /setowner, which sets the owner of Dir alone - an
//      owner is never inherited): an owner can always rewrite the ACL, and a folder created
//      before Setup ran may belong to an ordinary user;
//   3. replace Dir's DACL with a protected one - Administrators and SYSTEM full control, Users
//      read and execute, inheritable - through ApplyDacl (SetFileSecurityW), which changes Dir
//      only. No ACL change Setup makes may reach a file that is merely hard-linked into the
//      folder, and a propagating call (icacls, SetNamedSecurityInfo) would rewrite the shared
//      descriptor of such a file. From here on no ordinary user can create, delete or rename
//      entries in Dir;
//   4. re-check, now that the protected ACL is in place, that Dir is still a real directory
//      and not a link: a swap between the earlier checks and here (its parent may let users
//      rename it - see docs) would otherwise go unnoticed;
//   5. note-images: refuse it if it is a link, create it if it is missing, then give it its own
//      protected DACL in which Users may also modify - again through ApplyDacl, so the images
//      already inside keep the ACLs they were created with and are never touched;
//   6. re-own and reset every file already sitting directly in Dir, so one an ordinary user
//      placed here before Setup ran cannot keep its owner or an explicit grant - except the
//      *.dll and qt.conf names [InstallDelete] deletes next; a file that is a link (reparse
//      point or extra hard link), or whose link count cannot be read, stops Setup instead
//      (ProtectTopLevelFiles);
//   7. write the resulting ACLs of Dir and note-images to the Setup log.
// Returns '' when the folder is protected, otherwise the message that stops Setup.
//
// What no longer happens: because step 3 does not propagate, nothing that already sits below
// Dir has its inherited entries re-derived from the new DACL. Each kind of existing entry is
// covered as follows:
//   - code folders (protocol-profiles, qml, the Qt plugin folders, docs): [InstallDelete]
//     removes them whole (a link among them is removed, not followed) and [Files] recreates
//     them; a folder Setup creates inherits Dir's entries on creation;
//   - files directly in Dir: re-owned and reset in step 6, or - *.dll and qt.conf - deleted by
//     [InstallDelete] and, for ours, copied again;
//   - files [Files] replaces: Setup writes each one to a new temporary file in the destination
//     folder, deletes the old file and renames the new one into place (Inno Setup source,
//     Setup.Install.pas, ProcessFileEntry: GenerateUniqueName ... TFile.Create(TempFile,
//     fdCreateAlways) ... DeleteFile(DestFile) ... MoveFile(TempFile, DestFile)), so a replaced
//     file is a new file and inherits the protected entries; the uninstaller EXE is replaced
//     the same way (MoveFileReplace). The uninstall log is the exception: when Setup appends
//     to an existing unins???.dat it reopens it in place (Setup.UninstallLog.pas,
//     TUninstallLog.Save: fdOpenExisting), keeping its old ACL - it sits directly in Dir, so
//     step 6 has already re-owned and reset it;
//   - note-images and the images in it: step 5;
//   - any other folder in Dir that is neither ours nor listed in [InstallDelete] keeps the ACL it
//     had. The programs load nothing from such a folder.
//
// To check on a real installation (Get-Acl, or icacls, on each path; the Setup log also lists
// {app} and note-images) - a fresh install to D:\, an upgrade over 1.5.0, and Program Files:
//   {app}                            protected; Administrators F, SYSTEM F, Users RX, each
//                                    (OI)(CI); owner Administrators
//   {app}\*.exe, {app}\unins000.exe  inherited entries only; no write right for Users,
//   {app}\unins000.dat               Authenticated Users or Everyone; owner Administrators
//   {app}\*.dll, loose top-level     a planted one is gone ([InstallDelete]); ours are
//                                    inherited entries only, owner Administrators
//   {app}\qml\... and plugin folders inherited entries only (recreated by Setup)
//   {app}\note-images                protected; Administrators F, SYSTEM F, Users M, each
//                                    (OI)(CI); no other entry
function ProtectInstallDirectory(const Dir: String): String;
var
  Code: Integer;
  Images: String;
begin
  Result := '';
  if IsDriveRoot(Dir) then
  begin
    Result := FmtMessage(CustomMessage('InstallDirIsDriveRoot'), [Dir]);
    Exit;
  end;
  // A link is never created over, even one whose target is gone.
  if (not IsReparsePoint(Dir)) and (not DirExists(Dir)) then
    ForceDirectories(Dir);
  if IsReparsePoint(Dir) then
  begin
    Result := FmtMessage(CustomMessage('InstallDirIsLink'), [Dir]);
    Exit;
  end;
  if not DirExists(Dir) then
  begin
    Result := FmtMessage(CustomMessage('InstallDirCreateFailed'), [Dir]);
    Exit;
  end;

  Code := RunIcacls(Dir, IcaclsOwnerArgs);
  if Code = 0 then
    Code := ApplyDacl(Dir, AppDirSddl);
  if Code <> 0 then
  begin
    Result := FmtMessage(CustomMessage('InstallDirProtectFailed'), [Dir, IntToStr(Code)]);
    Exit;
  end;

  // Re-verify after protecting: if Dir was swapped for a link since the checks above, stop
  // before [Files] writes anything through it (RedirectionGuard also blocks the traversal).
  if IsReparsePoint(Dir) then
  begin
    Result := FmtMessage(CustomMessage('InstallDirIsLink'), [Dir]);
    Exit;
  end;
  if not DirExists(Dir) then
  begin
    Result := FmtMessage(CustomMessage('InstallDirCreateFailed'), [Dir]);
    Exit;
  end;

  // note-images: a link is refused before anything is granted; a missing folder is created
  // here (by Setup, inside the now protected Dir) so that its grant is set by ApplyDacl rather
  // than by [Dirs].
  Images := AddBackslash(Dir) + NoteImagesDirName;
  if IsReparsePoint(Images) then
  begin
    Result := FmtMessage(CustomMessage('NoteImagesIsLink'), [Images]);
    Exit;
  end;
  if not DirExists(Images) then
    CreateDir(Images);
  if IsReparsePoint(Images) or (not DirExists(Images)) then
  begin
    Result := FmtMessage(CustomMessage('InstallDirCreateFailed'), [Images]);
    Exit;
  end;
  // Administrators own the folder (the call changes the folder alone, nothing below it): an
  // ordinary user who created it before Setup ran would otherwise stay its owner, and an owner
  // can rewrite the grant.
  Code := RunIcacls(Images, IcaclsOwnerArgs);
  if Code = 0 then
    Code := ApplyDacl(Images, NoteImagesSddl);
  if Code <> 0 then
  begin
    Result := FmtMessage(CustomMessage('NoteImagesGrantFailed'), [Images, IntToStr(Code)]);
    Exit;
  end;

  Result := ProtectTopLevelFiles(Dir);
  LogAcl(Dir);
  LogAcl(Images);
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
  DownloadPage := CreateDownloadPage(SetupMessage(msgWizardPreparing), SetupMessage(msgPreparingDesc), @OnDownloadProgress); // BOUNDARY-ALLOW(NET-009): the pinned, SHA-256 checked Npcap download; Npcap's licence forbids bundling it
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
  Failure: String;
begin
  if CurStep = ssInstall then
  begin
    // Before anything is written; see ProtectInstallDirectory. Abort from ssInstall ends
    // Setup ("Abort" in the Inno Setup help), and nothing has been installed yet.
    Failure := ProtectInstallDirectory(ExpandConstant('{app}'));
    if Failure <> '' then
    begin
      Log('Install folder not protected: ' + Failure);
      SuppressibleMsgBox(Failure, mbCriticalError, MB_OK, IDOK);
      Abort;
    end;
  end;

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
var
  Dir: String;
begin
  Result := True;
  if CurPageID = wpSelectDir then
  begin
    // ProtectInstallDirectory will make the chosen folder modifiable by administrators only.
    // A drive root, a link, or a folder that already holds someone's other files is refused
    // here, while another folder can still be chosen; an earlier installation of this
    // application is not. These same refusals are enforced again at ssInstall, so a silent
    // install that never shows this page is covered too.
    Dir := WizardDirValue;
    if IsDriveRoot(Dir) then
    begin
      SuppressibleMsgBox(FmtMessage(CustomMessage('InstallDirIsDriveRoot'), [Dir]), mbError, MB_OK, IDOK);
      Result := False;
    end
    else if IsReparsePoint(Dir) then
    begin
      SuppressibleMsgBox(FmtMessage(CustomMessage('InstallDirIsLink'), [Dir]), mbError, MB_OK, IDOK);
      Result := False;
    end
    else if DirExists(Dir) and DirectoryHasEntries(Dir) and (not HoldsThisApplication(Dir)) then
    begin
      SuppressibleMsgBox(FmtMessage(CustomMessage('InstallDirNotEmpty'), [Dir]), mbError, MB_OK, IDOK);
      Result := False;
    end;
  end;
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
  ImagesDir: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    // The profile the *installing* user named, not the one this uninstaller happens to
    // be elevated as. Falls back to this account only when nothing was recorded.
    DataRoot := UninstallUserDataDir;
    if DataRoot = '' then
      DataRoot := ExpandConstant('{localappdata}');
    DataDir := AddBackslash(DataRoot) + 'MentorRecorder';
    // Not part of the data this prompt offers to delete ([Dirs] uninsneveruninstall); the
    // prompt names it so the answer is not mistaken for "everything is gone".
    ImagesDir := AddBackslash(ExpandConstant('{app}')) + NoteImagesDirName;

    // Nothing is ever deleted without this prompt, and its default answer is "No".
    if DirExists(DataDir) then
      if MsgBox(FmtMessage(CustomMessage('RemoveUserData'), [DataDir, ImagesDir]), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(DataDir, True, True, True);

    RegDeleteKeyIncludingSubkeys(HKEY_LOCAL_MACHINE, UserDataRegKey);
  end;
end;
