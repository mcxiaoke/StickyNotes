using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using StickyNotes.Data;
using StickyNotes.Messages;
using StickyNotes.Models;
using StickyNotes.Services;

namespace StickyNotes.ViewModels;

/// <summary>
/// 独立彩色便签贴纸的 ViewModel
/// </summary>
public partial class NoteViewModel : ObservableObject
{
    private readonly INoteRepository _repository;
    private readonly AutoSaveCoordinator _autoSaveCoordinator;
    private readonly SettingsService _settingsService;

    [ObservableProperty]
    private Note _note = new();

    [ObservableProperty]
    private string _content = string.Empty;

    [ObservableProperty]
    private NoteColor _color = NoteColor.Yellow;

    [ObservableProperty]
    private bool _alwaysOnTop;

    /// <summary>
    /// 兼容旧属性绑定
    /// </summary>
    public bool IsPinned
    {
        get => AlwaysOnTop;
        set => AlwaysOnTop = value;
    }

    [ObservableProperty]
    private double _fontSize = 14.0;

    public NoteViewModel(
        INoteRepository repository,
        AutoSaveCoordinator autoSaveCoordinator,
        SettingsService? settingsService = null)
    {
        _repository = repository;
        _autoSaveCoordinator = autoSaveCoordinator;
        _settingsService = settingsService ?? new SettingsService();

        _fontSize = _settingsService.EditorFontSize;

        // 监听字号全局调整
        WeakReferenceMessenger.Default.Register<FontSizeChangedMessage>(this, (_, msg) =>
        {
            FontSize = msg.Value;
        });
    }

    /// <summary>
    /// 交互式调整当前便签显示字号 (支持 Ctrl+滚轮缩放，限制在 10 ~ 36 pt)
    /// </summary>
    public void ChangeFontSize(double delta)
    {
        double newSize = Math.Clamp(FontSize + delta, 10.0, 36.0);
        if (Math.Abs(FontSize - newSize) > 0.1)
        {
            _settingsService.SetEditorFontSize(newSize);
        }
    }

    public void Initialize(Note note)
    {
        Note = note;
        Content = note.Content;
        Color = note.Color;
        AlwaysOnTop = note.AlwaysOnTop;
    }

    partial void OnContentChanged(string value)
    {
        Note.Content = value;
        Note.UpdatedAt = DateTime.UtcNow;

        // 触发 500ms 防抖静默保存
        _autoSaveCoordinator.ScheduleSave(Note, async n =>
        {
            await _repository.SaveAsync(n);
            // 发送正文变更轻量消息（不触发列表重排抖动）
            WeakReferenceMessenger.Default.Send(new NoteContentChangedMessage(n.Id, n.Content, n.UpdatedAt));
        });
    }

    [RelayCommand]
    public async Task TogglePinAsync()
    {
        AlwaysOnTop = !AlwaysOnTop;
        Note.AlwaysOnTop = AlwaysOnTop;
        Note.UpdatedAt = DateTime.UtcNow;
        await _repository.SaveAsync(Note);
        WeakReferenceMessenger.Default.Send(new NoteMetaChangedMessage(Note));
    }

    [RelayCommand]
    public async Task SetColorAsync(NoteColor newColor)
    {
        Color = newColor;
        Note.Color = newColor;
        Note.UpdatedAt = DateTime.UtcNow;
        await _repository.SaveAsync(Note);
        WeakReferenceMessenger.Default.Send(new NoteMetaChangedMessage(Note));
    }

    [RelayCommand]
    public async Task DeleteAsync()
    {
        // 1. 内存实体标记为已删除与已关闭
        Note.IsDeleted = true;
        Note.IsOpen = false;
        Note.UpdatedAt = DateTime.UtcNow;

        // 2. 取消防抖待写入队列中的旧数据，防止异步覆写
        _autoSaveCoordinator.CancelPendingSave(Note.Id);

        // 3. 执行数据库归档持久化与广播
        await _repository.ArchiveNoteAsync(Note.Id);
        WeakReferenceMessenger.Default.Send(new NoteArchivedMessage(Note.Id));
    }

    /// <summary>
    /// 立即强制刷盘当前便签
    /// </summary>
    public async Task FlushSaveAsync()
    {
        // 已归档/已删除便签禁止执行回写保存
        if (Note.IsDeleted) return;

        await _autoSaveCoordinator.FlushAsync(Note.Id);
        await _repository.SaveAsync(Note);
        // 必须广播持久化正文 Note.Content，禁止使用展示用的派生属性 Note.PreviewText：
        // PreviewText 在空白便签时会返回「（空白便签）」占位文案，且会 Trim 掉正文首尾空格与缩进，
        // 一旦广播出去会污染主列表内存实体（NotesListViewModel.HandleNoteContentChanged），
        // 再经「切换置顶」等全字段 SaveAsync 覆盖写回数据库，造成正文被永久污染。
        WeakReferenceMessenger.Default.Send(new NoteContentChangedMessage(Note.Id, Note.Content, Note.UpdatedAt));
    }
}
