using System.Windows;
using System.Windows.Controls;
using StickyNotes.Models;
using StickyNotes.ViewModels;

namespace StickyNotes.Views;

public partial class ArchivedNotesWindow : Wpf.Ui.Controls.FluentWindow
{
    public ArchivedNotesViewModel ViewModel { get; }

    public ArchivedNotesWindow(ArchivedNotesViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();

        Loaded += async (_, _) =>
        {
            await ViewModel.LoadArchivedNotesAsync();
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
