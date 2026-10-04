; ============================================================================
; StickyNotes Inno Setup 安装脚本（应用壳）
; ============================================================================
; 通用核心在同目录 AppInstaller.Common.iss；其它应用复用时复制本壳，
; 修改下方 define 即可（AppId 必须换成每个应用自己的唯一 GUID）。
;
; 手动编译示例：
;   iscc /DPayloadDir=<发布目录> /DAppVersion=1.2.3 /DIconFile=<图标路径> StickyNotes.iss
; 产物输出到脚本所在目录（可用 /O<目录> 覆盖）。

#define AppId "{{5BF37E32-D787-423D-A28E-E2F2C5C96C9A}"
#define AppName "StickyNotes"
#define AppExeName "StickyNotes.exe"
#define AppPublisher "mcxiaoke"
#define AppURL "https://github.com/mcxiaoke/StickyNotes"
; StickyNotes 漫游数据目录为 %LOCALAPPDATA%\StickyNotes
; （见 src/StickyNotes/Infrastructure/AppPaths.cs，与常见 %APPDATA% 应用不同）
#define AppDataDirRoot "{localappdata}"
; 应用自带开机自启功能，README 说明该行需要保留
#define AppAutoStartNote

#include "AppInstaller.Common.iss"
