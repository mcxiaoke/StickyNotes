using System;
using System.Runtime.InteropServices;

namespace StickyNotes.Infrastructure;

/// <summary>
/// Win32 原生 API 互操作封装
/// </summary>
internal static class NativeMethods
{
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

    // 说明：托盘图标已整体迁移到 H.NotifyIcon.Wpf（TaskbarIcon）托管，TaskbarCreated 的监听
    // 与重建由该库内部完成；本项目不再保留任何手写 Shell_NotifyIcon 互操作（N-9 清理）。

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
