using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
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
            await ViewModel.FlushSaveAsync();
        };

        Closing += async (_, _) =>
        {
            await ViewModel.FlushSaveAsync();
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
            catch
            {
                // 兜底调用系统按行滚动
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
        MoreMenuPopup.IsOpen = !MoreMenuPopup.IsOpen;
    }

    private void ShowNotesList_Click(object sender, RoutedEventArgs e)
    {
        MoreMenuPopup.IsOpen = false;
        // 触发唤醒主管理窗口解耦消息
        WeakReferenceMessenger.Default.Send(new ShowNotesListRequestedMessage());
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
    }

    private async void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
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
