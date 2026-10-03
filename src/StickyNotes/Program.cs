using System;
using System.Threading;
using StickyNotes.Infrastructure;

namespace StickyNotes;

/// <summary>
/// 应用程序首要入口点：在加载任何 WPF 框架、XAML 资源与大型程序集之前，
/// 第一时间完成单实例互斥量检测与已有窗口唤醒，实现第二实例毫秒级退出。
/// </summary>
public static class Program
{
    /// <summary>退出码：本次为第二实例，已唤醒既有实例后退出（非错误）</summary>
    public const int ExitCodeAlreadyRunning = 2;

    private static Mutex? _instanceMutex;

    [STAThread]
    public static void Main(string[] args)
    {
        // 1. [第 1 行代码] 单实例互斥量保护（基于数据路径确定性 SHA256 哈希）
        var mutexName = AppPaths.InstanceMutexName;
        _instanceMutex = new Mutex(true, mutexName, out bool isNew);

        if (!isNew)
        {
            // 广播唤醒已有实例并立即退出当前进程（耗时仅数毫秒，彻底避开 WPF、XAML 解析与嵌入程序集解压）
            NativeMethods.NotifyExistingInstance();
            // 显式退出码：调用方（脚本/父进程）可借此区分「已有实例在运行」与「启动失败」
            Environment.ExitCode = ExitCodeAlreadyRunning;
            return;
        }

        try
        {
            // 2. 仅在新实例（首个实例）时才初始化 WPF 框架并启动主消息泵
            var app = new App();
            app.InitializeComponent();
            app.Run();
        }
        catch (Exception ex)
        {
            // 启动阶段的最外层兜底：此处 AppLog 可能尚不可用，双通道输出保证痕迹不丢
            AppLog.Error($"[Program] 应用启动/运行阶段未捕获异常: {ex.Message}", ex);
            System.Diagnostics.Debug.WriteLine($"[Program] 应用启动/运行阶段未捕获异常: {ex}");
            throw;
        }
        finally
        {
            _instanceMutex?.ReleaseMutex();
            _instanceMutex?.Dispose();
            _instanceMutex = null;
            AppLog.Info("[Program] 单实例互斥量已释放，进程退出");
        }
    }
}