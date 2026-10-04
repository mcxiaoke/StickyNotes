using System.Windows;
using System.Windows.Controls;
using StickyNotes.ViewModels;

namespace StickyNotes.Views;

public partial class SyncSettingsWindow : Wpf.Ui.Controls.FluentWindow
{
    public SyncSettingsViewModel ViewModel { get; }

    public SyncSettingsWindow(SyncSettingsViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    /// <summary>WebDAV 密码框不参与 XAML 绑定（Password 不可双向绑定），经此推送到 VM 并加密落盘</summary>
    private void WebDavPasswordBox_OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox box)
        {
            ViewModel.SetWebDavPasswordInput(box.Password);
        }
    }

    /// <summary>S3 SecretKey 输入推送（语义同上）</summary>
    private void S3SecretKeyBox_OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox box)
        {
            ViewModel.SetS3SecretKeyInput(box.Password);
        }
    }
}
