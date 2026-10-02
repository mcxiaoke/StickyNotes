using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using StickyNotes.Infrastructure;

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
    /// 在专用的全局 STA 线程上运行 UI 代码并捕获异常
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

        waitHandle.Wait();

        if (captured != null)
        {
            throw new AggregateException("STA 测试线程抛出异常", captured);
        }
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
        string projectRoot = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\..\..\.."));
        string screenshotDir = Path.Combine(projectRoot, "temp", "screenshots");
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

        string projectRoot = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\..\..\.."));
        string screenshotDir = Path.Combine(projectRoot, "temp", "screenshots");
        Directory.CreateDirectory(screenshotDir);

        string fullPath = Path.Combine(screenshotDir, filename);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));

        using var fs = File.Create(fullPath);
        enc.Save(fs);
    }
}
