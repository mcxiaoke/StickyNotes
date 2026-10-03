using System.Windows;
using System.Windows.Controls;
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
        };

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
            var result = MessageBox.Show(
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
