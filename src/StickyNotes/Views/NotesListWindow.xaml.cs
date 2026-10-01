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
public partial class NotesListWindow : Window
{
    public NotesListViewModel ViewModel => (NotesListViewModel)DataContext;

    public NotesListWindow(NotesListViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        Loaded += async (_, _) =>
        {
            await ViewModel.LoadNotesAsync();
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

    private void NoteCard_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 排除内部按钮触发的冒泡
        if (e.OriginalSource is DependencyObject dep)
        {
            var btn = FindVisualAncestor<Button>(dep);
            if (btn != null) return;
        }

        if (sender is FrameworkElement { DataContext: Note note })
        {
            ViewModel.OpenNoteCommand.Execute(note);
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

    private async void ContextDeleteNote_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: Note note })
        {
            if (!string.IsNullOrWhiteSpace(note.Content))
            {
                var result = MessageBox.Show(
                    "确定要删除这条便签吗？",
                    "删除确认",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question
                );
                if (result != MessageBoxResult.Yes) return;
            }

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
}
