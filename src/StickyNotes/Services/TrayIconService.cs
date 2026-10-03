using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using CommunityToolkit.Mvvm.Messaging;
using StickyNotes.Infrastructure;
using StickyNotes.Messages;

namespace StickyNotes.Services;

/// <summary>
/// Windows 系统任务栏托盘图标服务 (Shell_NotifyIcon)
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private const int TrayIconId = 1001;

    private readonly WindowManager _windowManager;
    private readonly SettingsService _settingsService;
    private HwndSource? _hwndSource;
    private NativeMethods.NOTIFYICONDATA _nid;
    private IntPtr _hIcon = IntPtr.Zero;
    private ContextMenu? _contextMenu;
    private bool _isCreated;

    public TrayIconService(WindowManager windowManager, SettingsService settingsService)
    {
        _windowManager = windowManager;
        _settingsService = settingsService;
    }

    /// <summary>
    /// 初始化托盘图标及右键上下文菜单
    /// </summary>
    public void Initialize()
    {
        if (_isCreated) return;

        var parameters = new HwndSourceParameters("StickyNotes_TrayHelper")
        {
            WindowStyle = 0,
            Width = 0,
            Height = 0
        };

        _hwndSource = new HwndSource(parameters);
        _hwndSource.AddHook(HwndHook);

        // 加载应用程序原生图标 (从当前可执行文件或标准应用程序图标提取)
        try
        {
            var exePath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exePath))
            {
                _hIcon = NativeMethods.ExtractIcon(IntPtr.Zero, exePath, 0);
            }
            if (_hIcon == IntPtr.Zero || _hIcon == (IntPtr)1)
            {
                _hIcon = NativeMethods.LoadIcon(IntPtr.Zero, NativeMethods.IDI_APPLICATION);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[TrayIconService] 提取托盘图标失败: {ex.Message}", ex);
            _hIcon = NativeMethods.LoadIcon(IntPtr.Zero, NativeMethods.IDI_APPLICATION);
        }

        _nid = new NativeMethods.NOTIFYICONDATA
        {
            cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.NOTIFYICONDATA>(),
            hWnd = _hwndSource.Handle,
            uID = TrayIconId,
            uFlags = NativeMethods.TrayNotifyIconFlags,
            uCallbackMessage = NativeMethods.WM_TRAYICON,
            hIcon = _hIcon,
            szTip = "StickyNotes 便签"
        };

        CreateIcon();
        CreateContextMenu();
    }

    /// <summary>
    /// 向 Shell 注册（或重新注册）托盘图标，并声明使用版本 4 行为。
    /// </summary>
    private void CreateIcon()
    {
        if (_hwndSource == null)
        {
            return;
        }

        bool ok = NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_ADD, ref _nid);
        if (ok)
        {
            _isCreated = true;

            // 必须紧接着调用 NIM_SETVERSION：否则图标停留在"旧式"形态，
            // 不参与 Win10/11 图标区布局（原 F-P1-8 第二宗毛病）。
            var versionData = _nid;
            versionData.uTimeoutOrVersion = NativeMethods.NOTIFYICON_VERSION_4;
            NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_SETVERSION, ref versionData);

            AppLog.Info("[TrayIconService] 系统托盘图标创建成功（版本 4）");
        }
        else
        {
            _isCreated = false;
            AppLog.Warn("[TrayIconService] 系统托盘图标创建失败");
        }
    }

    private void CreateContextMenu()
    {
        _contextMenu = new ContextMenu();

        var itemNew = new MenuItem { Header = "新建便签 (Ctrl+N)" };
        itemNew.Click += (_, _) => WeakReferenceMessenger.Default.Send(new NewNoteRequestedMessage());
        _contextMenu.Items.Add(itemNew);

        var itemList = new MenuItem { Header = "便签列表 (Ctrl+H)" };
        itemList.Click += (_, _) => WeakReferenceMessenger.Default.Send(new ShowNotesListRequestedMessage());
        _contextMenu.Items.Add(itemList);

        _contextMenu.Items.Add(new Separator());

        var itemSettings = new MenuItem { Header = "设置中心" };
        itemSettings.Click += (_, _) => _windowManager.OpenOrActivateSettingsWindow();
        _contextMenu.Items.Add(itemSettings);

        _contextMenu.Items.Add(new Separator());

        var itemExit = new MenuItem { Header = "退出便签" };
        itemExit.Click += (_, _) =>
        {
            AppLog.Info("[TrayIconService] 用户点击托盘退出菜单");

            // 退出顺序至关重要（F-P0-1）：先持久化置顶便签坐标 —— 此刻窗口字典仍完整；
            // 若拖到 App.OnExit 才做，Shutdown() 已先关闭全部窗口并清空字典，坐标将永久丢失。
            // 随后标记退出，使窗口 Closed 回调跳过 IsOpen=false 回写。
            _windowManager.BeginShutdownAndPersistPinnedPlacement();

            // 最后关闭应用。
            Application.Current.Shutdown();
        };
        _contextMenu.Items.Add(itemExit);
    }

    private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // explorer.exe 重启 / 崩溃恢复后会广播 TaskbarCreated：此时旧图标已失效，
        // 必须重建，否则托盘图标永久消失（原 F-P1-8 第一宗毛病）。
        if (msg == NativeMethods.WM_TASKBAR_CREATED && NativeMethods.WM_TASKBAR_CREATED != 0)
        {
            AppLog.Info("[TrayIconService] 检测到任务栏重建（explorer 重启），正在恢复托盘图标");
            _isCreated = false;
            CreateIcon();
            handled = true;
            return IntPtr.Zero;
        }

        if (msg == NativeMethods.WM_TRAYICON)
        {
            // 注意：v4 下事件码位于 LOWORD(lParam)，图标 ID 在 HIWORD(lParam)。
            // 早期版本曾把 lParam 整体当事件码比较，导致所有分支都不命中、托盘完全无响应。
            var kind = NativeMethods.ClassifyTrayEvent(lParam.ToInt32());

            switch (kind)
            {
                case NativeMethods.TrayEventKind.OpenList:
                    WeakReferenceMessenger.Default.Send(new ShowNotesListRequestedMessage());
                    handled = true;
                    break;

                case NativeMethods.TrayEventKind.ShowContextMenu:
                    ShowContextMenu();
                    handled = true;
                    break;
            }

            return IntPtr.Zero;
        }

        // 实例唤醒消息必须由**本窗口**兜底处理（而不是只挂主列表窗口）：
        // 主列表窗口在 --minimized/--autostart 启动路径下不会被 Show()，
        // 因此它此刻没有 HWND，消息永远收不到；托盘隐藏窗口则始终存在。
        // 早期实现只在 NotesListWindow.WndProc 里处理该消息，导致「已有实例在运行，
        // 但再双击 exe 毫无反应」（用户必须结束进程再重开）。
        if (msg == NativeMethods.WM_ACTIVATE_INSTANCE && NativeMethods.WM_ACTIVATE_INSTANCE != 0)
        {
            handled = true;

            // 用 BeginInvoke 让消息处理即刻返回，避免在窗口过程里同步做窗口激活造成重入
            var dispatcher = _hwndSource?.Dispatcher;
            if (dispatcher != null)
            {
                dispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.Normal,
                    new Action(() => _windowManager.OpenOrActivateNotesListWindow()));
            }
            else
            {
                _windowManager.OpenOrActivateNotesListWindow();
            }

            return IntPtr.Zero;
        }

        return IntPtr.Zero;
    }

    private void ShowContextMenu()
    {
        if (_contextMenu == null || _hwndSource == null) return;

        NativeMethods.SetForegroundWindow(_hwndSource.Handle);

        // 菜单关闭后必须主动复位托盘图标状态，否则图标会偶发保持"按下"的灰色高亮态
        // （原 F-P1-8 第三宗毛病）。
        _contextMenu.Closed -= ContextMenu_Closed;
        _contextMenu.Closed += ContextMenu_Closed;

        // 用 MousePoint：该模式由 WPF 在**屏幕 DIP 空间**内部处理定位，天然正确。
        // 禁止改用 Placement=AbsolutePoint + 消息里的 wParam 锚点坐标：
        // 那是**物理像素**，而 WPF 的 HorizontalOffset/VerticalOffset 单位是 **DIP**。
        // 在 150% 缩放下把物理值当 DIP 用会放大 1.5 倍，坐标越界后被 WPF 钳到屏幕边缘，
        // 表现为「右键菜单跑到屏幕右下角、远离托盘图标」。
        _contextMenu.Placement = PlacementMode.MousePoint;
        _contextMenu.IsOpen = true;
    }

    private void ContextMenu_Closed(object? sender, RoutedEventArgs e)
    {
        var handle = _hwndSource?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        // 复位图标状态，并向通知区域归还焦点（否则键盘用户关闭菜单后焦点会丢失）。
        NativeMethods.PostMessage(handle, NativeMethods.WM_CANCELMODE, IntPtr.Zero, IntPtr.Zero);
        NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_SETFOCUS, ref _nid);
    }

    public void Dispose()
    {
        if (_isCreated)
        {
            NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_DELETE, ref _nid);
            _isCreated = false;
        }

        if (_hIcon != IntPtr.Zero)
        {
            NativeMethods.DestroyIcon(_hIcon);
            _hIcon = IntPtr.Zero;
        }

        _hwndSource?.RemoveHook(HwndHook);
        _hwndSource?.Dispose();
        _hwndSource = null;
    }
}
