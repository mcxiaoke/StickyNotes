using System;
using System.Runtime.InteropServices;

namespace StickyNotes.Infrastructure;

/// <summary>
/// Win32 原生 API 互操作封装
/// </summary>
internal static class NativeMethods
{
    public const int HWND_BROADCAST = 0xffff;
    public const int SW_RESTORE = 9;

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    public static extern int RegisterWindowMessage(string lpString);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    public static readonly int WM_ACTIVATE_INSTANCE = RegisterWindowMessage("StickyNotes_Activate_MainWindow");

    /// <summary>
    /// 由 explorer.exe 在任务栏（重新）创建后广播的系统消息。托盘图标必须监听它，
    /// 否则用户重启 explorer 后图标永久消失，只能重启应用（原 F-P1-8）。
    /// </summary>
    public static readonly int WM_TASKBAR_CREATED = RegisterWindowMessage("TaskbarCreated");

    /// <summary>
    /// 唤醒已在运行的实例：把注册消息**定向**投递给本应用的**全部**顶层窗口。
    /// </summary>
    /// <remarks>
    /// 三个必须遵守的约束（每一条都曾因违反而出现真实故障）：
    /// <list type="number">
    ///   <item>
    ///     **禁止用 `Environment.ProcessId` 定位目标窗口**。本方法是由**第二实例**调用的，
    ///     第二实例自身没有任何窗口；按"本进程"筛选会命中零个窗口，唤醒静默失效，
    ///     表现为「已有实例在运行，但再双击 exe 毫无反应」。必须按**应用标识**（同可执行文件名）匹配。
    ///   </item>
    ///   <item>
    ///     **禁止改回 `PostMessage(HWND_BROADCAST, ...)`**：那会把消息发给当前会话内所有顶层窗口，
    ///     且 UIPI 下若两个实例完整性级别不同（一个提权、一个普通），唤醒会静默失败（原 F-P1-8 第四宗毛病）。
    ///   </item>
    ///   <item>
    ///     **必须投递给每一个匹配窗口，不得命中第一个就提前停止枚举**。WPF 进程有多个顶层窗口
    ///     （调度器窗口、托盘隐藏 helper 窗口等），EnumWindows 按 z 序返回，第一个往往不是主列表窗口；
    ///     若提前 return，消息会落在只处理托盘消息的隐藏窗口上。好在托盘窗口已兜底处理该消息。
    ///   </item>
    /// </list>
    /// </remarks>
    public static void NotifyExistingInstance()
    {
        PostActivateToSameApplicationWindows();
    }

    /// <summary>
    /// 定向投递实例激活消息给同一应用的顶层窗口（含本进程），返回成功投递的窗口数。
    /// </summary>
    internal static int PostActivateToSameApplicationWindows()
    {
        if (WM_ACTIVATE_INSTANCE == 0)
        {
            return 0;
        }

        var currentProcessName = GetCurrentProcessName();

        return EnumWindowsAndPost(currentProcessName);
    }

    /// <summary>
    /// 兼容旧调用名：语义同 <see cref="PostActivateToSameApplicationWindows"/>。
    /// </summary>
    internal static int PostActivateToOwnProcessWindows() => PostActivateToSameApplicationWindows();

    private static int EnumWindowsAndPost(string currentProcessName)
    {
        int postedCount = 0;

        EnumWindows((hwnd, _) =>
        {
            if (!IsSameApplicationWindow(hwnd, currentProcessName))
            {
                return true;
            }

            // 不使用 return false：必须遍历完全部匹配窗口
            if (PostMessage(hwnd, WM_ACTIVATE_INSTANCE, IntPtr.Zero, IntPtr.Zero))
            {
                postedCount++;
            }

            return true;
        }, IntPtr.Zero);

        return postedCount;
    }

    /// <summary>
    /// 判定给定顶层窗口是否属于"同一个应用"（可执行文件名相同，含当前进程自身）。
    /// 独立为可测方法，便于用 HwndSource 构造窗口直接验证匹配规则。
    /// </summary>
    internal static bool IsSameApplicationWindow(IntPtr hwnd, string currentProcessName)
    {
        GetWindowThreadProcessId(hwnd, out uint windowProcessId);
        if (windowProcessId == 0)
        {
            return false;
        }

        if (windowProcessId == (uint)Environment.ProcessId)
        {
            return true;
        }

        try
        {
            using var process = System.Diagnostics.Process.GetProcessById((int)windowProcessId);
            return IsSameProcessName(process.ProcessName, currentProcessName);
        }
        catch (Exception)
        {
            // 进程可能在枚举期间退出，或无权查询 —— 忽略该窗口
            return false;
        }
    }

    /// <summary>
    /// 进程名比较（忽略大小写、忽略 Windows 可能附加的扩展名差异）。
    /// 独立为纯函数以便直接单测。
    /// </summary>
    internal static bool IsSameProcessName(string? processName, string? currentProcessName)
    {
        if (string.IsNullOrEmpty(processName) || string.IsNullOrEmpty(currentProcessName))
        {
            return false;
        }

        return string.Equals(processName, currentProcessName, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetCurrentProcessName()
    {
        try
        {
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            return process.ProcessName;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    #region 全局热键 (RegisterHotKey)
    public const int WM_HOTKEY = 0x0312;

    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;
    public const uint MOD_NOREPEAT = 0x4000;

    public const uint VK_N = 0x4E;
    public const uint VK_H = 0x48;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    #endregion

    #region 任务栏托盘 (Shell_NotifyIcon)
    public const int WM_USER = 0x0400;
    public const int WM_TRAYICON = WM_USER + 101;

    public const int WM_LBUTTONUP = 0x0202;
    public const int WM_LBUTTONDBLCLK = 0x0203;
    public const int WM_RBUTTONUP = 0x0205;
    public const int WM_CONTEXTMENU = 0x007B;

    public const int NIM_ADD = 0x00000000;
    public const int NIM_MODIFY = 0x00000001;
    public const int NIM_DELETE = 0x00000002;
    public const int NIM_SETVERSION = 0x00000004;

    /// <summary>NOTIFYICON_VERSION_4：启用 Win10/11 图标区布局与 WM_CONTEXTMENU 语义</summary>
    public const int NOTIFYICON_VERSION_4 = 4;

    /// <summary>菜单关闭后需要主动复位托盘图标状态，否则偶发保持"按下"灰态</summary>
    public const int WM_CANCELMODE = 0x001F;

    /// <summary>把焦点还给任务栏通知区域（菜单关闭/按 ESC 取消后应调用）</summary>
    public const int NIM_SETFOCUS = 0x00000003;

    /// <summary>
    /// NOTIFYICON_VERSION_4 下的「图标被选中/激活」事件码。
    /// 鼠标左键单击、以及键盘空格/回车激活通知图标时，Shell 发送本事件而非 WM_LBUTTONUP。
    /// </summary>
    public const int NIN_SELECT = WM_USER + 0;   // 0x0400

    /// <summary>NOTIFYICON_VERSION_4 下键盘激活通知图标的事件码</summary>
    public const int NIN_KEYSELECT = WM_USER + 1; // 0x0401

    /// <summary>托盘图标事件分类（由原始 lParam 解析得到）</summary>
    public enum TrayEventKind
    {
        /// <summary>与托盘无关或未知事件，应忽略</summary>
        None = 0,

        /// <summary>左键单击/双击或键盘激活 → 唤醒主列表</summary>
        OpenList,

        /// <summary>右键或键盘菜单键 → 弹出上下文菜单</summary>
        ShowContextMenu
    }

    /// <summary>
    /// 从 WM_TRAYICON 的 lParam 解析托盘事件类型。
    /// </summary>
    /// <remarks>
    /// **必须用 LOWORD(lParam) 取事件码，绝不能把 lParam 整体当事件码比较。**
    /// 在 NOTIFYICON_VERSION_4 下，Shell 的解码规则变为：
    /// <list type="bullet">
    ///   <item><c>LOWORD(lParam)</c> = 通知事件（NIN_SELECT / NIN_KEYSELECT / WM_CONTEXTMENU / 鼠标消息）</item>
    ///   <item><c>HIWORD(lParam)</c> = 图标 ID（本项目的 TrayIconId，非 0）</item>
    /// </list>
    /// 因此 lParam 实际形如 <c>0x03E9_0400</c>；若直接与 <c>0x0400</c> 或 <c>0x007B</c> 比较，
    /// 所有分支都不会命中，表现为「托盘图标在、但左右键和双击全部无反应」。
    /// 来源：Microsoft Learn，NOTIFYICONDATA 结构的 uCallbackMessage 说明。
    /// </remarks>
    internal static TrayEventKind ClassifyTrayEvent(int lParam)
    {
        int eventCode = LowWord(lParam);

        return eventCode switch
        {
            // v4 语义：左键/键盘激活
            NIN_SELECT or NIN_KEYSELECT => TrayEventKind.OpenList,
            // v4 语义：右键 或 键盘菜单键（文档明确：鼠标右键与菜单键都发 WM_CONTEXTMENU）
            WM_CONTEXTMENU => TrayEventKind.ShowContextMenu,
            // 兼容旧版（未成功 SETVERSION）与鼠标消息直传的情形
            WM_LBUTTONUP or WM_LBUTTONDBLCLK => TrayEventKind.OpenList,
            WM_RBUTTONUP => TrayEventKind.ShowContextMenu,
            _ => TrayEventKind.None
        };
    }

    /// <summary>取 16 位低字（事件码所在位段）</summary>
    internal static int LowWord(int value) => value & 0xFFFF;

    /// <summary>取 16 位高字（图标 ID 所在位段）</summary>
    internal static int HighWord(int value) => (value >> 16) & 0xFFFF;

    /// <summary>
    /// 托盘 NOTIFYICONDATA 的 uFlags。
    /// 必须包含 <see cref="NIF_SHOWTIP"/>：当 uVersion 为 NOTIFYICON_VERSION_4 时，
    /// 标准 tooltip 默认被抑制（留给应用自绘富弹窗），不加此标志用户就看不到托盘提示文字。
    /// </summary>
    internal const int TrayNotifyIconFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP;

    public const int NIF_MESSAGE = 0x00000001;
    public const int NIF_ICON = 0x00000002;
    public const int NIF_TIP = 0x00000004;
    public const int NIF_STATE = 0x00000008;
    public const int NIF_INFO = 0x00000010;
    public const int NIF_GUID = 0x00000020;
    public const int NIF_SHOWTIP = 0x00000080;

    public const int NIIF_NONE = 0x00000000;
    public const int NIIF_INFO = 0x00000001;
    public const int NIIF_WARNING = 0x00000002;
    public const int NIIF_ERROR = 0x00000003;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    public struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public int uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool Shell_NotifyIcon(int dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    public static extern IntPtr ExtractIcon(IntPtr hInst, string lpszExeFileName, int nIconIndex);

    [DllImport("user32.dll")]
    public static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

    public static readonly IntPtr IDI_APPLICATION = (IntPtr)32512;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr CopyIcon(IntPtr hIcon);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyIcon(IntPtr hIcon);

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out POINT lpPoint);
    #endregion

    #region 内存优化与工作集修剪 (Working Set Trimming)
    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EmptyWorkingSet(IntPtr hProcess);

    /// <summary>
    /// 主动触发当前进程工作集修剪，释放后台闲置/非活跃物理内存
    /// </summary>
    public static void TrimWorkingSet()
    {
        try
        {
            // 适度建议 GC 收集非存活对象
            GC.Collect(2, GCCollectionMode.Optimized, false);
            GC.WaitForPendingFinalizers();

            using var proc = System.Diagnostics.Process.GetCurrentProcess();
            EmptyWorkingSet(proc.Handle);
        }
        catch (Exception ex)
        {
            // NativeMethods 会在 Program.Main 极早期被调用，此时 AppPaths 可能尚未就绪，
            // 因此使用 Debug 输出而非 AppLog，避免日志系统自身的初始化副作用。
            System.Diagnostics.Debug.WriteLine($"[NativeMethods] 修剪工作集失败: {ex.Message}");
        }
    }
    #endregion
}
