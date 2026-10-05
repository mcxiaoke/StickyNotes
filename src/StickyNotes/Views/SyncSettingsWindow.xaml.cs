using System.ComponentModel;
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

        ViewModel.CredentialsCommitted += ClearPasswordBoxes;
        Closing += SyncSettingsWindow_OnClosing;
    }

    /// <summary>保存成功后清空两个密码框（DPAPI 密文已落盘，明文不留在界面）</summary>
    private void ClearPasswordBoxes()
    {
        WebDavPasswordBox.Password = string.Empty;
        S3SecretKeyBox.Password = string.Empty;
    }

    /// <summary>WebDAV 密码框不参与 XAML 绑定（Password 不可双向绑定），经此推送到 VM 草稿</summary>
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

    /// <summary>关闭时若有未保存的同步配置修改，询问后再退出（草稿不落盘）</summary>
    private void SyncSettingsWindow_OnClosing(object? sender, CancelEventArgs e)
    {
        if (ViewModel.ConfirmDiscardDraft())
        {
            return;
        }

        e.Cancel = true;
    }
}
