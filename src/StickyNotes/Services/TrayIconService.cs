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
            uFlags = NativeMethods.NIF_MESSAGE | NativeMethods.NIF_ICON | NativeMethods.NIF_TIP,
            uCallbackMessage = NativeMethods.WM_TRAYICON,
            hIcon = _hIcon,
            szTip = "彩色便签 (StickyNotes)"
        };

        bool ok = NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_ADD, ref _nid);
        if (ok)
        {
            _isCreated = true;
            AppLog.Info("[TrayIconService] 系统托盘图标创建成功");
        }
        else
        {
            AppLog.Warn("[TrayIconService] 系统托盘图标创建失败");
        }

        CreateContextMenu();
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
        if (msg == NativeMethods.WM_TRAYICON)
        {
            int eventId = lParam.ToInt32();
            if (eventId == NativeMethods.WM_LBUTTONUP || eventId == NativeMethods.WM_LBUTTONDBLCLK)
            {
                WeakReferenceMessenger.Default.Send(new ShowNotesListRequestedMessage());
                handled = true;
            }
            else if (eventId == NativeMethods.WM_RBUTTONUP || eventId == NativeMethods.WM_CONTEXTMENU)
            {
                ShowContextMenu();
                handled = true;
            }
        }

        return IntPtr.Zero;
    }

    private void ShowContextMenu()
    {
        if (_contextMenu == null || _hwndSource == null) return;

        NativeMethods.SetForegroundWindow(_hwndSource.Handle);
        _contextMenu.Placement = PlacementMode.MousePoint;
        _contextMenu.IsOpen = true;
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
