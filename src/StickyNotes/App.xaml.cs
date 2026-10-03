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
    private static Mutex? _instanceMutex;
    private IServiceProvider? _serviceProvider;

    /// <summary>
    /// 全局应用是否正在关闭中（用于隔离普通窗口关闭与应用退出）
    /// </summary>
    public static bool IsShuttingDown { get; private set; }

    public static IServiceProvider Services => ((App)Current)._serviceProvider!;

    protected override async void OnStartup(StartupEventArgs e)
    {
        // 1. 单实例互斥量保护（基于数据路径确定性 SHA256 哈希，防止 .NET 8 字符串哈希随机化导致单实例失效）
        var mutexName = AppPaths.InstanceMutexName;
        _instanceMutex = new Mutex(true, mutexName, out bool isNew);

        if (!isNew)
        {
            // 广播唤醒已有实例并退出当前进程
            NativeMethods.NotifyExistingInstance();
            Shutdown();
            return;
        }

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
        PerformSafeShutdown();
    }

    /// <summary>
    /// 执行应用退出前的统一安全持久化（坐标记忆 + 脏数据刷盘）
    /// </summary>
    private void PerformSafeShutdown()
    {
        if (IsShuttingDown) return;
        IsShuttingDown = true;

        try
        {
            // 1. 同步持久化当前桌面上所有打开贴纸的精确坐标与尺寸，保持 IsOpen = true
            _serviceProvider?.GetService<WindowManager>()?.PersistActiveWindowsBoundsOnExit();

            // 2. 将所有待写脏数据直接写入 SQLite，绝不走 UI 消息总线，防止死锁
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

        _instanceMutex?.ReleaseMutex();
        _instanceMutex?.Dispose();
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
