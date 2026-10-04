using System.Diagnostics;
using System.Reflection;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Win32;
using StickyNotes.Infrastructure;
using StickyNotes.Messages;
using StickyNotes.Services;
using StickyNotes.Sync;
using StickyNotes.Views;

namespace StickyNotes.ViewModels;

public record FontSizeOption(string Label, double Size);

public record AutoCloseOption(string Label, int Minutes);

public record SyncBackendOption(string Label, SyncBackendType Type);

public record SyncIntervalOption(string Label, int Minutes);

/// <summary>
/// 应用程序独立设置窗口 ViewModel
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _settingsService;
    private readonly ExportImportService _exportImportService;
    private readonly AutoStartService? _autoStartService;
    private readonly PinService? _pinService;
    private readonly SyncHost? _syncHost;

    /// <summary>WebDAV 密码输入框的即时值（不落盘，保存时经 DPAPI 加密；空表示沿用已存凭据）</summary>
    private string _webDavPasswordInput = string.Empty;

    /// <summary>S3 SecretKey 输入框的即时值（语义同上）</summary>
    private string _s3SecretKeyInput = string.Empty;

    [ObservableProperty]
    private bool _isSyncBusy;

    [ObservableProperty]
    private SyncBackendType _selectedSyncBackendType;

    public IReadOnlyList<FontSizeOption> FontSizeOptions { get; } = new List<FontSizeOption>
    {
        new("小 (12 pt)", 12.0),
        new("标准 (14 pt - 默认)", 14.0),
        new("中 (16 pt)", 16.0),
        new("大 (18 pt)", 18.0),
        new("特大 (20 pt)", 20.0),
        new("超大 (24 pt)", 24.0)
    };

    public IReadOnlyList<AutoCloseOption> AutoCloseOptions { get; } = new List<AutoCloseOption>
    {
        new("不启用", 0),
        new("离开 1 分钟后", 1),
        new("离开 5 分钟后", 5),
        new("离开 10 分钟后", 10),
        new("离开 30 分钟后", 30)
    };

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

    [ObservableProperty]
    private double _selectedFontSize;

    [ObservableProperty]
    private int _selectedAutoCloseMinutes;

    [ObservableProperty]
    private int _selectedSyncIntervalMinutes;

    public string PreviewText => "这是一条便签示例文本：支持纯文本与多行输入，清晰易读 (StickyNotes 123 ABC)";

    public string AppVersion { get; }
    public string GitCommit { get; }
    public string BuildTime { get; }
    public string RuntimeInfo { get; }
    public string DataDirectoryPath => AppPaths.DataDirectory;
    public bool IsPortableMode => AppPaths.IsPortableMode;
    public string DeploymentModeDescription => AppPaths.DeploymentModeDescription;

    public bool MinimizeToTrayOnClose
    {
        get => _settingsService.MinimizeToTrayOnClose;
        set
        {
            if (_settingsService.MinimizeToTrayOnClose != value)
            {
                _settingsService.MinimizeToTrayOnClose = value;
                OnPropertyChanged();
            }
        }
    }

    public bool EnableGlobalHotKeys
    {
        get => _settingsService.EnableGlobalHotKeys;
        set
        {
            if (_settingsService.EnableGlobalHotKeys != value)
            {
                _settingsService.EnableGlobalHotKeys = value;
                WeakReferenceMessenger.Default.Send(new HotKeyConfigChangedMessage(value));
                OnPropertyChanged();
            }
        }
    }

    public bool StartMinimized
    {
        get => _settingsService.StartMinimized;
        set
        {
            if (_settingsService.StartMinimized != value)
            {
                _settingsService.StartMinimized = value;
                OnPropertyChanged();
            }
        }
    }

    public bool IsAutoStartEnabled
    {
        get => _autoStartService?.IsAutoStartEnabled ?? false;
        set
        {
            if (_autoStartService != null && _autoStartService.IsAutoStartEnabled != value)
            {
                _autoStartService.SetAutoStart(value);
                OnPropertyChanged();
                // 「开机启动时最小化到托盘」依赖于开机自启，状态变化必须通知其可操作性
                OnPropertyChanged(nameof(IsStartMinimizedEnabled));
            }
        }
    }

    /// <summary>
    /// 「开机启动时最小化到托盘」是否可操作。开机自启关闭时该项无意义，
    /// 若仍可切换会让用户看到自相矛盾的状态组合。
    /// </summary>
    public bool IsStartMinimizedEnabled => IsAutoStartEnabled;

    /// <summary>PIN 锁定是否生效</summary>
    public bool IsPinEnabled => _pinService?.IsPinEnabled ?? false;

    /// <summary>PIN 配置数据是否已损坏（settings.json 中的盐/哈希缺失或非法，锁定已被强制失效）</summary>
    public bool IsPinDataCorrupted => _settingsService.Settings.PinDataCorrupted;

    /// <summary>PIN 锁定状态描述文字</summary>
    public string PinStatusText => IsPinEnabled ? "已启用，打开列表与归档时需输入 PIN" : "未启用";

    public SettingsViewModel(
        SettingsService settingsService,
        ExportImportService exportImportService,
        AutoStartService? autoStartService = null,
        PinService? pinService = null,
        SyncHost? syncHost = null)
    {
        _settingsService = settingsService;
        _exportImportService = exportImportService;
        _autoStartService = autoStartService;
        _pinService = pinService;
        _syncHost = syncHost;

        _selectedFontSize = _settingsService.EditorFontSize;
        _selectedAutoCloseMinutes = _settingsService.ListAutoCloseMinutes;
        _selectedSyncBackendType = _settingsService.Settings.Sync.BackendType;
        _selectedSyncIntervalMinutes = _settingsService.Settings.Sync.EffectiveIntervalMinutes;

        if (_syncHost != null)
        {
            _syncHost.StateChanged += RefreshSyncStatusOnDispatcher;
        }

        RefreshSyncStatus();

        // 获取程序集构建与版本信息
        var assembly = Assembly.GetExecutingAssembly();
        var infoVer = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        AppVersion = !string.IsNullOrWhiteSpace(infoVer) ? infoVer : (assembly.GetName().Version?.ToString(3) ?? "1.0.0");

        var metadata = assembly.GetCustomAttributes<AssemblyMetadataAttribute>().ToDictionary(m => m.Key, m => m.Value);
        GitCommit = metadata.TryGetValue("GitCommit", out var commit) && !string.IsNullOrWhiteSpace(commit) ? commit : "dev";
        BuildTime = metadata.TryGetValue("BuildTime", out var time) && !string.IsNullOrWhiteSpace(time) ? time : DateTime.Now.ToString("yyyy-MM-dd");

        RuntimeInfo = $".NET 8.0 ({System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture})";
    }

    partial void OnSelectedFontSizeChanged(double value)
    {
        _settingsService.SetEditorFontSize(value);
    }

    partial void OnSelectedAutoCloseMinutesChanged(int value)
    {
        _settingsService.ListAutoCloseMinutes = value;
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

    /// <summary>解析用于弹对话框的宿主窗口（当前激活窗口或主窗口）</summary>
    private static Window? GetOwnerWindow() =>
        Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
        ?? Application.Current?.MainWindow;

    private void RefreshPinState()
    {
        OnPropertyChanged(nameof(IsPinEnabled));
        OnPropertyChanged(nameof(PinStatusText));
        OnPropertyChanged(nameof(IsPinDataCorrupted));
    }

    [RelayCommand]
    private void EnablePin()
    {
        if (_pinService == null || IsPinEnabled) return;

        AppLog.Info("[SettingsViewModel] 用户请求启用 PIN 锁定");
        if (PinSetupDialog.Execute(GetOwnerWindow(), _pinService, PinDialogMode.Enable))
        {
            // 用户重新设置成功后，清除「数据已损坏」告警标记
            _settingsService.Settings.PinDataCorrupted = false;
            RefreshPinState();
            AppLog.Info("[SettingsViewModel] PIN 锁定已启用");
        }
    }

    [RelayCommand]
    private void ChangePin()
    {
        if (_pinService == null || !IsPinEnabled) return;

        AppLog.Info("[SettingsViewModel] 用户请求修改 PIN");
        if (PinSetupDialog.Execute(GetOwnerWindow(), _pinService, PinDialogMode.Change))
        {
            RefreshPinState();
            AppLog.Info("[SettingsViewModel] PIN 已修改");
        }
    }

    [RelayCommand]
    private void ClearPin()
    {
        if (_pinService == null || !IsPinEnabled) return;

        AppLog.Info("[SettingsViewModel] 用户请求清除 PIN");
        if (PinSetupDialog.Execute(GetOwnerWindow(), _pinService, PinDialogMode.Disable))
        {
            RefreshPinState();
            AppLog.Info("[SettingsViewModel] PIN 已清除，锁定失效");
        }
    }

    [RelayCommand]
    public async Task ExportJsonAsync()
    {
        var dialog = new SaveFileDialog
        {
            Title = "导出所有便签备份",
            Filter = "JSON 文件 (*.json)|*.json",
            FileName = $"StickyNotes_Backup_{DateTime.Now:yyyyMMdd_HHmmss}.json"
        };

        if (dialog.ShowDialog() == true)
        {
            try
            {
                int count = await _exportImportService.ExportNotesAsync(dialog.FileName);
                MessageBox.Show(
                    $"导出成功！\n共导出 {count} 条便签至文件：\n{dialog.FileName}",
                    "便签导出完成",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information
                );
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"导出失败: {ex.Message}",
                    "导出错误",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error
                );
            }
        }
    }

    [RelayCommand]
    public async Task ImportJsonAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "导入便签备份文件",
            Filter = "JSON 文件 (*.json)|*.json"
        };

        if (dialog.ShowDialog() == true)
        {
            try
            {
                int count = await _exportImportService.ImportNotesAsync(dialog.FileName);
                MessageBox.Show(
                    $"导入完成！\n成功导入/合并了 {count} 条便签。",
                    "便签导入完成",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information
                );

                // 通知主管理窗口全量重载（导入可能新增/更新任意条数，不能逐条插卡片）。
                // 禁止改回 NoteCreatedMessage + new Note()：那条路径会生成一张幽灵便签。原 F-P1-2 子问题 2。
                WeakReferenceMessenger.Default.Send(new NotesReloadedRequestedMessage());
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"导入失败: {ex.Message}",
                    "导入错误",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error
                );
            }
        }
    }

    [RelayCommand]
    public void OpenDataFolder()
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", AppPaths.DataDirectory)
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"打开目录失败: {ex.Message}", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ---------------- 网络同步 ----------------

    private SyncSettings Sync => _settingsService.Settings.Sync;

    public string SyncDeviceId => string.IsNullOrWhiteSpace(Sync.DeviceId) ? "（尚未生成）" : Sync.DeviceId;

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
    /// <summary>设置页同步状态行（最近成功时间 / 最近错误摘要）</summary>
    public string SyncStatusText
    {
        get => _syncStatusText;
        private set => SetProperty(ref _syncStatusText, value);
    }

    /// <summary>由设置窗口 code-behind 在 PasswordChanged 时推送（密码框不参与 XAML 绑定）</summary>
    public void SetWebDavPasswordInput(string password)
    {
        _webDavPasswordInput = password ?? string.Empty;
        if (_webDavPasswordInput.Length == 0) return; // 清空输入框不代表清除已存凭据

        Sync.WebDavPassword = CredentialProtector.Protect(_webDavPasswordInput);
        SaveSyncSettings();
        OnPropertyChanged(nameof(SyncWebDavCredentialHint));
    }

    /// <summary>由设置窗口 code-behind 在 PasswordChanged 时推送</summary>
    public void SetS3SecretKeyInput(string secretKey)
    {
        _s3SecretKeyInput = secretKey ?? string.Empty;
        if (_s3SecretKeyInput.Length == 0) return;

        Sync.S3SecretKey = CredentialProtector.Protect(_s3SecretKeyInput);
        SaveSyncSettings();
        OnPropertyChanged(nameof(SyncS3CredentialHint));
    }

    private void SaveSyncSettings()
    {
        _settingsService.SaveSettings();
        _syncHost?.ApplySettingsChanged();
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
