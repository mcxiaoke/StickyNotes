; ============================================================================
; 通用 Windows 应用 Inno Setup 安装脚本 — 核心模板
; ============================================================================
; 基于 CarroDesk.iss 抽取泛化而成，应用无关；每个应用只需一个十几行的壳脚本
; （参考 StickyNotes.iss）定义下方参数后 #include 本文件即可复用。
;
; 由发布脚本调用 iscc 编译，支持通过 /D 传入动态参数：
;   AppVersion      版本号（如 1.2.3）
;   PayloadDir      发布产物目录（含主程序 exe）
;   IconFile        应用图标绝对路径（可选，缺省使用 Inno 默认图标）
;
; 可通过 /D 覆盖的可选参数（通常由壳脚本定义，命令行优先级更高）：
;   AppCliExeName        命令行工具 exe 文件名（可选，未定义则不打包/不检测）
;   AppLicenseFile       许可协议文本路径（可选，定义后显示许可页）
;
; 壳脚本必须定义：
;   AppId                唯一 GUID（形如 {{XXXXXXXX-XXXX-...}，决定升级识别）
;   AppName              应用名
;
; 壳脚本可选定义（有默认值）：
;   AppExeName           主程序文件名（默认 AppName + ".exe"）
;   AppPublisher         发布者（默认 AppName）
;   AppURL               主页/支持 URL（默认不显示）
;   AppDataDirRoot       漫游数据目录根（默认 {userappdata}，即 %APPDATA%；
;                        若应用数据在 %LOCALAPPDATA% 则定义为 {localappdata}）
;   AppDataDirName       漫游数据目录名（默认 AppName）
;   SetupOutputBaseName  安装包输出文件名（默认 AppName + "-setup"）
;
; 特性亮点（与 CarroDesk 一致）：
;   1. Per-user 安装（PrivilegesRequired=lowest），默认装到 %LOCALAPPDATA%\Programs\<AppName>，全程免 UAC 提权；
;   2. 现代向导样式，支持简体中文与英文多语言，语言智能匹配系统环境；
;   3. 自动继承已有安装路径（智能纠偏，AppendDefaultDirName=no 严禁重复追加目录层级）；
;   4. 安装与卸载前检测运行中的主程序（及可选 CLI），友好提示并安全结束进程树；
;   5. 数据模式自定义页：
;      - 漫游模式（推荐）：数据在 AppDataDirRoot\AppDataDirName，生成开始菜单项与系统卸载信息；
;      - 便携模式：数据在安装目录 AppPortableDataDirName（默认 app_data）下，生成 AppPortableFlagName（默认 portable.ini）标记，纯绿色零系统残留；
;      - 智能预选：目标目录已有便携标记/数据目录时自动预选便携模式，切换模式时数据平滑迁移；
;   6. 发布目录整体安装，自动排除 *.pdb / 便携数据；*.sample.* 样例文件仅在目标不存在时复制；
;   7. 卸载时保护用户数据：若数据目录中仍有用户文件，保留数据并给出提示。

; ---------------------------------------------------------------------------
; 编译期参数默认值
; ---------------------------------------------------------------------------

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

#ifndef PayloadDir
  #define PayloadDir "..\..\dist\publish"
#endif

#ifndef AppExeName
  #define AppExeName AppName + ".exe"
#endif

#ifndef AppPublisher
  #define AppPublisher AppName
#endif

#ifndef AppDataDirName
  #define AppDataDirName AppName
#endif

#ifndef AppDataDirRoot
  #define AppDataDirRoot "{userappdata}"
#endif

#ifndef SetupOutputBaseName
  #define SetupOutputBaseName AppName + "-setup"
#endif

; 便携模式标记文件名与数据目录名（默认与 CarroDesk/StickyNotes 一致；
; 修改时必须同步应用代码中的读取逻辑，否则应用将无法识别便携模式）
#ifndef AppPortableFlagName
  #define AppPortableFlagName "portable.ini"
#endif

#ifndef AppPortableDataDirName
  #define AppPortableDataDirName "app_data"
#endif

; 主打包行的排除规则：pdb、样例、便携数据；AppPreserveDirs 追加在后
; （AppPreserveDirs 为逗号分隔目录名，升级时不可覆盖，卸载时自然保留）
#define BaseExcludes "*.pdb,*.sample.*," + AppPortableDataDirName + "\*," + AppPortableFlagName + "," + AppPortableFlagName + ".bak"
#ifdef AppPreserveDirs
  #define MainExcludes BaseExcludes + "," + StringChange(AppPreserveDirs, ",", "\*,") + "\*"
#else
  #define MainExcludes BaseExcludes
#endif

; 壳脚本必填项校验
#ifndef AppId
  #error 请在应用壳脚本中定义 AppId（形如 {{XXXXXXXX-XXXX-XXXX-XXXX-XXXXXXXXXXXX} 的唯一 GUID）
#endif
#ifndef AppName
  #error 请在应用壳脚本中定义 AppName
#endif

; 发布目录完整性校验（编译期即失败，避免打出一个空安装包）
#if !FileExists(AddBackslash(PayloadDir) + AppExeName)
  #error PayloadDir 中未找到主程序，请检查 /DPayloadDir 参数
#endif

[Setup]
AppId={#AppId}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
#ifdef AppURL
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}
AppUpdatesURL={#AppURL}
#endif
#ifdef AppLicenseFile
LicenseFile={#AppLicenseFile}
#endif

; 安装路径推导与权限（免管理员提权，Per-user 安装）
DefaultDirName={code:GetDefaultInstallDir}
DefaultGroupName={#AppName}
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

; 彻底禁用自动追加尾部目录名（解决用户选择已有目录时被多追加一层 AppName 的问题）
AppendDefaultDirName=no
; 禁用 Inno 自动读取历史，统一由 GetDefaultInstallDir 智能推导与清洗校验
UsePreviousAppDir=no

; 架构与安装包配置
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#ifdef IconFile
SetupIconFile={#IconFile}
#endif
UninstallDisplayIcon={app}\{#AppExeName}
OutputDir=.
OutputBaseFilename={#SetupOutputBaseName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ShowLanguageDialog=auto
DisableProgramGroupPage=yes
DisableDirPage=no
DirExistsWarning=no

; 进程关闭检测
CloseApplications=yes
CloseApplicationsFilter=*.exe

[Languages]
Name: "chinesesimplified"; MessagesFile: "Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
chinesesimplified.DataModeTitle=数据模式选择
chinesesimplified.DataModeSubTitle=选择 {#AppName} 的配置与数据存储位置
chinesesimplified.DataModePrompt=请选择程序运行时配置与数据的存放方式：
chinesesimplified.DataModeRoamingTitle=漫游模式 (推荐)
chinesesimplified.DataModeRoamingDesc=数据存放于 %1%n适合单机固定安装，生成开始菜单快捷方式与卸载项，升级或移动目录不影响个人数据。
chinesesimplified.DataModePortableTitle=便携模式 (纯绿色)
chinesesimplified.DataModePortableDesc=数据存放于安装目录下的 {#AppPortableDataDirName} 文件夹%n与程序文件整合在同一目录，零系统残留，不写开始菜单和卸载注册表，拷贝整个目录即可迁移。
chinesesimplified.DataModeCurrentDir=当前安装目录：
chinesesimplified.DetectedPortableHint=检测到所选目录已有便携版数据，已为您自动预选便携模式。
chinesesimplified.AppRunningWarning=检测到 %1 正在运行。%n%n安装/升级需要先结束相关进程，否则文件无法被覆盖。%n%n是否现在立即结束这些进程并继续？
chinesesimplified.AppRunningUninstallWarning=检测到 %1 正在运行。%n%n卸载需要先结束相关进程。%n%n是否现在立即结束这些进程并继续？
chinesesimplified.CloseProcessFailed=无法结束运行中的进程，请手动关闭后再试。
chinesesimplified.InstallAborted=安装已取消：请先退出正在运行的程序。
chinesesimplified.UninstallProcessRunning=仍有进程正在运行，卸载终止。请手动关闭后再试。
chinesesimplified.OpenInstallDir=打开安装目录
chinesesimplified.UninstallRetainData=检测到 %1 中仍有用户数据，已为您妥善保留。如需彻底清除请手动删除。
chinesesimplified.DataLocation=数据存放位置：
chinesesimplified.ProgramPath=程序安装路径：

english.DataModeTitle=Data Mode Selection
english.DataModeSubTitle=Choose where {#AppName} stores configurations and data
english.DataModePrompt=Please choose how runtime configurations and data will be stored:
english.DataModeRoamingTitle=Roaming Mode (Recommended)
english.DataModeRoamingDesc=Data is stored in %1%nBest for standard installation. Creates Start Menu shortcuts and uninstaller. Personal data remains safe on update.
english.DataModePortableTitle=Portable Mode (Green / Standalone)
english.DataModePortableDesc=Data is stored in the {#AppPortableDataDirName} folder under the installation directory.%nFully portable with zero system footprint. No Start Menu shortcuts or registry uninstaller created.
english.DataModeCurrentDir=Installation directory:
english.DetectedPortableHint=Detected existing portable data in the target folder; portable mode has been preselected for you.
english.AppRunningWarning=%1 is currently running.%n%nSetup needs to close running processes to overwrite files.%n%nDo you want to close these processes and continue?
english.AppRunningUninstallWarning=%1 is currently running.%n%nUninstall needs to close running processes.%n%nDo you want to close these processes and continue?
english.CloseProcessFailed=Failed to close the running processes. Please close them manually and try again.
english.InstallAborted=Setup cancelled: please exit the running application first.
english.UninstallProcessRunning=Processes are still running. Uninstall aborted. Please close them manually and try again.
english.OpenInstallDir=Open installation folder
english.UninstallRetainData=User data still exists in %1 and has been preserved. You can delete it manually if no longer needed.
english.DataLocation=Data location:
english.ProgramPath=Program path:

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked; Check: IsRoamingMode

[Files]
; 发布目录整体安装（排除 pdb、样例文件、便携数据与 AppPreserveDirs 保留目录）
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs; Excludes: "{#MainExcludes}"
; 样例文件：仅在目标不存在时复制，避免覆盖用户已修改的样例（无样例文件时自动跳过）
Source: "{#PayloadDir}\*.sample.*"; DestDir: "{app}"; Flags: ignoreversion onlyifdoesntexist skipifsourcedoesntexist

[Icons]
Name: "{userprograms}\{#AppName}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Check: IsRoamingMode
Name: "{userprograms}\{#AppName}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"; Check: IsRoamingMode
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon; Check: IsRoamingMode

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(AppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
Filename: "{app}"; Description: "{cm:OpenInstallDir}"; Flags: postinstall shellexec skipifsilent unchecked

[Code]
var
  DataModePage: TWizardPage;
  RoamingRadio: TNewRadioButton;
  PortableRadio: TNewRadioButton;
  PromptLabel: TNewStaticText;
  DescRoaming: TNewStaticText;
  DescPortable: TNewStaticText;
  HintLabel: TNewStaticText;
  DirLabel: TNewStaticText;
  SelectedDataMode: Integer; // 0 = 漫游模式 (Roaming), 1 = 便携模式 (Portable)
  OriginalDataMode: Integer; // 检测到的原始数据模式

// ================================================================
// 辅助函数：常用路径
// ================================================================

function RoamingDataDir(): string;
begin
  Result := AddBackslash(ExpandConstant('{#AppDataDirRoot}')) + '{#AppDataDirName}';
end;

// ================================================================
// 辅助函数：进程管理
// ================================================================

function IsProcessRunning(const ExeName: string): Boolean;
var
  TempFile: string;
  Cmd: string;
  Output: AnsiString;
  ResultCode: Integer;
begin
  Result := False;
  TempFile := ExpandConstant('{tmp}\proclist_' + ExeName + '.txt');
  Cmd := '/c tasklist /NH /FO CSV /FI "IMAGENAME eq ' + ExeName + '" > "' + TempFile + '"';
  if Exec(ExpandConstant('{cmd}'), Cmd, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    if LoadStringFromFile(TempFile, Output) then
    begin
      if Pos(Lowercase(ExeName), Lowercase(string(Output))) > 0 then
        Result := True;
    end;
    DeleteFile(TempFile);
  end;
end;

function AnyAppRunning(): Boolean;
begin
  Result := IsProcessRunning('{#AppExeName}');
#ifdef AppCliExeName
  Result := Result or IsProcessRunning('{#AppCliExeName}');
#endif
end;

procedure KillAppProcesses();
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{cmd}'), '/c taskkill /F /T /IM "{#AppExeName}"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
#ifdef AppCliExeName
  Exec(ExpandConstant('{cmd}'), '/c taskkill /F /T /IM "{#AppCliExeName}"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
#endif
  Sleep(500);
end;

// ================================================================
// 辅助函数：目录检测与递归复制
// ================================================================

function DirHasFiles(const ADir: string): Boolean;
var
  FR: TFindRec;
begin
  Result := False;
  if not DirExists(ADir) then
    Exit;
  if FindFirst(ADir + '\*', FR) then
  try
    repeat
      if (FR.Name <> '.') and (FR.Name <> '..') then
      begin
        if FR.Attributes and FILE_ATTRIBUTE_DIRECTORY <> 0 then
        begin
          if DirHasFiles(ADir + '\' + FR.Name) then
          begin
            Result := True;
            Exit;
          end;
        end
        else
        begin
          Result := True;
          Exit;
        end;
      end;
    until not FindNext(FR);
  finally
    FindClose(FR);
  end;
end;

procedure CopyDirTree(const SrcDir, DstDir: string);
var
  FR: TFindRec;
  SrcPath, DstPath: string;
begin
  if not DirExists(SrcDir) then
    Exit;
  ForceDirectories(DstDir);
  if FindFirst(SrcDir + '\*', FR) then
  try
    repeat
      if (FR.Name <> '.') and (FR.Name <> '..') then
      begin
        SrcPath := SrcDir + '\' + FR.Name;
        DstPath := DstDir + '\' + FR.Name;
        if FR.Attributes and FILE_ATTRIBUTE_DIRECTORY <> 0 then
          CopyDirTree(SrcPath, DstPath)
        else if not FileExists(DstPath) then
          CopyFile(SrcPath, DstPath, False);
      end;
    until not FindNext(FR);
  finally
    FindClose(FR);
  end;
end;

procedure DeleteSampleFiles(const AppDir: string);
var
  FR: TFindRec;
begin
  if FindFirst(AppDir + '\*.sample.*', FR) then
  try
    repeat
      if FR.Attributes and FILE_ATTRIBUTE_DIRECTORY = 0 then
        DeleteFile(AppDir + '\' + FR.Name);
    until not FindNext(FR);
  finally
    FindClose(FR);
  end;
end;

// ================================================================
// 辅助函数：安装路径智能纠偏与默认推导
// ================================================================

function NormalizeInstallPath(const InPath: string): string;
var
  P, Parent: string;
begin
  P := RemoveBackslashUnlessRoot(InPath);
  Parent := ExtractFileDir(P);
  // 若出现类似 ...\AppName\AppName 的嵌套，且父目录已是应用根目录，则自动纠正为父目录
  if (Parent <> '') and (Lowercase(ExtractFileName(P)) = Lowercase('{#AppName}')) then
  begin
    if FileExists(Parent + '\{#AppPortableFlagName}') or FileExists(Parent + '\{#AppExeName}') then
    begin
      P := Parent;
    end
    else if Lowercase(ExtractFileName(Parent)) = Lowercase('{#AppName}') then
    begin
      P := Parent;
    end;
  end;
  Result := P;
end;

function GetDefaultInstallDir(Param: string): string;
var
  SrcDir: string;
  RegPath: string;
begin
  // 1. 若安装包运行在已有应用目录下（便携版就地升级），优先使用安装包所在目录
  SrcDir := ExpandConstant('{src}');
  if FileExists(SrcDir + '\{#AppPortableFlagName}') or FileExists(SrcDir + '\{#AppExeName}') then
  begin
    Result := SrcDir;
    Exit;
  end;

  // 2. 检查 Inno Setup 历史安装注册表并清洗纠正
  if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#AppId}_is1', 'InstallLocation', RegPath) and (RegPath <> '') then
  begin
    RegPath := NormalizeInstallPath(RegPath);
    if DirExists(RegPath) then
    begin
      Result := RegPath;
      Exit;
    end;
  end;

  // 3. 默认漫游安装路径
  Result := ExpandConstant('{localappdata}\Programs\{#AppName}');
end;

// ================================================================
// 准备安装：检测运行中进程
// ================================================================

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if AnyAppRunning() then
  begin
    if MsgBox(FmtMessage(CustomMessage('AppRunningWarning'), ['{#AppName}']), mbConfirmation, MB_YESNO or MB_DEFBUTTON1) = IDYES then
    begin
      KillAppProcesses();
      if AnyAppRunning() then
      begin
        Result := CustomMessage('CloseProcessFailed');
      end;
    end
    else
    begin
      Result := CustomMessage('InstallAborted');
    end;
  end;
end;

// ================================================================
// 判断是否为漫游模式（用于 Icons 与 Tasks 的 Check 回调）
// ================================================================

function IsRoamingMode(): Boolean;
begin
  Result := (SelectedDataMode = 0);
end;

function IsPortableMode(): Boolean;
begin
  Result := (SelectedDataMode = 1);
end;

// ================================================================
// 自定义页面：数据模式选择
// ================================================================

procedure UpdateDataModeInfo();
var
  TargetDir: string;
begin
  TargetDir := WizardDirValue;
  if SelectedDataMode = 1 then
  begin
    DirLabel.Caption := CustomMessage('DataLocation') + TargetDir + '\{#AppPortableDataDirName}' + #13#10 +
      CustomMessage('ProgramPath') + TargetDir;
  end
  else
  begin
    DirLabel.Caption := CustomMessage('DataLocation') + RoamingDataDir() + #13#10 +
      CustomMessage('ProgramPath') + TargetDir;
  end;
end;

procedure UpdateDataModeSelection();
var
  TargetDir: string;
  HasPortableIni: Boolean;
  HasAppDataDir: Boolean;
begin
  TargetDir := WizardDirValue;
  HasPortableIni := FileExists(TargetDir + '\{#AppPortableFlagName}');
  HasAppDataDir := DirExists(TargetDir + '\{#AppPortableDataDirName}');

  if HasPortableIni or HasAppDataDir then
  begin
    OriginalDataMode := 1;
    SelectedDataMode := 1;
    PortableRadio.Checked := True;
    RoamingRadio.Checked := False;
    HintLabel.Caption := '★ ' + CustomMessage('DetectedPortableHint');
    HintLabel.Visible := True;
  end
  else
  begin
    OriginalDataMode := 0;
    SelectedDataMode := 0;
    RoamingRadio.Checked := True;
    PortableRadio.Checked := False;
    HintLabel.Visible := False;
  end;

  UpdateDataModeInfo();
end;

procedure RadioClick(Sender: TObject);
begin
  if RoamingRadio.Checked then
    SelectedDataMode := 0
  else
    SelectedDataMode := 1;
  UpdateDataModeInfo();
end;

procedure CreateDataModePage();
begin
  DataModePage := CreateCustomPage(wpSelectDir, CustomMessage('DataModeTitle'), CustomMessage('DataModeSubTitle'));

  PromptLabel := TNewStaticText.Create(DataModePage);
  PromptLabel.Parent := DataModePage.Surface;
  PromptLabel.Caption := CustomMessage('DataModePrompt');
  PromptLabel.Left := ScaleX(0);
  PromptLabel.Top := ScaleY(0);
  PromptLabel.Width := DataModePage.SurfaceWidth;
  PromptLabel.Height := ScaleY(24);
  PromptLabel.AutoSize := False;
  PromptLabel.ShowAccelChar := False;

  // 漫游模式单选
  RoamingRadio := TNewRadioButton.Create(DataModePage);
  RoamingRadio.Parent := DataModePage.Surface;
  RoamingRadio.Caption := CustomMessage('DataModeRoamingTitle');
  RoamingRadio.Left := ScaleX(8);
  RoamingRadio.Top := ScaleY(26);
  RoamingRadio.Width := DataModePage.SurfaceWidth - ScaleX(16);
  RoamingRadio.Font.Style := [fsBold];
  RoamingRadio.OnClick := @RadioClick;

  DescRoaming := TNewStaticText.Create(DataModePage);
  DescRoaming.Parent := DataModePage.Surface;
  DescRoaming.Caption := FmtMessage(CustomMessage('DataModeRoamingDesc'), [RoamingDataDir()]);
  DescRoaming.Left := ScaleX(26);
  DescRoaming.Top := ScaleY(48);
  DescRoaming.Width := DataModePage.SurfaceWidth - ScaleX(36);
  DescRoaming.Height := ScaleY(36);
  DescRoaming.AutoSize := False;
  DescRoaming.ShowAccelChar := False;

  // 便携模式单选
  PortableRadio := TNewRadioButton.Create(DataModePage);
  PortableRadio.Parent := DataModePage.Surface;
  PortableRadio.Caption := CustomMessage('DataModePortableTitle');
  PortableRadio.Left := ScaleX(8);
  PortableRadio.Top := ScaleY(90);
  PortableRadio.Width := DataModePage.SurfaceWidth - ScaleX(16);
  PortableRadio.Font.Style := [fsBold];
  PortableRadio.OnClick := @RadioClick;

  DescPortable := TNewStaticText.Create(DataModePage);
  DescPortable.Parent := DataModePage.Surface;
  DescPortable.Caption := CustomMessage('DataModePortableDesc');
  DescPortable.Left := ScaleX(26);
  DescPortable.Top := ScaleY(112);
  DescPortable.Width := DataModePage.SurfaceWidth - ScaleX(36);
  DescPortable.Height := ScaleY(36);
  DescPortable.AutoSize := False;
  DescPortable.ShowAccelChar := False;

  // 提示信息标签（检测到已有便携版时显示）
  HintLabel := TNewStaticText.Create(DataModePage);
  HintLabel.Parent := DataModePage.Surface;
  HintLabel.Left := ScaleX(8);
  HintLabel.Top := ScaleY(154);
  HintLabel.Width := DataModePage.SurfaceWidth - ScaleX(16);
  HintLabel.Height := ScaleY(22);
  HintLabel.Font.Color := clHighlight;
  HintLabel.Font.Style := [fsBold];
  HintLabel.AutoSize := False;
  HintLabel.ShowAccelChar := False;
  HintLabel.Visible := False;

  // 路径与状态展示标签
  DirLabel := TNewStaticText.Create(DataModePage);
  DirLabel.Parent := DataModePage.Surface;
  DirLabel.Left := ScaleX(8);
  DirLabel.Top := ScaleY(180);
  DirLabel.Width := DataModePage.SurfaceWidth - ScaleX(16);
  DirLabel.Height := ScaleY(40);
  DirLabel.AutoSize := False;
  DirLabel.ShowAccelChar := False;
end;

procedure InitializeWizard();
begin
  SelectedDataMode := 0;
  OriginalDataMode := -1;
  CreateDataModePage();
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if CurPageID = wpSelectDir then
  begin
    // 离开目录选择页时，自动将可能选中的嵌套目录规范化
    WizardForm.DirEdit.Text := NormalizeInstallPath(WizardForm.DirEdit.Text);
  end;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID = DataModePage.ID then
  begin
    // 动态同步各控件宽度，适配不同 DPI 缩放并保证换行正常
    PromptLabel.Width := DataModePage.SurfaceWidth;
    RoamingRadio.Width := DataModePage.SurfaceWidth - ScaleX(16);
    DescRoaming.Width := DataModePage.SurfaceWidth - ScaleX(36);
    PortableRadio.Width := DataModePage.SurfaceWidth - ScaleX(16);
    DescPortable.Width := DataModePage.SurfaceWidth - ScaleX(36);
    HintLabel.Width := DataModePage.SurfaceWidth - ScaleX(16);
    DirLabel.Width := DataModePage.SurfaceWidth - ScaleX(16);

    UpdateDataModeSelection();
  end;
end;

// ================================================================
// 数据迁移逻辑（模式切换时，目标已存在的同名文件一律跳过不覆盖）
// ================================================================

procedure MigrateDataIfNeeded();
var
  SrcDir, DstDir: string;
begin
  if (OriginalDataMode = -1) or (OriginalDataMode = SelectedDataMode) then
    Exit;

  if SelectedDataMode = 1 then
  begin
    // 从漫游切换到便携
    SrcDir := RoamingDataDir();
    DstDir := ExpandConstant('{app}') + '\{#AppPortableDataDirName}';
  end
  else
  begin
    // 从便携切换到漫游
    SrcDir := ExpandConstant('{app}') + '\{#AppPortableDataDirName}';
    DstDir := RoamingDataDir();
  end;

  CopyDirTree(SrcDir, DstDir);
end;

// ================================================================
// 安装后收尾与清理
// ================================================================

procedure CleanPortableFootprint();
var
  AppDir: string;
begin
  AppDir := ExpandConstant('{app}');
  // 1. 便携模式：彻底清理 Inno Setup 生成的卸载注册表与同名旧版注册表
  RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#AppId}_is1');
  RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#AppName}');

  // 2. 删除卸载器相关文件，达到 100% 纯绿色
  DeleteFile(AppDir + '\unins000.exe');
  DeleteFile(AppDir + '\unins000.dat');
  DeleteFile(AppDir + '\unins000.msg');
end;

procedure WriteReadmeFile(const AppDir, DataDir: string);
var
  Lines: TStringList;
begin
  Lines := TStringList.Create();
  try
    Lines.Add('{#AppName} {#AppVersion}');
    Lines.Add('-----------------------------------------');
    Lines.Add('安装目录：' + AppDir);
    Lines.Add('数据目录：' + DataDir);
    if SelectedDataMode = 1 then
    begin
      Lines.Add('数据模式：便携模式（纯绿色，无系统残留）');
      Lines.Add('');
      Lines.Add('卸载说明：直接删除安装目录即可（便携版不在注册表与开始菜单写入任何残留）。');
    end
    else
    begin
      Lines.Add('数据模式：漫游模式');
      Lines.Add('');
      Lines.Add('卸载说明：在控制面板或系统「应用和功能」中选择卸载即可。');
      Lines.Add('（卸载时仅移除程序文件，个人数据将安全保留）');
    end;
    Lines.Add('');
    Lines.Add('主程序：{#AppExeName}');
#ifdef AppCliExeName
    Lines.Add('命令行：{#AppCliExeName}');
#endif
#ifdef AppAutoStartNote
    Lines.Add('开机自启：可在主程序设置中自由开关（写入 HKCU Run）');
#endif
    Lines.SaveToFile(AppDir + '\README.txt');
  finally
    Lines.Free();
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  AppDir, PortableIni, PortableDataDir, TargetDataDir: string;
begin
  if CurStep = ssPostInstall then
  begin
    AppDir := ExpandConstant('{app}');
    PortableIni := AppDir + '\{#AppPortableFlagName}';
    PortableDataDir := AppDir + '\{#AppPortableDataDirName}';

    // 1. 数据迁移
    MigrateDataIfNeeded();

    // 2. 根据数据模式处理标志文件
    if SelectedDataMode = 1 then
    begin
      TargetDataDir := PortableDataDir;
      ForceDirectories(PortableDataDir);

      // 创建或保留便携模式标记文件
      if not FileExists(PortableIni) then
      begin
        SaveStringToFile(PortableIni, '; {#AppName} Portable Mode' + #13#10 +
          '; Data directory: ' + PortableDataDir + #13#10, False);
      end;
    end
    else
    begin
      TargetDataDir := RoamingDataDir();
      ForceDirectories(TargetDataDir);

      // 漫游模式：若存在旧便携标记文件则备份
      if FileExists(PortableIni) then
      begin
        DeleteFile(PortableIni + '.bak');
        RenameFile(PortableIni, PortableIni + '.bak');
      end;

      // 清理同名旧版卸载注册表项，避免控制面板重复项
      RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#AppName}');
    end;

    // 3. 生成 README.txt
    WriteReadmeFile(AppDir, TargetDataDir);
  end
  else if CurStep = ssDone then
  begin
    // 安装全部完成，此时 Inno 卸载项与文件已生成完毕，如果是便携模式则立即清理，实现纯绿色
    if SelectedDataMode = 1 then
    begin
      CleanPortableFootprint();
    end;
  end;
end;

procedure DeinitializeSetup();
begin
  // 安装向导关闭时再次确保便携模式清理彻底
  if SelectedDataMode = 1 then
  begin
    CleanPortableFootprint();
  end;
end;

// ================================================================
// 卸载流程处理
// ================================================================

function InitializeUninstall(): Boolean;
begin
  Result := True;
  if AnyAppRunning() then
  begin
    if MsgBox(FmtMessage(CustomMessage('AppRunningUninstallWarning'), ['{#AppName}']), mbConfirmation, MB_YESNO or MB_DEFBUTTON1) = IDYES then
    begin
      KillAppProcesses();
      if AnyAppRunning() then
      begin
        MsgBox(CustomMessage('UninstallProcessRunning'), mbError, MB_OK);
        Result := False;
      end;
    end
    else
    begin
      Result := False;
    end;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  AppDir, RunVal, RunKey: string;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    AppDir := ExpandConstant('{app}');
    RunKey := 'Software\Microsoft\Windows\CurrentVersion\Run';

    // 1. 清理自启注册表（仅当指向本安装目录时）
    if RegQueryStringValue(HKCU, RunKey, '{#AppName}', RunVal) then
    begin
      if Pos(Lowercase(AppDir), Lowercase(RunVal)) > 0 then
      begin
        RegDeleteValue(HKCU, RunKey, '{#AppName}');
      end;
    end;

    // 2. 清理样例文件和安装说明
    DeleteFile(AppDir + '\README.txt');
    DeleteSampleFiles(AppDir);

    // 3. 检查便携数据目录：如果含有用户数据，保留并提示；为空则清理
    if DirExists(AppDir + '\{#AppPortableDataDirName}') then
    begin
      if DirHasFiles(AppDir + '\{#AppPortableDataDirName}') then
      begin
        MsgBox(FmtMessage(CustomMessage('UninstallRetainData'), [AppDir + '\{#AppPortableDataDirName}']), mbInformation, MB_OK);
      end
      else
      begin
        DelTree(AppDir + '\{#AppPortableDataDirName}', True, True, True);
        DeleteFile(AppDir + '\{#AppPortableFlagName}');
        DeleteFile(AppDir + '\{#AppPortableFlagName}.bak');
      end;
    end;

    // 尝试删除安装根目录（非空则会自动保留）
    RemoveDir(AppDir);
  end;
end;
