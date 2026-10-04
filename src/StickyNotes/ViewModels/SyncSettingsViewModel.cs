using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StickyNotes.Services;
using StickyNotes.Sync;

namespace StickyNotes.ViewModels;

public record SyncBackendOption(string Label, SyncBackendType Type);

public record SyncIntervalOption(string Label, int Minutes);

/// <summary>
/// 网络同步独立设置窗口 ViewModel（自 SettingsViewModel 拆分，2026-10-04）：
/// 后端选择与凭据、测试连接、立即同步、同步状态展示。
/// </summary>
public partial class SyncSettingsViewModel : ObservableObject
{
    private readonly SettingsService _settingsService;
    private readonly SyncHost? _syncHost;

    /// <summary>WebDAV 密码输入框的即时值（不落盘，保存时经 DPAPI 加密；空表示沿用已存凭据）</summary>
    private string _webDavPasswordInput = string.Empty;

    /// <summary>S3 SecretKey 输入框的即时值（语义同上）</summary>
    private string _s3SecretKeyInput = string.Empty;

    [ObservableProperty]
    private bool _isSyncBusy;

    [ObservableProperty]
    private SyncBackendType _selectedSyncBackendType;

    [ObservableProperty]
    private int _selectedSyncIntervalMinutes;

    public IReadOnlyList<SyncBackendOption> SyncBackendOptions { get; } = new List<SyncBackendOption>
    {
        new("WebDAV", SyncBackendType.WebDav),
        new("Cloudflare R2 (S3 兼容)", SyncBackendType.S3)
    };

    public IReadOnlyList<SyncIntervalOption> SyncIntervalOptions { get; } = new List<SyncIntervalOption>
    {
        new("5 分钟", 5),
        new("10 分钟", 10),
        new("15 分钟（默认）", 15),
        new("30 分钟", 30),
        new("60 分钟", 60),
        new("120 分钟", 120)
    };

    public bool IsWebDavPanelVisible => SelectedSyncBackendType == SyncBackendType.WebDav;

    public bool IsS3PanelVisible => SelectedSyncBackendType == SyncBackendType.S3;

    public string SyncDeviceId => string.IsNullOrWhiteSpace(Sync.DeviceId) ? "（尚未生成）" : Sync.DeviceId;

    public SyncSettingsViewModel(SettingsService settingsService, SyncHost? syncHost = null)
    {
        _settingsService = settingsService;
        _syncHost = syncHost;

        _selectedSyncBackendType = Sync.BackendType;
        _selectedSyncIntervalMinutes = Sync.EffectiveIntervalMinutes;

        if (_syncHost != null)
        {
            _syncHost.StateChanged += RefreshSyncStatusOnDispatcher;
        }

        RefreshSyncStatus();
    }

    public bool SyncEnabled
    {
        get => Sync.Enabled;
        set
        {
            if (Sync.Enabled == value) return;
            Sync.Enabled = value;
            SaveSyncSettings();
        }
    }

    /// <summary>显式允许明文 http://（内网 NAS；协议设计 §5.1）</summary>
    public bool SyncAllowInsecureHttp
    {
        get => Sync.WebDavAllowInsecureHttp;
        set
        {
            if (Sync.WebDavAllowInsecureHttp == value) return;
            Sync.WebDavAllowInsecureHttp = value;
            SaveSyncSettings();
        }
    }

    public string SyncWebDavUrl
    {
        get => Sync.WebDavUrl;
        set { if (Sync.WebDavUrl != value) { Sync.WebDavUrl = value; SaveSyncSettings(); } }
    }

    public string SyncWebDavUsername
    {
        get => Sync.WebDavUsername;
        set { if (Sync.WebDavUsername != value) { Sync.WebDavUsername = value; SaveSyncSettings(); } }
    }

    public string SyncWebDavCredentialHint =>
        string.IsNullOrEmpty(Sync.WebDavPassword) ? "凭据：未配置" : "凭据：已保存（输入新值可替换）";

    public string SyncS3Endpoint
    {
        get => Sync.S3Endpoint;
        set { if (Sync.S3Endpoint != value) { Sync.S3Endpoint = value; SaveSyncSettings(); } }
    }

    public string SyncS3Bucket
    {
        get => Sync.S3Bucket;
        set { if (Sync.S3Bucket != value) { Sync.S3Bucket = value; SaveSyncSettings(); } }
    }

    public string SyncS3BasePrefix
    {
        get => Sync.S3BasePrefix;
        set { if (Sync.S3BasePrefix != value) { Sync.S3BasePrefix = value; SaveSyncSettings(); } }
    }

    public string SyncS3AccessKey
    {
        get => Sync.S3AccessKey;
        set { if (Sync.S3AccessKey != value) { Sync.S3AccessKey = value; SaveSyncSettings(); } }
    }

    public string SyncS3CredentialHint =>
        string.IsNullOrEmpty(Sync.S3SecretKey) ? "凭据：未配置" : "凭据：已保存（输入新值可替换）";

    private string _syncStatusText = "同步未启用";
    /// <summary>同步状态行（最近成功时间 / 最近错误摘要）</summary>
    public string SyncStatusText
    {
        get => _syncStatusText;
        private set => SetProperty(ref _syncStatusText, value);
    }

    partial void OnSelectedSyncBackendTypeChanged(SyncBackendType value)
    {
        Sync.BackendType = value;
        SaveSyncSettings();
        OnPropertyChanged(nameof(IsWebDavPanelVisible));
        OnPropertyChanged(nameof(IsS3PanelVisible));
    }

    partial void OnSelectedSyncIntervalMinutesChanged(int value)
    {
        Sync.IntervalMinutes = value;
        SaveSyncSettings();
    }

    private SyncSettings Sync => _settingsService.Settings.Sync;

    private void SaveSyncSettings()
    {
        _settingsService.SaveSettings();
        _syncHost?.ApplySettingsChanged();
    }

    /// <summary>由窗口 code-behind 在 PasswordChanged 时推送（密码框不参与 XAML 绑定）</summary>
    public void SetWebDavPasswordInput(string password)
    {
        _webDavPasswordInput = password ?? string.Empty;
        if (_webDavPasswordInput.Length == 0) return; // 清空输入框不代表清除已存凭据

        Sync.WebDavPassword = CredentialProtector.Protect(_webDavPasswordInput);
        SaveSyncSettings();
        OnPropertyChanged(nameof(SyncWebDavCredentialHint));
    }

    /// <summary>由窗口 code-behind 在 PasswordChanged 时推送</summary>
    public void SetS3SecretKeyInput(string secretKey)
    {
        _s3SecretKeyInput = secretKey ?? string.Empty;
        if (_s3SecretKeyInput.Length == 0) return;

        Sync.S3SecretKey = CredentialProtector.Protect(_s3SecretKeyInput);
        SaveSyncSettings();
        OnPropertyChanged(nameof(SyncS3CredentialHint));
    }

    /// <summary>用界面当前编辑值构造测试用配置（凭据取输入框新值，缺省沿用已存值）</summary>
    private SyncSettings BuildEffectiveSyncSettings()
    {
        var s = Sync;
        return new SyncSettings
        {
            Enabled = s.Enabled,
            BackendType = s.BackendType,
            DeviceId = s.DeviceId,
            IntervalMinutes = s.IntervalMinutes,
            WebDavUrl = s.WebDavUrl,
            WebDavUsername = s.WebDavUsername,
            WebDavAllowInsecureHttp = s.WebDavAllowInsecureHttp,
            WebDavPassword = _webDavPasswordInput.Length == 0 ? s.WebDavPassword : CredentialProtector.Protect(_webDavPasswordInput),
            S3Endpoint = s.S3Endpoint,
            S3Bucket = s.S3Bucket,
            S3BasePrefix = s.S3BasePrefix,
            S3AccessKey = s.S3AccessKey,
            S3SecretKey = _s3SecretKeyInput.Length == 0 ? s.S3SecretKey : CredentialProtector.Protect(_s3SecretKeyInput)
        };
    }

    [RelayCommand]
    private async Task TestSyncConnectionAsync()
    {
        if (IsSyncBusy) return;
        IsSyncBusy = true;
        try
        {
            await StorageBackendFactory.TestConnectionAsync(BuildEffectiveSyncSettings());
            MessageBox.Show("连接成功，凭据有效。", "测试连接", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"连接失败：{ex.Message}", "测试连接", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsSyncBusy = false;
        }
    }

    [RelayCommand]
    private async Task SyncNowManualAsync()
    {
        if (IsSyncBusy) return;
        if (_syncHost == null) return;

        IsSyncBusy = true;
        try
        {
            var summary = await _syncHost.SyncNowAsync("manual");
            if (summary != null)
            {
                MessageBox.Show(
                    $"同步完成。\n远端对象：{summary.Listed}\n下行应用：{summary.Downloaded}（守卫跳过 {summary.GuardedSkipped}）\n上传：{summary.Uploaded}",
                    "立即同步", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else if (!Sync.Enabled || !Sync.IsConfigured)
            {
                MessageBox.Show("请先启用同步并配置服务器信息。", "立即同步", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        finally
        {
            IsSyncBusy = false;
        }
    }

    private void RefreshSyncStatusOnDispatcher()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null)
        {
            RefreshSyncStatus();
            return;
        }

        _ = dispatcher.BeginInvoke(RefreshSyncStatus);
    }

    private void RefreshSyncStatus()
    {
        if (!Sync.Enabled)
        {
            SyncStatusText = "同步未启用";
            return;
        }

        if (_syncHost == null)
        {
            SyncStatusText = "同步服务未运行";
            return;
        }

        var state = _syncHost.CurrentState;
        if (state.LastError != null)
        {
            var attempt = state.LastAttemptAt?.ToLocalTime().ToString("HH:mm:ss") ?? "--";
            var lastOk = state.LastSuccessAt?.ToLocalTime().ToString("MM-dd HH:mm") ?? "从未成功";
            var error = state.LastError.Length > 80 ? state.LastError[..80] + "…" : state.LastError;
            SyncStatusText = $"上次同步失败（{attempt}）：{error}；最近成功：{lastOk}";
        }
        else if (state.LastSuccessAt != null)
        {
            SyncStatusText = $"最近同步：{state.LastSuccessAt.Value.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
        }
        else
        {
            SyncStatusText = "已启用，尚未同步";
        }
    }
}
