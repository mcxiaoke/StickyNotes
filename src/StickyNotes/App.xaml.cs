using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using StickyNotes.Data;
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

    public static IServiceProvider Services => ((App)Current)._serviceProvider!;

    protected override async void OnStartup(StartupEventArgs e)
    {
        // 1. 单实例互斥量保护（防止多开进程锁死 SQLite）
        const string mutexName = "Global\\StickyNotes_App_Instance_Mutex_mcxiaoke";
        _instanceMutex = new Mutex(true, mutexName, out bool isNew);

        if (!isNew)
        {
            // 唤醒已有实例并退出当前进程
            NativeMethods.BringExistingInstanceToFront();
            Shutdown();
            return;
        }

        base.OnStartup(e);

        // 2. 捕获未处理异常并记录，防止数据丢失
        DispatcherUnhandledException += (_, args) =>
        {
            try
            {
                var coordinator = _serviceProvider?.GetService<AutoSaveCoordinator>();
                coordinator?.FlushAllAsync().GetAwaiter().GetResult();
            }
            catch { }

            System.Diagnostics.Debug.WriteLine($"[App] 未处理异常: {args.Exception}");
            args.Handled = true;
        };

        // 3. 构建依赖注入服务容器
        var services = new ServiceCollection();
        ConfigureServices(services);
        _serviceProvider = services.BuildServiceProvider();

        // 4. 初始化数据库与执行 PRAGMA 增量迁移
        var dbCtx = _serviceProvider.GetRequiredService<SqliteDatabaseContext>();
        await dbCtx.InitializeAndMigrateAsync();

        // 5. 每日启动轮转冷备份检查
        BackupService.RunDailyBackupIfNeeded();

        // 6. 恢复上次未关闭的便签贴纸
        var repo = _serviceProvider.GetRequiredService<INoteRepository>();
        var windowManager = _serviceProvider.GetRequiredService<WindowManager>();
        var allActive = await repo.GetAllActiveAsync();

        foreach (var note in allActive.Where(n => n.IsOpen))
        {
            windowManager.OpenOrActivateNote(note);
        }

        // 7. 显示管理中心主窗口
        var mainWindow = _serviceProvider.GetRequiredService<NotesListWindow>();
        MainWindow = mainWindow;
        mainWindow.Show();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        // 退出前强制立即将所有便签刷盘
        if (_serviceProvider != null)
        {
            var coordinator = _serviceProvider.GetService<AutoSaveCoordinator>();
            if (coordinator != null)
            {
                await coordinator.FlushAllAsync();
                coordinator.Dispose();
            }
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

        // 领域服务
        services.AddSingleton<ISearchService, SearchService>();
        services.AddSingleton<AutoSaveCoordinator>();
        services.AddSingleton<WindowManager>();

        // ViewModels
        services.AddSingleton<NotesListViewModel>();
        services.AddTransient<NoteViewModel>();

        // Views
        services.AddSingleton<NotesListWindow>();
    }
}

internal static class NativeMethods
{
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr FindWindow(string? lpClassName, string lpWindowName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private const int SW_RESTORE = 9;

    public static void BringExistingInstanceToFront()
    {
        var hWnd = FindWindow(null, "便签管理中心");
        if (hWnd != IntPtr.Zero)
        {
            ShowWindow(hWnd, SW_RESTORE);
            SetForegroundWindow(hWnd);
        }
    }
}
