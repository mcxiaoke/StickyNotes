using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using StickyNotes.Data;
using StickyNotes.Infrastructure;
using StickyNotes.Services;
using StickyNotes.ViewModels;
using StickyNotes.Views;

namespace StickyNotes;

/// <summary>
/// 应用程序主入口生命周期管理
/// </summary>
public partial class App : Application
{
    private IServiceProvider? _serviceProvider;
    private bool _hasPerformedShutdown;

    /// <summary>
    /// 全局应用是否正在关闭中（用于隔离普通窗口关闭与应用退出）
    /// </summary>
    public static bool IsShuttingDown { get; private set; }

    /// <summary>
    /// 在**发起退出之前**置位关闭标志（托盘「退出便签」、系统注销/关机等所有主动退出路径都必须先调用）。
    /// 这是 F-P0-1 的关键：WPF 的真实事件顺序是「全部窗口 Closed → App.OnExit」，
    /// 若等到 OnExit 才置位，窗口关闭回调会误判为「用户主动关闭」而把 IsOpen 写成 false，
    /// 导致下次启动一张贴纸都不出现。因此必须在 Shutdown() 之前先声明退出意图。
    /// </summary>
    public static void BeginShutdown()
    {
        if (IsShuttingDown) return;
        IsShuttingDown = true;
        AppLog.Info("[App] 已进入退出流程，窗口关闭回调将不再回写 IsOpen=false");
    }

    public static IServiceProvider Services => ((App)Current)._serviceProvider!;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 2. 注册三层全局异常捕获，记录日志并安全刷盘，杜绝静默崩溃与数据丢失
        DispatcherUnhandledException += (_, args) =>
        {
            AppLog.Error($"[App] Dispatcher 未处理异常: {args.Exception.Message}", args.Exception);
            try
            {
                _serviceProvider?.GetService<AutoSaveCoordinator>()?.FlushAllDirectToStorage();
            }
            catch { }
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                AppLog.Error($"[App] AppDomain 未处理异常: {ex.Message}", ex);
            }
            try
            {
                _serviceProvider?.GetService<AutoSaveCoordinator>()?.FlushAllDirectToStorage();
            }
            catch { }
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLog.Error($"[App] TaskScheduler 未观察异常: {args.Exception.Message}", args.Exception);
            args.SetObserved();
        };

        // 3. 监听 Windows 系统关机/注销事件
        SystemEvents.SessionEnding += OnSessionEnding;

        AppLog.Info($"[App] StickyNotes 启动，环境: {Environment.Version}, OS: {Environment.OSVersion}");

        // 4. 构建依赖注入服务容器
        var services = new ServiceCollection();
        ConfigureServices(services);
        _serviceProvider = services.BuildServiceProvider();

        // 5. 初始化数据库与执行 PRAGMA 增量迁移
        var dbCtx = _serviceProvider.GetRequiredService<SqliteDatabaseContext>();
        await dbCtx.InitializeAndMigrateAsync();

        // 6. 每日启动冷备份在后台线程异步执行，不阻塞 UI 渲染呈现
        _ = Task.Run(() => BackupService.RunDailyBackupIfNeeded());

        // 7. 恢复上次未关闭的便签贴纸（保持原位与物理尺寸）
        var repo = _serviceProvider.GetRequiredService<INoteRepository>();
        var windowManager = _serviceProvider.GetRequiredService<WindowManager>();
        var allActive = await repo.GetAllActiveAsync();

        foreach (var note in allActive.Where(n => n.IsOpen))
        {
            windowManager.OpenOrActivateNote(note);
        }

        // 8. 保持后台托盘常驻模式
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // 9. 初始化系统托盘图标与全局快捷键
        _serviceProvider.GetRequiredService<TrayIconService>().Initialize();
        _serviceProvider.GetRequiredService<HotKeyService>().Initialize();

        // 10. 管理中心主窗口呈现（若携带 --autostart 或 --minimized 参数，则保持托盘静默不弹窗）
        var mainWindow = _serviceProvider.GetRequiredService<NotesListWindow>();
        MainWindow = mainWindow;

        bool startMinimized = e.Args.Any(a => a.Equals("--autostart", StringComparison.OrdinalIgnoreCase) || 
                                              a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));
        if (!startMinimized)
        {
            mainWindow.Show();
        }
        else
        {
            AppLog.Info("[App] 检测到自启动参数 (--autostart/--minimized)，主窗口保持在后台托盘");
        }

        // 11. 启动初始化稳定后，延迟 5 秒修剪冷启动与 JIT 编译产生的瞬时工作集页面
        _ = Task.Run(async () =>
        {
            await Task.Delay(5000);
            NativeMethods.TrimWorkingSet();
        });
    }

    private void OnSessionEnding(object? sender, SessionEndingEventArgs e)
    {
        AppLog.Info($"[App] 收到操作系统注销/关机通知 ({e.Reason})，立即同步落盘");
        // 关机不经过 Shutdown()，此处必须直接完成「先存坐标、再标记退出」的原子动作
        _serviceProvider?.GetService<WindowManager>()?.BeginShutdownAndPersistPinnedPlacement();
        PerformSafeShutdown();
    }

    /// <summary>
    /// 执行应用退出前的统一安全持久化（坐标记忆 + 脏数据刷盘）。
    /// 主路径已在 TrayIconService 中于 Shutdown() 之前调用（那时窗口字典仍完整）；
    /// 此处的调用是对 SessionEnding、异常退出等场景的兜底，用 _hasPerformedShutdown 保证幂等。
    /// </summary>
    private void PerformSafeShutdown()
    {
        if (_hasPerformedShutdown) return;

        _hasPerformedShutdown = true;

        try
        {
            // 1. 仅持久化「桌面置顶」便签的精确坐标与尺寸（不写 IsOpen，避免依赖退出事件顺序）
            _serviceProvider?.GetService<WindowManager>()?.PersistPinnedWindowsPlacementOnExit();

            // 2. 声明退出：确保随后窗口关闭回调不回写 IsOpen=false（幂等）
            BeginShutdown();

            // 3. 将所有待写脏数据直接写入 SQLite，绝不走 UI 消息总线，防止死锁
            _serviceProvider?.GetService<AutoSaveCoordinator>()?.FlushAllDirectToStorage();

            AppLog.Info("[App] 退出前安全落盘完成");
        }
        catch (Exception ex)
        {
            AppLog.Error($"[App] 退出安全落盘异常: {ex.Message}", ex);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SystemEvents.SessionEnding -= OnSessionEnding;
        PerformSafeShutdown();

        _serviceProvider?.GetService<TrayIconService>()?.Dispose();
        _serviceProvider?.GetService<HotKeyService>()?.Dispose();

        if (_serviceProvider is IDisposable disp)
        {
            disp.Dispose();
        }

        base.OnExit(e);
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        // 核心基础设施与数据层
        services.AddSingleton<SqliteDatabaseContext>();
        services.AddSingleton<INoteRepository, NoteRepository>();

        // 领域服务与配置
        services.AddSingleton<SettingsService>();
        services.AddSingleton<PinService>();
        services.AddSingleton<ExportImportService>();
        services.AddSingleton<ISearchService, SearchService>();
        services.AddSingleton<AutoSaveCoordinator>(sp => 
            new AutoSaveCoordinator(sp.GetRequiredService<INoteRepository>()));
        services.AddSingleton<WindowManager>();
        services.AddSingleton<AutoStartService>();
        services.AddSingleton<HotKeyService>();
        services.AddSingleton<TrayIconService>();

        // ViewModels
        services.AddSingleton<NotesListViewModel>();
        services.AddSingleton<ArchivedNotesViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddTransient<NoteViewModel>();

        // Views
        services.AddSingleton<NotesListWindow>();
        services.AddTransient<ArchivedNotesWindow>();
        services.AddTransient<SettingsWindow>();
    }
}
