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

    public NotesListViewModel ViewModel => (NotesListViewModel)DataContext;

    public NotesListWindow(NotesListViewModel viewModel, SettingsService? settingsService = null)
    {
        InitializeComponent();
        DataContext = viewModel;
        _settingsService = settingsService;

        Loaded += async (_, _) =>
        {
            RestoreWindowPlacement();
            await ViewModel.LoadNotesAsync();
        };

        Closing += (s, e) =>
        {
            SaveWindowPlacement();
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

        KeyDown += (s, e) =>
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                if (e.Key == Key.N)
                {
                    ViewModel.NewNoteCommand.Execute(null);
                    e.Handled = true;
                }
                else if (e.Key == Key.F)
                {
                    SearchBox.Focus();
                    SearchBox.SelectAll();
                    e.Handled = true;
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
        };
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

        // 双击打开新窗口
        if (e.ClickCount == 2 && sender is FrameworkElement { DataContext: Note note })
        {
            ViewModel.OpenNoteCommand.Execute(note);
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


    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && ViewModel.SearchResults.Count > 0)
        {
            ViewModel.JumpToSearchHitCommand.Execute(ViewModel.SearchResults[0]);
            e.Handled = true;
        }
    }

    private void SearchHitCard_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SearchHit hit })
        {
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
        catch { }
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
        catch { }
    }
}
