using System.Net.NetworkInformation;
using System.Windows;
using CommunityToolkit.Mvvm.Messaging;
using StickyNotes.Data;
using StickyNotes.Infrastructure;
using StickyNotes.Messages;
using StickyNotes.Sync;

namespace StickyNotes.Services;

/// <summary>
/// 同步触发与生命周期中枢（协议设计 §6）：
/// 启动延迟触发、便签新增/修改防抖 5s、删除/恢复防抖 2s、后台定时（默认 15 分钟）、
/// 手动「立即同步」、网络恢复触发；全部汇入 <see cref="SyncEngine"/> 的单飞入口。
/// </summary>
public sealed class SyncHost : IDisposable
{
    /// <summary>编辑类变更的防抖毫秒数（评审决议 1：新增/修改后 5 秒）</summary>
    private const int EditDebounceMs = 5000;

    /// <summary>删除/恢复类变更的防抖毫秒数（尽快传播墓碑）</summary>
    private const int MetaDebounceMs = 2000;

    /// <summary>启动后首轮同步的延迟秒数</summary>
    private const int StartupDelaySeconds = 10;

    private readonly INoteRepository _repository;
    private readonly SettingsService _settingsService;
    private readonly SyncEngine _engine;
    private readonly WindowManager? _windowManager;
    private readonly SyncStateStore _stateStore;

    private readonly object _triggerGate = new();
    private CancellationTokenSource? _pendingTriggerCts;
    private Timer? _timer;
    private IStorageBackend? _backend;
    private string? _backendFingerprint;
    private bool _started;

    /// <summary>同步状态变化（成功/失败）后触发，设置页据此刷新展示</summary>
    public event Action? StateChanged;

    public SyncHost(
        INoteRepository repository,
        SettingsService settingsService,
        SyncEngine engine,
        WindowManager? windowManager = null,
        SyncStateStore? stateStore = null)
    {
        _repository = repository;
        _settingsService = settingsService;
        _engine = engine;
        _windowManager = windowManager;
        _stateStore = stateStore ?? new SyncStateStore();
    }

    /// <summary>当前同步状态（设置页展示）</summary>
    public SyncState CurrentState => _stateStore.Load();

    /// <summary>启动全部触发器（应用 OnStartup 末尾调用一次；重复调用无副作用）</summary>
    public void Start()
    {
        if (_started) return;
        _started = true;

        RegisterMessengers();
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;

        EnsureDeviceId();
        ResetTimer();

        AppLog.Info($"[SyncHost] 已启动（间隔={_settingsService.Settings.Sync.EffectiveIntervalMinutes} 分钟, 后端={_settingsService.Settings.Sync.BackendType}）");

        // 首轮同步：启动稳定后延迟执行，不阻塞 UI 呈现
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(StartupDelaySeconds)).ConfigureAwait(false);
                await SyncNowAsync("startup").ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppLog.Error($"[SyncHost] 启动同步异常: {ex.Message}", ex);
            }
        });
    }

    /// <summary>设置变更（保存后）调用：重建定时器并在启用时自动发起同步；后端实例按配置指纹在下一轮自动重建</summary>
    public void ApplySettingsChanged()
    {
        if (!_started) return;
        ResetTimer();
        AppLog.Info("[SyncHost] 同步设置已变更，定时器与后端配置已刷新");

        var settings = _settingsService.Settings.Sync;
        if (settings.Enabled && settings.IsConfigured)
        {
            AppLog.Info("[SyncHost] 设置变更已保存且同步已启用，正在触发自动同步 (settings_saved)");
            _ = Task.Run(async () =>
            {
                try
                {
                    await SyncNowAsync("settings_saved").ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    AppLog.Error($"[SyncHost] 保存配置后自动同步异常: {ex.Message}", ex);
                }
            });
        }
    }

    /// <summary>立即执行一轮同步（手动按钮 / 各触发器统一入口）</summary>
    public async Task<SyncRoundSummary?> SyncNowAsync(string reason)
    {
        var settings = _settingsService.Settings.Sync;
        if (!settings.Enabled || !settings.IsConfigured)
        {
            return null;
        }

        var backend = GetOrBuildBackend(settings);
        if (backend == null)
        {
            return null;
        }

        AppLog.Info($"[SyncHost] 开始执行同步 (reason={reason}, backend={settings.BackendType}, encryption={settings.EnableEncryption})");

        try
        {
            var summary = await _engine.RunAsync(backend, GetDeviceId(), settings.EnableEncryption).ConfigureAwait(false);

            if (summary != null)
            {
                // 上传逐条容错（P2-4）：部分失败不再抛异常，摘要照常返回；
                // 失败数记入 LastError 供设置页展示，失败的 id 下轮差量对账自动补传
                var uploadFailed = summary.UploadFailedIds.Count;
                _stateStore.Update(s =>
                {
                    s.LastSuccessAt = DateTime.UtcNow;
                    s.LastError = uploadFailed > 0
                        ? $"本轮有 {uploadFailed} 条便签上传失败，将在下一轮同步自动重试"
                        : null;
                    s.LastAttemptAt = DateTime.UtcNow;
                    s.LastUploadedCount = summary.Uploaded;
                    s.LastDownloadedCount = summary.Downloaded;
                    s.LastListedCount = summary.Listed;
                });
                AppLog.Info($"[SyncHost] 同步完成({reason}): listed={summary.Listed}, downloaded={summary.Downloaded}, " +
                            $"uploaded={summary.Uploaded}, uploadFailed={uploadFailed}, skipped={summary.SkippedInvalid}, " +
                            $"guarded={summary.GuardedSkipped}, incomplete={summary.IncompleteCount}");

                if (summary.AppliedIds.Count > 0)
                {
                    RefreshUiAfterDownload(summary.AppliedIds);
                }

                OnStateChanged();
            }

            return summary;
        }
        catch (Exception ex)
        {
            _stateStore.Update(s =>
            {
                s.LastError = ex.Message;
                s.LastAttemptAt = DateTime.UtcNow;
            });
            AppLog.Error($"[SyncHost] 同步失败({reason}): {ex.Message}", ex);
            OnStateChanged();
            return null;
        }
    }

    // ---- 内部实现 ----

    private void RegisterMessengers()
    {
        // 发送方可能在线程池线程（防抖落盘任务），处理器只做线程安全的防抖登记
        WeakReferenceMessenger.Default.Register<NoteContentChangedMessage>(this,
            (_, _) => TriggerDebounced(EditDebounceMs, "edit"));
        WeakReferenceMessenger.Default.Register<NoteCreatedMessage>(this,
            (_, _) => TriggerDebounced(EditDebounceMs, "create"));
        WeakReferenceMessenger.Default.Register<NoteMetaChangedMessage>(this,
            (_, _) => TriggerDebounced(EditDebounceMs, "meta"));
        WeakReferenceMessenger.Default.Register<NoteArchivedMessage>(this,
            (_, _) => TriggerDebounced(MetaDebounceMs, "archive"));
        WeakReferenceMessenger.Default.Register<NoteRestoredMessage>(this,
            (_, _) => TriggerDebounced(MetaDebounceMs, "restore"));
        WeakReferenceMessenger.Default.Register<NoteDeletedMessage>(this,
            (_, _) => TriggerDebounced(MetaDebounceMs, "delete"));
    }

    private void TriggerDebounced(int delayMilliseconds, string reason)
    {
        lock (_triggerGate)
        {
            // 与 AutoSaveCoordinator 同一约束：只 Cancel 不 Dispose，释放权归持有任务（原 F-P1-7）
            _pendingTriggerCts?.Cancel();
            _pendingTriggerCts = new CancellationTokenSource();
            var token = _pendingTriggerCts.Token;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(delayMilliseconds, token).ConfigureAwait(false);
                    await SyncNowAsync(reason).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // 被新触发合并，正常
                }
                catch (Exception ex)
                {
                    AppLog.Error($"[SyncHost] 防抖同步({reason})异常: {ex.Message}", ex);
                }
            }, CancellationToken.None);
        }
    }

    private void ResetTimer()
    {
        var dueTime = TimeSpan.FromMinutes(_settingsService.Settings.Sync.EffectiveIntervalMinutes);

        _timer?.Dispose();
        _timer = new Timer(
            _ => _ = Task.Run(() => SyncNowAsync("timer")),
            null, dueTime, dueTime);
    }

    private IStorageBackend? GetOrBuildBackend(SyncSettings settings)
    {
        var fingerprint = BuildFingerprint(settings);
        if (_backend != null && string.Equals(_backendFingerprint, fingerprint, StringComparison.Ordinal))
        {
            return _backend;
        }

        _backend?.Dispose();
        _backend = null;
        _backendFingerprint = null;

        try
        {
            _backend = StorageBackendFactory.Create(settings);
            _backendFingerprint = fingerprint;
            return _backend;
        }
        catch (Exception ex)
        {
            AppLog.Error($"[SyncHost] 构建同步后端失败: {ex.Message}", ex);
            return null;
        }
    }

    private static string BuildFingerprint(SyncSettings s) =>
        $"{s.BackendType}|{s.EnableEncryption}|{s.WebDavUrl}|{s.WebDavUsername}|{s.WebDavPassword}|{s.WebDavAllowInsecureHttp}|" +
        $"{s.S3Endpoint}|{s.S3Bucket}|{s.S3BasePrefix}|{s.S3AccessKey}|{s.S3SecretKey}";

    private string GetDeviceId()
    {
        var id = _settingsService.Settings.Sync.DeviceId;
        return string.IsNullOrWhiteSpace(id) ? "win-unknown" : id;
    }

    private void EnsureDeviceId()
    {
        if (!string.IsNullOrWhiteSpace(_settingsService.Settings.Sync.DeviceId)) return;

        _settingsService.Settings.Sync.DeviceId = SyncSettings.GenerateDeviceId();
        _settingsService.SaveSettings();
        AppLog.Info($"[SyncHost] 已生成本机设备标识 {_settingsService.Settings.Sync.DeviceId}");
    }

    private void RefreshUiAfterDownload(IReadOnlyList<Guid> appliedIds)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null)
        {
            // 无 UI 环境（测试）：仍需广播，供已注册的 VM 刷新
            WeakReferenceMessenger.Default.Send(new NotesReloadedRequestedMessage());
            return;
        }

        _ = dispatcher.BeginInvoke(async () =>
        {
            try
            {
                if (_windowManager != null)
                {
                    await _windowManager.RefreshOpenNoteContentsAsync(appliedIds).ConfigureAwait(true);
                }

                // 列表/归档窗口全量重载（与导入备份同一通道）
                WeakReferenceMessenger.Default.Send(new NotesReloadedRequestedMessage());
            }
            catch (Exception ex)
            {
                AppLog.Error($"[SyncHost] 同步后 UI 刷新失败: {ex.Message}", ex);
            }
        });
    }

    private void OnStateChanged()
    {
        try
        {
            StateChanged?.Invoke();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[SyncHost] StateChanged 订阅者异常: {ex.Message}");
        }
    }

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        if (e.IsAvailable)
        {
            TriggerDebounced(3000, "network-restored");
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
        WeakReferenceMessenger.Default.UnregisterAll(this);
        _backend?.Dispose();
        _backend = null;
    }
}
