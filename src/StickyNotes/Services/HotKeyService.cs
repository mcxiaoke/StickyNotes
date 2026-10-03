using System;
using System.Windows.Interop;
using CommunityToolkit.Mvvm.Messaging;
using StickyNotes.Infrastructure;
using StickyNotes.Messages;

namespace StickyNotes.Services;

/// <summary>
/// 系统全局快捷键服务 (Win+Alt+N, Win+Alt+H)
/// </summary>
public sealed class HotKeyService : IDisposable
{
    private const int HotKeyId_NewNote = 9001;
    private const int HotKeyId_ShowList = 9002;

    private readonly SettingsService _settingsService;
    private HwndSource? _hwndSource;
    private bool _isRegistered;

    public HotKeyService(SettingsService settingsService)
    {
        _settingsService = settingsService;

        // 监听设置变更
        WeakReferenceMessenger.Default.Register<HotKeyConfigChangedMessage>(this, (_, msg) =>
        {
            if (msg.Value)
            {
                RegisterHotKeys();
            }
            else
            {
                UnregisterHotKeys();
            }
        });
    }

    /// <summary>
    /// 初始化全局消息接收窗口并按需注册快捷键
    /// </summary>
    public void Initialize()
    {
        if (_hwndSource != null) return;

        var parameters = new HwndSourceParameters("StickyNotes_HotKeyHelper")
        {
            WindowStyle = 0,
            Width = 0,
            Height = 0
        };

        _hwndSource = new HwndSource(parameters);
        _hwndSource.AddHook(HwndHook);

        if (_settingsService.EnableGlobalHotKeys)
        {
            RegisterHotKeys();
        }
    }

    public void RegisterHotKeys()
    {
        if (_hwndSource == null || _isRegistered) return;

        IntPtr handle = _hwndSource.Handle;
        uint modifiers = NativeMethods.MOD_WIN | NativeMethods.MOD_ALT | NativeMethods.MOD_NOREPEAT;

        // 1. 注册 Win+Alt+N (新建便签)
        bool successN = NativeMethods.RegisterHotKey(handle, HotKeyId_NewNote, modifiers, NativeMethods.VK_N);
        if (successN)
        {
            AppLog.Info("[HotKeyService] 全局热键 Win+Alt+N 注册成功 (快速新建)");
        }
        else
        {
            AppLog.Warn("[HotKeyService] 全局热键 Win+Alt+N 注册失败 (可能被其它软件占用)");
        }

        // 2. 注册 Win+Alt+H (呼出主列表)
        bool successH = NativeMethods.RegisterHotKey(handle, HotKeyId_ShowList, modifiers, NativeMethods.VK_H);
        if (successH)
        {
            AppLog.Info("[HotKeyService] 全局热键 Win+Alt+H 注册成功 (呼出列表)");
        }
        else
        {
            AppLog.Warn("[HotKeyService] 全局热键 Win+Alt+H 注册失败 (可能被其它软件占用)");
        }

        // 仅在至少一个热键真正注册成功时才置位，否则保留 false 以便用户关闭再开启开关时能够重试注册
        _isRegistered = successN || successH;
        AppLog.Info($"[HotKeyService] 全局热键注册结果: Win+Alt+N={successN}, Win+Alt+H={successH}, IsRegistered={_isRegistered}");
    }

    public void UnregisterHotKeys()
    {
        if (_hwndSource == null || !_isRegistered) return;

        IntPtr handle = _hwndSource.Handle;
        NativeMethods.UnregisterHotKey(handle, HotKeyId_NewNote);
        NativeMethods.UnregisterHotKey(handle, HotKeyId_ShowList);
        _isRegistered = false;
        AppLog.Info("[HotKeyService] 全局热键已注销");
    }

    private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY)
        {
            int hotkeyId = wParam.ToInt32();
            if (hotkeyId == HotKeyId_NewNote)
            {
                AppLog.Info("[HotKeyService] 捕获全局热键 Win+Alt+N，发出新建请求");
                WeakReferenceMessenger.Default.Send(new NewNoteRequestedMessage());
                handled = true;
            }
            else if (hotkeyId == HotKeyId_ShowList)
            {
                AppLog.Info("[HotKeyService] 捕获全局热键 Win+Alt+H，发出唤醒主列表请求");
                WeakReferenceMessenger.Default.Send(new ShowNotesListRequestedMessage());
                handled = true;
            }
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        WeakReferenceMessenger.Default.UnregisterAll(this);
        UnregisterHotKeys();
        _hwndSource?.RemoveHook(HwndHook);
        _hwndSource?.Dispose();
        _hwndSource = null;
    }
}
