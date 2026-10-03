using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using StickyNotes.Infrastructure;
using StickyNotes.Messages;
using StickyNotes.Models;
using StickyNotes.ViewModels;

namespace StickyNotes.Views;

/// <summary>
/// 独立彩色便签贴纸窗口后台逻辑
/// </summary>
public partial class NoteWindow : Window
{
    public NoteViewModel ViewModel => (NoteViewModel)DataContext;
    public TextBox Editor => EditorTextBox;
    public System.Windows.Controls.Primitives.Popup MoreMenu => MoreMenuPopup;
    public Button CloseButtonControl => CloseNoteButton;

    public NoteWindow(NoteViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        // 实时同步尺寸与坐标至实体，保证持久化绝对准确
        SizeChanged += (_, _) =>
        {
            if (WindowState == WindowState.Normal && ActualWidth > 0 && ActualHeight > 0)
            {
                ViewModel.Note.WindowWidth = ActualWidth;
                ViewModel.Note.WindowHeight = ActualHeight;
            }
        };

        LocationChanged += (_, _) =>
        {
            if (WindowState == WindowState.Normal)
            {
                ViewModel.Note.WindowX = Left;
                ViewModel.Note.WindowY = Top;
            }
        };

        // 监听窗口失焦与关闭事件，立即触发无延迟强制刷盘保存
        Deactivated += async (_, _) =>
        {
            // 无待保存改动时短路，避免「在便签与列表间来回切换 = 每切一次一条全量 UPDATE」（原 F-P2-10）
            if (!ViewModel.HasPendingChanges) return;
            await ViewModel.FlushSaveAsync();
        };

        // 关闭必须是同步阻塞刷盘：此前的 async void 写法中，await 之后的续体在
        // 「全部窗口 Closed → App.OnExit → 进程结束」这一真实时序下根本不会执行，
        // 导致最后 500ms 输入不落库、也不广播到主列表（原 F-P1-4）。
        // 复用 AutoSaveCoordinator 中已验证不会死锁的 ConfigureAwait(false).GetAwaiter().GetResult() 模式。
        Closing += (_, _) =>
        {
            try
            {
                ViewModel.FlushSaveBlocking();
            }
            catch (Exception ex)
            {
                AppLog.Error($"[NoteWindow] 关闭时同步刷盘失败: {ex.Message}", ex);
            }
        };
    }

    /// <summary>
    /// 顶部工具栏支持拖拽移动窗口
    /// </summary>
    private void Toolbar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    /// <summary>
    /// 精准跳行与高亮定位算法实现
    /// </summary>
    public void JumpToSearchHit(int targetCharIndex, int keywordLength)
    {
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;

        Activate();

        // 调度至 Loaded 优先级，确保 TextBox 完成文本排版测量后滚动生效
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            var text = EditorTextBox.Text;
            if (string.IsNullOrEmpty(text)) return;

            // 1. 安全防越界保护（防止搜索后内容变动导致索引越界）
            int safeStart = Math.Clamp(targetCharIndex, 0, text.Length);
            int safeLen = Math.Clamp(keywordLength, 0, text.Length - safeStart);

            // 2. 聚焦并高亮选中目标文本
            EditorTextBox.Focus();
            EditorTextBox.Select(safeStart, safeLen);

            // 3. 计算视觉行号与矩形坐标，智能视口居中
            try
            {
                var charRect = EditorTextBox.GetRectFromCharacterIndex(safeStart);
                double targetOffset = EditorTextBox.VerticalOffset + charRect.Top - (EditorTextBox.ActualHeight / 2.5);
                EditorTextBox.ScrollToVerticalOffset(Math.Max(0, targetOffset));
            }
            catch (Exception ex)
            {
                // 兜底调用系统按行滚动（记录原因便于排查跳转定位异常）
                AppLog.Warn($"[NoteWindow] 精确跳行定位失败，降级为按行滚动: {ex.Message}");
                int visualLine = EditorTextBox.GetLineIndexFromCharacterIndex(safeStart);
                EditorTextBox.ScrollToLine(visualLine);
            }
        });
    }

    private void NewNoteButton_Click(object sender, RoutedEventArgs e)
    {
        // 触发新建便签解耦消息
        WeakReferenceMessenger.Default.Send(new NewNoteRequestedMessage());
    }

    private void MoreMenuButton_Click(object sender, RoutedEventArgs e)
    {
        // 注意：MoreMenuPopup.StaysOpen="False"，鼠标点击按钮时 Popup 会先因失去外部点击而自动关闭，
        // 若此处使用 IsOpen = !IsOpen 取反，会在已关闭的基础上再次打开，导致菜单只能开不能关。
        // 因此这里只负责「打开」，关闭交给 Popup 自身的 StaysOpen 行为。
        MoreMenuPopup.IsOpen = true;
    }

    private void ShowNotesList_Click(object sender, RoutedEventArgs e)
    {
        MoreMenuPopup.IsOpen = false;
        // 调度至 Normal 优先级异步执行，确保 Popup 关闭完成并释放焦点后，再唤醒并置前主列表窗口
        Dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
        {
            WeakReferenceMessenger.Default.Send(new ShowNotesListRequestedMessage());
        });
    }

    private async void ColorSelected_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is NoteColor color)
        {
            MoreMenuPopup.IsOpen = false;
            await ViewModel.SetColorAsync(color);
        }
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (e.Key == Key.N)
            {
                NewNoteButton_Click(this, new RoutedEventArgs());
                e.Handled = true;
            }
            else if (e.Key == Key.W)
            {
                Close();
                e.Handled = true;
            }
            else if (e.Key == Key.D)
            {
                DeleteButton_Click(this, new RoutedEventArgs());
                e.Handled = true;
            }
            else if (e.Key == Key.H)
            {
                ShowNotesList_Click(this, new RoutedEventArgs());
                e.Handled = true;
            }
            else if (e.Key == Key.P)
            {
                ViewModel.TogglePinCommand.Execute(null);
                e.Handled = true;
            }
        }

        // 纯键盘用户离开编辑区的通道（F-P1-11 剩余项）：编辑区 AcceptsTab="True" 会吞掉 Tab
        // 用于插入制表符，若无本通道，键盘用户进入正文后将无法用键盘到达任何工具按钮。
        // 仅在焦点确实位于正文编辑区时拦截，避免影响其它控件的 Esc/Tab 行为。
        if ((e.Key == Key.Escape ||
             (e.Key == Key.Tab && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control))
            && Keyboard.FocusedElement == EditorTextBox)
        {
            EditorTextBox.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            e.Handled = true;
        }
    }

    private async void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        MoreMenuPopup.IsOpen = false;
        await ViewModel.DeleteAsync();
        Close();
    }


    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void NoteWindow_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            e.Handled = true;
            double step = e.Delta > 0 ? 1.0 : -1.0;
            ViewModel.ChangeFontSize(step);
        }
    }
}
