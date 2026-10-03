using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using StickyNotes.Data;
using StickyNotes.Infrastructure;
using StickyNotes.Services;
using StickyNotes.ViewModels;

namespace StickyNotes.Tests;

/// <summary>
/// 测试环境隔离与 STA 线程执行器
/// </summary>
[TestClass]
public class TestEnvironment
{
    public static string TempRoot { get; private set; } = string.Empty;

    [AssemblyInitialize]
    public static void AssemblyInit(TestContext context)
    {
        TempRoot = Path.Combine(
            Path.GetTempPath(),
            "StickyNotes.Tests",
            DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);

        Directory.CreateDirectory(TempRoot);
        AppPaths.DataDirOverride = TempRoot;

        context.WriteLine($"[TestEnvironment] 测试数据已隔离至临时目录: {TempRoot}");
    }

    [AssemblyCleanup]
    public static void AssemblyCleanup()
    {
        AppPaths.DataDirOverride = null;
        try
        {
            if (!string.IsNullOrEmpty(TempRoot) && Directory.Exists(TempRoot))
            {
                Directory.Delete(TempRoot, true);
            }
        }
        catch { }
    }

    /// <summary>
    /// 为测试装配一个与生产 <c>App.ConfigureServices</c> 等价的最小 DI 容器
    /// （含 <see cref="NoteViewModel"/> 所需的 <see cref="SettingsService"/>）。
    /// <see cref="WindowManager"/> 不再容忍 <c>null</c> 容器：它必须经容器解析 NoteViewModel。
    /// </summary>
    public static IServiceProvider CreateWindowManagerContainer(INoteRepository repository)
    {
        var services = new ServiceCollection();
        services.AddSingleton(repository);
        services.AddSingleton(TestEnvironment.CreateSettingsService());
        services.AddSingleton(new AutoSaveCoordinator(repository));
        services.AddSingleton<WindowManager>();
        services.AddTransient<NoteViewModel>();
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// 创建一个隔离在本次测试临时目录内的 <see cref="SettingsService"/>。
    /// 便签 ViewModel 现要求显式注入设置服务（不再容忍绕过 DI 的隐式 fallback）。
    /// </summary>
    public static SettingsService CreateSettingsService() =>
        new(Path.Combine(TempRoot, $"settings-{Guid.NewGuid():N}.json"));

    /// <summary>
    /// 确保存在一个不会自动关停的 Application 实例，并载入主题资源
    /// </summary>
    public static void EnsureApplication()
    {
        var current = Application.Current;
        if (current == null)
        {
            var app = new Application
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown
            };

            // 合并 WPF-UI 与本地主题资源
            var rd = new ResourceDictionary();
            rd.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/Wpf.Ui;component/Resources/Theme/Light.xaml", UriKind.Absolute)
            });
            rd.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/Wpf.Ui;component/Resources/Wpf.Ui.xaml", UriKind.Absolute)
            });
            rd.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/StickyNotes;component/Resources/StickyColors.xaml", UriKind.Absolute)
            });
            rd.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/StickyNotes;component/Resources/DesignTokens.xaml", UriKind.Absolute)
            });

            app.Resources = rd;
        }
        else if (current.Dispatcher.CheckAccess())
        {
            current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        }
    }

    private static readonly Thread _staThread;
    private static readonly System.Collections.Concurrent.BlockingCollection<Action> _staQueue = new();

    static TestEnvironment()
    {
        _staThread = new Thread(() =>
        {
            EnsureApplication();
            while (!_staQueue.IsCompleted)
            {
                try
                {
                    var action = _staQueue.Take();
                    action();
                }
                catch (InvalidOperationException)
                {
                    break;
                }
            }
        });
        _staThread.SetApartmentState(ApartmentState.STA);
        _staThread.IsBackground = true;
        _staThread.Start();
    }

    /// <summary>
    /// 在专用的全局 STA 线程上运行 UI 代码并捕获异常。
    /// 带超时保护（F-P2-27）：原先的无限 <c>Wait()</c> 会让一条挂死的 UI 用例永久挂住整个测试进程。
    /// </summary>
    public static void RunInSta(Action action)
    {
        Exception? captured = null;
        using var waitHandle = new ManualResetEventSlim();

        _staQueue.Add(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                captured = ex;
            }
            finally
            {
                waitHandle.Set();
            }
        });

        if (!waitHandle.Wait(TimeSpan.FromMinutes(2)))
        {
            throw new TimeoutException(
                "STA 测试线程 2 分钟内未返回，疑似 UI 用例挂死。" +
                "（F-P2-27：原先的无限等待会挂住整个测试进程；超时上限保证 CI 可快速失败定位。）");
        }

        if (captured != null)
        {
            throw new AggregateException("STA 测试线程抛出异常", captured);
        }
    }

    /// <summary>
    /// 解析截图输出目录。优先向上定位仓库根（以 <c>StickyNotes.slnx</c> 为标记），
    /// 输出到其 <c>temp/screenshots</c>；找不到标记时回退系统临时目录。
    /// 原实现硬编码从输出目录上溯 5 层（F-P2-27），耦合输出目录层级，工程结构稍变就会写错位置。
    /// </summary>
    private static string ResolveScreenshotDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 10 && dir != null; i++)
        {
            if (File.Exists(Path.Combine(dir.FullName, "StickyNotes.slnx")))
            {
                return Path.Combine(dir.FullName, "temp", "screenshots");
            }

            dir = dir.Parent;
        }

        return Path.Combine(Path.GetTempPath(), "StickyNotes.Tests", "screenshots");
    }

    /// <summary>
    /// 将窗口实际渲染并抓取保存为真实的高清 PNG 截图
    /// </summary>
    public static void SaveWindowSnapshot(Window win, double width, double height, string filename)
    {
        win.Width = width;
        win.Height = height;
        win.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        win.Show();

        // 刷新 WPF 调度队列，等待测量排版全部完成
        var frame = new System.Windows.Threading.DispatcherFrame();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Loaded,
            (Action)(() => { frame.Continue = false; }));
        System.Windows.Threading.Dispatcher.PushFrame(frame);

        Thread.Sleep(120);

        // 获取当前物理屏幕或窗口的真实 HiDPI 缩放比（如 150% 即 144 DPI，125% 即 120 DPI）
        var dpi = VisualTreeHelper.GetDpi(win);
        double scaleX = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1.0;
        double scaleY = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1.0;
        double dpiX = dpi.PixelsPerInchX > 0 ? dpi.PixelsPerInchX : 96.0;
        double dpiY = dpi.PixelsPerInchY > 0 ? dpi.PixelsPerInchY : 96.0;

        double dipW = win.ActualWidth > 0 ? win.ActualWidth : width;
        double dipH = win.ActualHeight > 0 ? win.ActualHeight : height;

        int pxW = (int)Math.Max(1, Math.Round(dipW * scaleX));
        int pxH = (int)Math.Max(1, Math.Round(dipH * scaleY));

        var rtb = new RenderTargetBitmap(pxW, pxH, dpiX, dpiY, PixelFormats.Pbgra32);
        Visual target = win;
        if (win.AllowsTransparency && win.Content is Visual contentVisual)
        {
            target = contentVisual;
        }
        rtb.Render(target);

        // 保存至项目根目录下的 temp/screenshots
        string screenshotDir = ResolveScreenshotDirectory();
        Directory.CreateDirectory(screenshotDir);

        string fullPath = Path.Combine(screenshotDir, filename);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));

        using (var fs = File.Create(fullPath))
        {
            enc.Save(fs);
        }

        win.Close();
    }

    /// <summary>
    /// 将指定 FrameworkElement（例如弹出层、卡片等）单独测量排版并抓取保存为真实的高清 PNG 截图
    /// </summary>
    public static void SaveElementSnapshot(FrameworkElement element, double width, double height, string filename)
    {
        element.Width = width;
        element.Height = height;
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();

        // 默认按 150% (144 DPI) 或当前屏幕 DPI 渲染高清元素
        double scale = 1.5;
        double dpiValue = 144.0;
        int pxW = (int)Math.Max(1, Math.Round(width * scale));
        int pxH = (int)Math.Max(1, Math.Round(height * scale));

        var rtb = new RenderTargetBitmap(pxW, pxH, dpiValue, dpiValue, PixelFormats.Pbgra32);
        rtb.Render(element);

        string screenshotDir = ResolveScreenshotDirectory();
        Directory.CreateDirectory(screenshotDir);

        string fullPath = Path.Combine(screenshotDir, filename);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));

        using var fs = File.Create(fullPath);
        enc.Save(fs);
    }
}
