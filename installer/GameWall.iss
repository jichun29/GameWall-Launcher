; ───────────────────────────────────────────────────────────────
;  GameWall 安装包脚本（Inno Setup 6）
;
;  编译：ISCC.exe installer\GameWall.iss
;  产物：dist\GameWall-Setup-<版本>.exe
;
;  设计取舍：
;  1. 默认装到「非系统盘」——用户明确要求不要把游戏工具塞进 C 盘。
;     装到用户目录（PrivilegesRequired=lowest）是为了不弹 UAC，
;     普通用户也能装到 D:\ / E:\ 的自定义目录。
;  2. 桌面放一个快捷方式，开始菜单也放一个。
;  3. 卸载时只删程序本身，绝不动 %AppData%\GameWall 里的游戏库、
;     封面和优化备份——那是用户的资料，不是安装内容。
; ───────────────────────────────────────────────────────────────

#define AppName "GameWall"
; 版本号从 dist\GameWall.exe 的文件版本里现读，避免脚本和 csproj 各写一份对不上。
; GetFileVersion 返回的是四段式（1.0.15.0），这里截成三段，跟 GitHub tag 的写法一致。
#define AppVersionFull GetFileVersion("..\dist\GameWall.exe")
#define AppVersion Copy(AppVersionFull, 1, RPos('.', AppVersionFull) - 1)
#define AppPublisher "季春二九"
#define AppUrl "https://github.com/jichun29/GameWall-Launcher"
#define AppExe "GameWall.exe"

[Setup]
AppId={{8F3C1D42-6A7B-4E59-9C21-5D8E7B4A3F10}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
VersionInfoVersion={#AppVersion}
VersionInfoDescription={#AppName} 安装程序
VersionInfoCompany={#AppPublisher}
VersionInfoCopyright=© 2026 {#AppPublisher}

; 安装位置由下面的 PickInstallDir() 决定：优先非系统盘
DefaultDirName={code:PickInstallDir}
DisableDirPage=no
DisableProgramGroupPage=yes
DefaultGroupName={#AppName}
AllowNoIcons=yes

; 装到用户可写的位置，不触发 UAC
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0

; 单文件已经自包含运行时，压缩率用最高档
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ShowLanguageDialog=no
OutputDir=..\dist
OutputBaseFilename={#AppName}-Setup-{#AppVersion}
SetupIconFile=..\src\GameWall\Assets\GameWall.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName} {#AppVersion}

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "在桌面创建快捷方式"; GroupDescription: "附加任务:"; Flags: checkedonce

[Files]
Source: "..\dist\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
; 桌面快捷方式（用户勾选了才建）
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; IconFilename: "{app}\{#AppExe}"; Comment: "轻量 Windows 游戏封面墙"; Tasks: desktopicon
; 开始菜单
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; IconFilename: "{app}\{#AppExe}"; Comment: "轻量 Windows 游戏封面墙"
Name: "{group}\卸载 {#AppName}"; Filename: "{uninstallexe}"

[Run]
Filename: "{app}\{#AppExe}"; Description: "立即启动 {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; 只清安装目录自己产生的东西。%AppData%\GameWall 一律不碰。
Type: filesandordirs; Name: "{app}"

[Code]
// ─────────────────────────────────────────────────────────────
//  挑一个默认安装目录：优先非系统盘。
//
//  规则（按顺序）：
//   1. 枚举所有固定磁盘，排除系统盘（Windows 所在的那个盘）；
//   2. 剩下的盘里挑剩余空间最大的那个；
//   3. 该盘有 Program Files 就用它，没有就直接放盘符根下的 GameWall；
//   4. 一个非系统盘都没有（只有 C 盘）时，回退到系统盘的 Program Files。
//
//  注意：这里一律用 // 注释。Pascal 的花括号注释不支持嵌套，
//  注释里写 {sd} 这类常量会让编译器把第一个 } 当成注释结束，报语法错误。
// ─────────────────────────────────────────────────────────────

type
  TDriveCandidate = record
    Root: string;
    FreeBytes: Int64;
    TotalBytes: Int64;
  end;

function GetDriveFreeSpace(const Root: string; var FreeBytes, TotalBytes: Int64): Boolean;
begin
  // Inno Setup 的这个 API 只有三个参数：路径、可用空间、总空间
  Result := GetSpaceOnDisk64(Root, FreeBytes, TotalBytes);
end;

// 判断是不是本地固定磁盘（不是光驱、U 盘、网络盘）。
// Inno 的 Pascal Script 没有内置 GetDriveType，直接声明 kernel32 的导出函数。
function GetDriveTypeW(lpRootPathName: string): Cardinal;
  external 'GetDriveTypeW@kernel32.dll stdcall';

function IsFixedDrive(const Root: string): Boolean;
begin
  Result := GetDriveTypeW(Root) = 3; // DRIVE_FIXED
end;

function PickInstallDir(Param: string): string;
var
  Drives: array of TDriveCandidate;
  Count, I, BestIndex: Integer;
  LetterCode: Integer;
  Root, SystemRoot: string;
  FreeBytes, TotalBytes: Int64;
  BestFree: Int64;
  Preferred: string;
begin
  SystemRoot := ExpandConstant('{sd}');
  SetLength(Drives, 0);

  // 枚举 A: ~ Z:，只看固定磁盘。
  // Pascal Script 的 for 循环不支持字符类型，得用序号再转成字母。
  for LetterCode := Ord('A') to Ord('Z') do
  begin
    Root := Chr(LetterCode) + ':\';
    if not IsFixedDrive(Root) then
      continue;

    // 跳过系统盘
    if CompareText(Copy(Root, 1, 2), SystemRoot) = 0 then
      continue;

    if not GetDriveFreeSpace(Root, FreeBytes, TotalBytes) then
      continue;

    // 太小的盘（< 2GB 可用）不参与，免得装不下或者把别人的盘塞满
    if FreeBytes < (2 * 1024 * 1024 * 1024) then
      continue;

    Count := Length(Drives);
    SetLength(Drives, Count + 1);
    Drives[Count].Root := Root;
    Drives[Count].FreeBytes := FreeBytes;
    Drives[Count].TotalBytes := TotalBytes;
  end;

  if Length(Drives) = 0 then
  begin
    // 只有系统盘，只能装 C 盘
    Result := ExpandConstant('{autopf}\GameWall');
    exit;
  end;

  // 挑剩余空间最大的
  BestIndex := 0;
  BestFree := Drives[0].FreeBytes;
  for I := 1 to High(Drives) do
  begin
    if Drives[I].FreeBytes > BestFree then
    begin
      BestFree := Drives[I].FreeBytes;
      BestIndex := I;
    end;
  end;

  Preferred := Drives[BestIndex].Root + 'Program Files\GameWall';

  // 盘上没有 Program Files 目录就直接放根下
  if not DirExists(Drives[BestIndex].Root + 'Program Files') then
    Preferred := Drives[BestIndex].Root + 'GameWall';

  Result := Preferred;
end;
