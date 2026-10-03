using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using StickyNotes.Infrastructure;
using StickyNotes.Messages;
using StickyNotes.Models;
using StickyNotes.Services;
using StickyNotes.ViewModels;

namespace StickyNotes.Views;

/// <summary>
/// 便签管理中心主窗口交互逻辑
/// </summary>
public partial class NotesListWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly SettingsService? _settingsService;
    private readonly PinService? _pinService;

    /// <summary>闲时自动关闭计时器：窗口持续不在前台达到设定分钟数后自动收起</summary>
    private readonly DispatcherTimer _idleCloseTimer = new() { Interval = TimeSpan.FromMinutes(10) };

    public NotesListViewModel ViewModel => (NotesListViewModel)DataContext;

    /// <summary>列表内容当前是否处于已解锁可见状态（供归档窗口判断是否需要初始锁定）</summary>
    public bool IsContentAccessible => IsVisible && !PinOverlay.IsLocked;

    public NotesListWindow(NotesListViewModel viewModel, SettingsService? settingsService = null, PinService? pinService = null)
    {
        InitializeComponent();
        DataContext = viewModel;
        _settingsService = settingsService;
        _pinService = pinService;
        PinOverlay.PinService = pinService;

        Loaded += async (_, _) =>
        {
            RestoreWindowPlacement();
            // 初始锁定：启用 PIN 时窗口一打开即被遮罩覆盖
            PinOverlay.IsLocked = _pinService?.IsPinEnabled ?? false;
            await ViewModel.LoadNotesAsync();
        };

        // 锁定规则：每次窗口从不可见变为可见（启动/托盘唤醒）都重新上锁；隐藏或收起时清空搜索词
        IsVisibleChanged += (s, e) =>
        {
            _idleCloseTimer.Stop();
            if (e.NewValue is true)
            {
                if (_pinService?.IsPinEnabled ?? false)
                {
                    PinOverlay.IsLocked = true;
                }
            }
            else
            {
                ViewModel.SearchText = string.Empty;
            }
        };

        // 闲时自动关闭：窗口失去前台即开始计时，回到前台停止；到点自动 Close（复用关闭到托盘逻辑）
        _idleCloseTimer.Tick += (_, _) =>
        {
            _idleCloseTimer.Stop();
            if (IsVisible && WindowState != WindowState.Minimized)
            {
                AppLog.Info("[NotesListWindow] 列表窗口长时间不在前台，自动收起到托盘");
                Close();
            }
        };

        Activated += (_, _) =>
        {
            _idleCloseTimer.Stop();
            if (PinOverlay.IsLocked) return;
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            {
                if (PinOverlay.IsLocked) return;
                RestoreOptimalFocus();
            });
        };

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

        Closing += (s, e) =>
        {
            SaveWindowPlacement();
            ViewModel.SearchText = string.Empty;
            if (!App.IsShuttingDown && (_settingsService?.MinimizeToTrayOnClose ?? true))
            {
                e.Cancel = true;
                Hide();
                _ = Task.Run(async () =>
                {
                    await Task.Delay(1000);
                    NativeMethods.TrimWorkingSet();
                });
            }
        };

        StateChanged += (s, e) =>
        {
            if (WindowState == WindowState.Minimized)
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(1000);
                    NativeMethods.TrimWorkingSet();
                });
            }
        };

        WeakReferenceMessenger.Default.Register<ShowNotesListRequestedMessage>(this, (_, _) =>
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
            {
                if (WindowState == WindowState.Minimized)
                {
                    WindowState = WindowState.Normal;
                }
                Show();
                Activate();
                Focus();

                var hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd != IntPtr.Zero)
                {
                    NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
                    NativeMethods.SetForegroundWindow(hwnd);
                }
            });
        });

        PreviewKeyDown += NotesListWindow_PreviewKeyDown;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (PresentationSource.FromVisual(this) is HwndSource source)
        {
            source.AddHook(WndProc);
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_ACTIVATE_INSTANCE && NativeMethods.WM_ACTIVATE_INSTANCE != 0)
        {
            if (WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Normal;
            }
            Show();
            Activate();
            Topmost = true;
            Topmost = false;
            Focus();
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void FilterAllRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (DataContext is NotesListViewModel vm)
        {
            vm.SelectedFilterIndex = 0;
        }
    }

    private void FilterPinnedRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (DataContext is NotesListViewModel vm)
        {
            vm.SelectedFilterIndex = 1;
        }
    }

    private void NoteCard_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 排除内部按钮触发的冒泡
        if (e.OriginalSource is DependencyObject dep)
        {
            var btn = FindVisualAncestor<System.Windows.Controls.Button>(dep);
            if (btn != null) return;
        }

        if (sender is FrameworkElement { DataContext: Note note })
        {
            NotesListBox.SelectedItem = note;
        }

        // 双击打开新窗口
        if (e.ClickCount == 2 && sender is FrameworkElement { DataContext: Note note2 })
        {
            ViewModel.OpenNoteCommand.Execute(note2);
            e.Handled = true;
        }
    }


    private void CardMoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement btn)
        {
            var card = FindVisualAncestor<Border>(btn);
            if (card?.ContextMenu != null)
            {
                card.ContextMenu.PlacementTarget = btn;
                card.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                card.ContextMenu.IsOpen = true;
                e.Handled = true;
            }
        }
    }

    private void ContextOpenNote_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: Note note })
        {
            ViewModel.OpenNoteCommand.Execute(note);
        }
    }

    private async void ContextTogglePin_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: Note note })
        {
            await ViewModel.TogglePinNoteCommand.ExecuteAsync(note);
        }
    }

    private void OpenArchiveButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.OpenArchiveCommand.Execute(null);
    }

    private void OpenSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.OpenSettingsCommand.Execute(null);
    }

    private async void ContextDeleteNote_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: Note note })
        {
            await ViewModel.DeleteNoteCommand.ExecuteAsync(note);
        }
    }


    internal ListBox NotesListBoxControl => NotesListBox;
    internal ListBox SearchHitsListBoxControl => SearchHitsListBox;
    internal Wpf.Ui.Controls.TextBox SearchBoxControl => SearchBox;

    internal void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down)
        {
            if (ViewModel.IsSearching)
            {
                if (ViewModel.SearchResults.Count > 0)
                {
                    int targetIndex = (SearchHitsListBox.SelectedIndex >= 0 && SearchHitsListBox.SelectedIndex < ViewModel.SearchResults.Count)
                        ? SearchHitsListBox.SelectedIndex
                        : 0;
                    FocusListBoxItem(SearchHitsListBox, targetIndex);
                    e.Handled = true;
                }
            }
            else
            {
                if (ViewModel.FilteredNotes.Any())
                {
                    int targetIndex = (NotesListBox.SelectedIndex >= 0 && NotesListBox.SelectedIndex < NotesListBox.Items.Count)
                        ? NotesListBox.SelectedIndex
                        : 0;
                    FocusListBoxItem(NotesListBox, targetIndex);
                    e.Handled = true;
                }
            }
        }
        else if (e.Key == Key.Enter)
        {
            if (ViewModel.IsSearching && ViewModel.SearchResults.Count > 0)
            {
                var hit = (SearchHitsListBox.SelectedItem as SearchHit) ?? ViewModel.SearchResults[0];
                SearchHitsListBox.SelectedItem = hit;
                ViewModel.JumpToSearchHitCommand.Execute(hit);
                e.Handled = true;
            }
            else if (!ViewModel.IsSearching && ViewModel.FilteredNotes.Any())
            {
                var note = (NotesListBox.SelectedItem as Note) ?? ViewModel.FilteredNotes.FirstOrDefault();
                if (note != null)
                {
                    NotesListBox.SelectedItem = note;
                    ViewModel.OpenNoteCommand.Execute(note);
                    e.Handled = true;
                }
            }
        }
        else if (e.Key == Key.Escape)
        {
            if (ViewModel.IsSearching)
            {
                ViewModel.SearchText = string.Empty;
                e.Handled = true;
            }
        }
    }

    // 兼容历史调用
    internal void SearchBox_KeyDown(object sender, KeyEventArgs e) => SearchBox_PreviewKeyDown(sender, e);

    /// <summary>
    /// 当窗口激活或从外部便签页返回时，智能恢复最佳键盘交互焦点
    /// </summary>
    public void RestoreOptimalFocus()
    {
        if (PinOverlay.IsLocked) return;

        // 如果用户已经在可编辑文本框内，不夺取输入焦点
        if (Keyboard.FocusedElement is System.Windows.Controls.TextBox ||
            Keyboard.FocusedElement is System.Windows.Controls.PasswordBox)
        {
            return;
        }

        if (ViewModel.IsSearching)
        {
            if (SearchHitsListBox.SelectedIndex >= 0 && SearchHitsListBox.SelectedIndex < ViewModel.SearchResults.Count)
            {
                FocusListBoxItem(SearchHitsListBox, SearchHitsListBox.SelectedIndex);
            }
            else if (ViewModel.SearchResults.Count > 0)
            {
                FocusListBoxItem(SearchHitsListBox, 0);
            }
            else
            {
                SearchBox.Focus();
                SearchBox.SelectAll();
            }
        }
        else
        {
            if (NotesListBox.SelectedIndex >= 0 && NotesListBox.SelectedIndex < NotesListBox.Items.Count)
            {
                FocusListBoxItem(NotesListBox, NotesListBox.SelectedIndex);
            }
            else if (NotesListBox.Items.Count > 0)
            {
                FocusListBoxItem(NotesListBox, 0);
            }
            else
            {
                SearchBox.Focus();
                SearchBox.SelectAll();
            }
        }
    }

    private void NotesListWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            if (e.Key == Key.N)
            {
                ViewModel.NewNoteCommand.Execute(null);
                e.Handled = true;
                return;
            }
            if (e.Key == Key.F)
            {
                SearchBox.Focus();
                SearchBox.SelectAll();
                e.Handled = true;
                return;
            }
        }

        // 若当前焦点在可编辑文本框（如搜索框、PIN密码框），由控件各自处理，不在此全局拦截
        if (Keyboard.FocusedElement is System.Windows.Controls.TextBox ||
            Keyboard.FocusedElement is System.Windows.Controls.PasswordBox)
        {
            return;
        }

        if (e.Key == Key.Down)
        {
            if (ViewModel.IsSearching)
            {
                if (SearchHitsListBox.Items.Count > 0)
                {
                    int next = SearchHitsListBox.SelectedIndex < 0
                        ? 0
                        : Math.Min(SearchHitsListBox.SelectedIndex + 1, SearchHitsListBox.Items.Count - 1);
                    FocusListBoxItem(SearchHitsListBox, next);
                    e.Handled = true;
                }
            }
            else
            {
                if (NotesListBox.Items.Count > 0)
                {
                    int next = NotesListBox.SelectedIndex < 0
                        ? 0
                        : Math.Min(NotesListBox.SelectedIndex + 1, NotesListBox.Items.Count - 1);
                    FocusListBoxItem(NotesListBox, next);
                    e.Handled = true;
                }
            }
        }
        else if (e.Key == Key.Up)
        {
            if (ViewModel.IsSearching)
            {
                if (SearchHitsListBox.SelectedIndex > 0)
                {
                    FocusListBoxItem(SearchHitsListBox, SearchHitsListBox.SelectedIndex - 1);
                    e.Handled = true;
                }
                else
                {
                    SearchBox.Focus();
                    SearchBox.SelectAll();
                    e.Handled = true;
                }
            }
            else
            {
                if (NotesListBox.SelectedIndex > 0)
                {
                    FocusListBoxItem(NotesListBox, NotesListBox.SelectedIndex - 1);
                    e.Handled = true;
                }
                else
                {
                    SearchBox.Focus();
                    SearchBox.SelectAll();
                    e.Handled = true;
                }
            }
        }
        else if (e.Key == Key.Enter)
        {
            if (ViewModel.IsSearching)
            {
                if (SearchHitsListBox.SelectedItem is SearchHit hit)
                {
                    ViewModel.JumpToSearchHitCommand.Execute(hit);
                    e.Handled = true;
                }
                else if (ViewModel.SearchResults.Count > 0)
                {
                    var hit0 = ViewModel.SearchResults[0];
                    SearchHitsListBox.SelectedItem = hit0;
                    ViewModel.JumpToSearchHitCommand.Execute(hit0);
                    e.Handled = true;
                }
            }
            else
            {
                if (NotesListBox.SelectedItem is Note note)
                {
                    ViewModel.OpenNoteCommand.Execute(note);
                    e.Handled = true;
                }
                else if (ViewModel.FilteredNotes.FirstOrDefault() is Note note0)
                {
                    NotesListBox.SelectedItem = note0;
                    ViewModel.OpenNoteCommand.Execute(note0);
                    e.Handled = true;
                }
            }
        }
        else if (e.Key == Key.Escape)
        {
            if (ViewModel.IsSearching)
            {
                ViewModel.SearchText = string.Empty;
                SearchBox.Focus();
                e.Handled = true;
            }
        }
    }

    internal void FocusListBoxItem(ListBox listBox, int index)
    {
        if (index < 0 || listBox.Items.Count == 0) return;
        index = Math.Clamp(index, 0, listBox.Items.Count - 1);
        listBox.SelectedIndex = index;

        if (listBox.ItemContainerGenerator.ContainerFromIndex(index) is ListBoxItem item)
        {
            item.Focus();
            item.BringIntoView();
        }
        else
        {
            listBox.ScrollIntoView(listBox.Items[index]);
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                var container = listBox.ItemContainerGenerator.ContainerFromIndex(index) as ListBoxItem;
                container?.Focus();
                container?.BringIntoView();
            });
        }
    }

    private void ListBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is ListBox listBox && e.NewFocus == listBox)
        {
            int target = listBox.SelectedIndex >= 0 ? listBox.SelectedIndex : 0;
            if (listBox.Items.Count > target)
            {
                FocusListBoxItem(listBox, target);
                e.Handled = true;
            }
        }
    }

    internal void SearchHitsListBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Up && SearchHitsListBox.SelectedIndex <= 0)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && SearchHitsListBox.SelectedItem is SearchHit hit)
        {
            ViewModel.JumpToSearchHitCommand.Execute(hit);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            ViewModel.SearchText = string.Empty;
            SearchBox.Focus();
            e.Handled = true;
        }
    }

    internal void NotesListBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Up && NotesListBox.SelectedIndex <= 0)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && NotesListBox.SelectedItem is Note note)
        {
            ViewModel.OpenNoteCommand.Execute(note);
            e.Handled = true;
        }
    }

    private void SearchHitCard_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SearchHit hit })
        {
            SearchHitsListBox.SelectedItem = hit;
            ViewModel.JumpToSearchHitCommand.Execute(hit);
        }
    }

    private static T? FindVisualAncestor<T>(DependencyObject current) where T : DependencyObject
    {
        while (current != null)
        {
            if (current is T match) return match;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private record WindowPlacementData(double Left, double Top, double Width, double Height);

    private void RestoreWindowPlacement()
    {
        try
        {
            if (System.IO.File.Exists(StickyNotes.Infrastructure.AppPaths.WindowConfigPath))
            {
                var json = System.IO.File.ReadAllText(StickyNotes.Infrastructure.AppPaths.WindowConfigPath);
                var config = System.Text.Json.JsonSerializer.Deserialize<WindowPlacementData>(json);
                if (config != null && config.Width >= MinWidth && config.Height >= MinHeight)
                {
                    double vLeft = SystemParameters.VirtualScreenLeft;
                    double vTop = SystemParameters.VirtualScreenTop;
                    double vRight = vLeft + SystemParameters.VirtualScreenWidth;
                    double vBottom = vTop + SystemParameters.VirtualScreenHeight;

                    if (config.Left >= vLeft - 20 && config.Left + 50 <= vRight &&
                        config.Top >= vTop - 20 && config.Top + 50 <= vBottom)
                    {
                        WindowStartupLocation = WindowStartupLocation.Manual;
                        Left = config.Left;
                        Top = config.Top;
                        Width = config.Width;
                        Height = config.Height;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[NotesListWindow] 保存主窗口位置失败: {ex.Message}", ex);
        }
    }

    private void SaveWindowPlacement()
    {
        try
        {
            if (WindowState == WindowState.Normal)
            {
                var data = new WindowPlacementData(
                    Left,
                    Top,
                    ActualWidth > 0 ? ActualWidth : Width,
                    ActualHeight > 0 ? ActualHeight : Height
                );
                var json = System.Text.Json.JsonSerializer.Serialize(data);
                System.IO.File.WriteAllText(StickyNotes.Infrastructure.AppPaths.WindowConfigPath, json);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[NotesListWindow] 保存主窗口位置失败: {ex.Message}", ex);
        }
    }
}
