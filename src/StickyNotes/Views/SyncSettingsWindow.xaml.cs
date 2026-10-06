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
        // VM 注册为 Transient 而其订阅的 SyncHost 是单例：窗口关闭时必须摘除订阅，否则持续泄漏（P2-7）
        Closed += (_, _) => ViewModel.DetachSyncHostEvents();
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
        // 应用退出流程中的窗口关闭不再询问草稿（P1-6）：此时退出标记已置位且不可回滚，
        // 若在此 e.Cancel 中止 Shutdown，应用会停留在「活着但已半退出」的坏状态，
        // 后续所有便签关闭都不再回写 IsOpen=false。退出时未保存的草稿按既定语义丢弃。
        if (App.IsShuttingDown)
        {
            return;
        }

        if (ViewModel.ConfirmDiscardDraft())
        {
            return;
        }

        e.Cancel = true;
    }
}
