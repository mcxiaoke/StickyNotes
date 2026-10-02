using StickyNotes.ViewModels;

namespace StickyNotes.Views;

public partial class SettingsWindow : Wpf.Ui.Controls.FluentWindow
{
    public SettingsViewModel ViewModel { get; }

    public SettingsWindow(SettingsViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }
}
