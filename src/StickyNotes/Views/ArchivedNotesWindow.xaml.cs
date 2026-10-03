using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using StickyNotes.Infrastructure;
using StickyNotes.Models;
using StickyNotes.Services;
using StickyNotes.ViewModels;

namespace StickyNotes.Views;

public partial class ArchivedNotesWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly PinService? _pinService;
    private readonly SettingsService? _settingsService;

    private readonly DispatcherTimer _idleCloseTimer = new() { Interval = TimeSpan.FromMinutes(10) };

    public ArchivedNotesViewModel ViewModel { get; }

    public ArchivedNotesWindow(
        ArchivedNotesViewModel viewModel,
        PinService? pinService = null,
        SettingsService? settingsService = null)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        _pinService = pinService;
        _settingsService = settingsService;
        PinOverlay.PinService = pinService;

        Loaded += async (_, _) =>
        {
            // 主列表已解锁可见时视为用户刚验证过身份，避免重复输 PIN；
            // 否则（列表锁定或已隐藏到托盘）归档窗口打开即锁定
            bool listUnlocked = Application.Current?.Windows.OfType<NotesListWindow>()
                .FirstOrDefault() is { IsContentAccessible: true };
            PinOverlay.IsLocked = (_pinService?.IsPinEnabled ?? false) && !listUnlocked;

            await ViewModel.LoadArchivedNotesAsync();
            FocusArchivedList();
        };

        // 键盘支持（F-P1-10 剩余项）：Ctrl+F 聚焦搜索框（占位文案此前承诺了该快捷键但未实现，N-6）、
        // Esc 清空搜索词；列表内 ↑/↓/Home/End 由 ListBox 原生导航承担。
        PreviewKeyDown += ArchivedNotesWindow_PreviewKeyDown;

        // 闲时自动关闭：与主列表同一设置项，防止人离开后归档内容长时间暴露在屏幕上
        _idleCloseTimer.Tick += (_, _) =>
        {
            _idleCloseTimer.Stop();
            if (IsVisible && WindowState != WindowState.Minimized)
            {
                AppLog.Info("[ArchivedNotesWindow] 归档窗口长时间不在前台，自动关闭");
                Close();
            }
        };

        Activated += (_, _) => _idleCloseTimer.Stop();
        Deactivated += (_, _) =>
        {
            var minutes = _settingsService?.ListAutoCloseMinutes ?? 0;
            if (minutes > 0 && IsVisible)
            {
                _idleCloseTimer.Interval = TimeSpan.FromMinutes(minutes);
                _idleCloseTimer.Stop();
                _idleCloseTimer.Start();
            }
        };
    }

    /// <summary>
    /// 加载完成后恢复键盘焦点：有归档则落到首项（↑/↓ 即可导航），否则落到搜索框
    /// </summary>
    private void FocusArchivedList()
    {
        if (PinOverlay.IsLocked) return;

        if (ArchivedNotesListBox.Items.Count > 0)
        {
            ArchivedNotesListBox.SelectedIndex = 0;
            if (ArchivedNotesListBox.ItemContainerGenerator.ContainerFromIndex(0) is ListBoxItem item)
            {
                item.Focus();
            }
        }
        else
        {
            SearchBox.Focus();
        }
    }

    private void ArchivedNotesWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Ctrl+F 聚焦搜索框（N-6）：搜索框占位文案承诺了该快捷键但窗口原先并未实现
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            SearchBox.Focus();
            e.Handled = true;
            return;
        }

        // Esc 清空搜索词（与主列表窗口一致）
        if (e.Key == Key.Escape && ViewModel.SearchText.Length > 0)
        {
            ViewModel.SearchText = string.Empty;
            e.Handled = true;
        }
    }

    private async void RestoreNoteButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: Note note })
        {
            await ViewModel.RestoreNoteCommand.ExecuteAsync(note);
        }
    }

    private async void HardDeleteNoteButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: Note note })
        {
            // 显式指定 Owner：避免确认框被主窗口遮挡，用户误以为无响应而重复点击（F-P1-10 剩余项）
            var result = MessageBox.Show(
                this,
                "确定要彻底删除该便签吗？\n此操作无法撤销，数据将永久丢失。",
                "彻底删除确认",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning
            );

            if (result == MessageBoxResult.Yes)
            {
                await ViewModel.HardDeleteNoteCommand.ExecuteAsync(note);
            }
        }
    }

    private async void ClearAllButton_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            this,
            "确定要清空所有已归档的便签吗？\n此操作将永久删除所有归档便签，无法撤销。",
            "清空归档确认",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning
        );

        if (result == MessageBoxResult.Yes)
        {
            await ViewModel.ClearAllArchivedCommand.ExecuteAsync(null);
        }
    }
}
