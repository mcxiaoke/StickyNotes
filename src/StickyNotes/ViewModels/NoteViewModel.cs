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

    [ObservableProperty]
    private Note _note = new();

    [ObservableProperty]
    private string _content = string.Empty;

    [ObservableProperty]
    private NoteColor _color = NoteColor.Yellow;

    [ObservableProperty]
    private bool _isPinned;

    public NoteViewModel(INoteRepository repository, AutoSaveCoordinator autoSaveCoordinator)
    {
        _repository = repository;
        _autoSaveCoordinator = autoSaveCoordinator;
    }

    public void Initialize(Note note)
    {
        Note = note;
        Content = note.Content;
        Color = note.Color;
        IsPinned = note.IsPinned;
    }

    partial void OnContentChanged(string value)
    {
        Note.Content = value;
        Note.UpdatedAt = DateTime.UtcNow;

        // 触发 500ms 防抖静默保存
        _autoSaveCoordinator.ScheduleSave(Note, async n =>
        {
            await _repository.SaveAsync(n);
            WeakReferenceMessenger.Default.Send(new NoteUpdatedMessage(n));
        });
    }

    [RelayCommand]
    public async Task TogglePinAsync()
    {
        IsPinned = !IsPinned;
        Note.IsPinned = IsPinned;
        Note.UpdatedAt = DateTime.UtcNow;
        await _repository.SaveAsync(Note);
        WeakReferenceMessenger.Default.Send(new NoteUpdatedMessage(Note));
    }

    [RelayCommand]
    public async Task SetColorAsync(NoteColor newColor)
    {
        Color = newColor;
        Note.Color = newColor;
        Note.UpdatedAt = DateTime.UtcNow;
        await _repository.SaveAsync(Note);
        WeakReferenceMessenger.Default.Send(new NoteUpdatedMessage(Note));
    }

    [RelayCommand]
    public async Task DeleteAsync()
    {
        await _autoSaveCoordinator.FlushAsync(Note.Id);
        await _repository.SoftDeleteAsync(Note.Id);
        WeakReferenceMessenger.Default.Send(new NoteDeletedMessage(Note.Id));
    }

    /// <summary>
    /// 立即强制刷盘当前便签
    /// </summary>
    public async Task FlushSaveAsync()
    {
        await _autoSaveCoordinator.FlushAsync(Note.Id);
        await _repository.SaveAsync(Note);
        WeakReferenceMessenger.Default.Send(new NoteUpdatedMessage(Note));
    }
}
