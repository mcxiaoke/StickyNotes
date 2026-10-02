using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using StickyNotes.Models;
using StickyNotes.ViewModels;

namespace StickyNotes.Views;

/// <summary>
/// 便签管理中心主窗口交互逻辑
/// </summary>
public partial class NotesListWindow : Wpf.Ui.Controls.FluentWindow
{
    public NotesListViewModel ViewModel => (NotesListViewModel)DataContext;

    public NotesListWindow(NotesListViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        Loaded += async (_, _) =>
        {
            RestoreWindowPlacement();
            await ViewModel.LoadNotesAsync();
        };

        Closing += (_, _) =>
        {
            SaveWindowPlacement();
        };

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
            ViewModel.SelectSearchHitCommand.Execute(ViewModel.SearchResults[0]);
            e.Handled = true;
        }
    }

    private void SearchHitCard_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SearchHit hit })
        {
            ViewModel.SelectSearchHitCommand.Execute(hit);
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
