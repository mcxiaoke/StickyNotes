using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using StickyNotes.Data;
using StickyNotes.Messages;
using StickyNotes.Models;

namespace StickyNotes.ViewModels;

/// <summary>
/// 已归档便签管理窗口 ViewModel
/// </summary>
public partial class ArchivedNotesViewModel : ObservableObject
{
    private readonly INoteRepository _repository;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilteredNotes))]
    [NotifyPropertyChangedFor(nameof(ArchivedCount))]
    [NotifyPropertyChangedFor(nameof(HasNoNotes))]
    [NotifyPropertyChangedFor(nameof(CanClearArchived))]
    private ObservableCollection<Note> _archivedNotes = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilteredNotes))]
    private string _searchText = string.Empty;

    public IEnumerable<Note> FilteredNotes
    {
        get
        {
            if (string.IsNullOrWhiteSpace(SearchText))
                return ArchivedNotes;

            var trimmed = SearchText.Trim();
            var tokens = trimmed.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0) return ArchivedNotes;
            var compact = string.Concat(tokens);

            return ArchivedNotes.Where(n =>
            {
                var content = n.Content ?? string.Empty;
                var title = n.DisplayTitle ?? string.Empty;

                if (content.Contains(trimmed, StringComparison.OrdinalIgnoreCase) ||
                    title.Contains(trimmed, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (compact.Length > 0 &&
                    (content.Contains(compact, StringComparison.OrdinalIgnoreCase) ||
                     title.Contains(compact, StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }

                return tokens.All(t =>
                    content.Contains(t, StringComparison.OrdinalIgnoreCase) ||
                    title.Contains(t, StringComparison.OrdinalIgnoreCase));
            });
        }
    }

    public int ArchivedCount => ArchivedNotes.Count;

    public bool HasNoNotes => ArchivedNotes.Count == 0;

    public bool CanClearArchived => ArchivedNotes.Count > 0;

    public ArchivedNotesViewModel(INoteRepository repository)
    {
        _repository = repository;

        // 监听便签归档消息，若归档窗口开启中，自动拉取新归档
        WeakReferenceMessenger.Default.Register<NoteArchivedMessage>(this, async (_, _) =>
        {
            await LoadArchivedNotesAsync();
        });
    }

    /// <summary>
    /// 加载所有已归档的便签
    /// </summary>
    public async Task LoadArchivedNotesAsync()
    {
        var list = await _repository.GetAllArchivedAsync();
        ArchivedNotes.Clear();
        foreach (var note in list)
        {
            ArchivedNotes.Add(note);
        }

        OnPropertyChanged(nameof(FilteredNotes));
        OnPropertyChanged(nameof(ArchivedCount));
        OnPropertyChanged(nameof(HasNoNotes));
    }

    partial void OnSearchTextChanged(string value)
    {
        OnPropertyChanged(nameof(FilteredNotes));
    }

    /// <summary>
    /// 恢复便签回主管理列表
    /// </summary>
    [RelayCommand]
    public async Task RestoreNoteAsync(Note note)
    {
        await _repository.RestoreNoteAsync(note.Id);
        note.IsDeleted = false;
        ArchivedNotes.Remove(note);

        OnPropertyChanged(nameof(FilteredNotes));
        OnPropertyChanged(nameof(ArchivedCount));
        OnPropertyChanged(nameof(HasNoNotes));

        // 广播恢复消息，主列表自动重新收纳
        WeakReferenceMessenger.Default.Send(new NoteRestoredMessage(note));
    }

    /// <summary>
    /// 彻底永久删除指定便签
    /// </summary>
    [RelayCommand]
    public async Task HardDeleteNoteAsync(Note note)
    {
        await _repository.HardDeleteAsync(note.Id);
        ArchivedNotes.Remove(note);

        OnPropertyChanged(nameof(FilteredNotes));
        OnPropertyChanged(nameof(ArchivedCount));
        OnPropertyChanged(nameof(HasNoNotes));
    }

    /// <summary>
    /// 清空所有已归档便签
    /// </summary>
    [RelayCommand]
    public async Task ClearAllArchivedAsync()
    {
        await _repository.ClearAllArchivedAsync();
        ArchivedNotes.Clear();

        OnPropertyChanged(nameof(FilteredNotes));
        OnPropertyChanged(nameof(ArchivedCount));
        OnPropertyChanged(nameof(HasNoNotes));
    }
}
