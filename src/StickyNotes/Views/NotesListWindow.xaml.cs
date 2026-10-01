using System.Windows;
using System.Windows.Input;
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
            if (e.Key == Key.N && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                ViewModel.NewNoteCommand.Execute(null);
                e.Handled = true;
            }
        };
    }
}
