using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using StickyNotes.Data;
using StickyNotes.Messages;
using StickyNotes.Models;
using StickyNotes.Services;

namespace StickyNotes.ViewModels;

/// <summary>
/// 便签管理中心主窗口 ViewModel
/// </summary>
public partial class NotesListViewModel : ObservableObject
{
    private readonly INoteRepository _repository;
    private readonly ISearchService _searchService;
    private readonly WindowManager _windowManager;
    private CancellationTokenSource? _searchCts;

    [ObservableProperty]
    private ObservableCollection<Note> _notes = new();

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private ObservableCollection<SearchHit> _searchResults = new();

    [ObservableProperty]
    private bool _isSearching;

    [ObservableProperty]
    private int _searchHitCount;

    public NotesListViewModel(
        INoteRepository repository,
        ISearchService searchService,
        WindowManager windowManager)
    {
        _repository = repository;
        _searchService = searchService;
        _windowManager = windowManager;

        // 注册消息总线监听
        WeakReferenceMessenger.Default.Register<NoteUpdatedMessage>(this, (_, msg) => HandleNoteUpdated(msg.Value));
        WeakReferenceMessenger.Default.Register<NoteDeletedMessage>(this, (_, msg) => HandleNoteDeleted(msg.Value));
        WeakReferenceMessenger.Default.Register<NoteCreatedMessage>(this, (_, msg) => HandleNoteCreated(msg.Value));
    }

    /// <summary>
    /// 加载所有未删除的活动便签
    /// </summary>
    public async Task LoadNotesAsync()
    {
        var list = await _repository.GetAllActiveAsync();
        Notes.Clear();
        foreach (var note in list)
        {
            Notes.Add(note);
        }
    }

    partial void OnSearchTextChanged(string value)
    {
        _searchCts?.Cancel();
        _searchCts?.Dispose();

        if (string.IsNullOrWhiteSpace(value))
        {
            IsSearching = false;
            SearchResults.Clear();
            SearchHitCount = 0;
            return;
        }

        IsSearching = true;
        _searchCts = new CancellationTokenSource();
        var token = _searchCts.Token;

        // 300ms 防抖即输即搜
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(300, token);
                if (!token.IsCancellationRequested)
                {
                    var hits = _searchService.Search(Notes, value);
                    System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                    {
                        SearchResults.Clear();
                        foreach (var hit in hits)
                        {
                            SearchResults.Add(hit);
                        }
                        SearchHitCount = hits.Count;
                    });
                }
            }
            catch (OperationCanceledException)
            {
            }
        }, token);
    }

    [RelayCommand]
    public async Task NewNoteAsync()
    {
        var newNote = new Note
        {
            Content = string.Empty,
            Color = NoteColor.Yellow,
            WindowX = 200 + (Notes.Count % 5) * 30,
            WindowY = 150 + (Notes.Count % 5) * 30
        };

        await _repository.SaveAsync(newNote);
        Notes.Insert(0, newNote);

        // 立即唤起独立贴纸窗口并聚焦
        _windowManager.OpenOrActivateNote(newNote);
    }

    [RelayCommand]
    public void OpenNote(Note note)
    {
        _windowManager.OpenOrActivateNote(note);
    }

    [RelayCommand]
    public async Task DeleteNoteAsync(Note note)
    {
        _windowManager.CloseNoteWindow(note.Id);
        await _repository.SoftDeleteAsync(note.Id);
        Notes.Remove(note);

        // 重新同步搜索状态
        if (IsSearching)
        {
            OnSearchTextChanged(SearchText);
        }
    }

    [RelayCommand]
    public void SelectSearchHit(SearchHit hit)
    {
        _windowManager.NavigateToHit(hit);
    }

    private void HandleNoteUpdated(Note updated)
    {
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            var idx = Notes.ToList().FindIndex(n => n.Id == updated.Id);
            if (idx >= 0)
            {
                Notes.RemoveAt(idx);
                // 重新按置顶和修改时间排入合适位置
                int insertPos = 0;
                while (insertPos < Notes.Count)
                {
                    var cur = Notes[insertPos];
                    if (updated.IsPinned && !cur.IsPinned) break;
                    if (updated.IsPinned == cur.IsPinned && updated.UpdatedAt >= cur.UpdatedAt) break;
                    insertPos++;
                }
                Notes.Insert(insertPos, updated);
            }

            if (IsSearching)
            {
                OnSearchTextChanged(SearchText);
            }
        });
    }

    private void HandleNoteDeleted(Guid id)
    {
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            _windowManager.CloseNoteWindow(id);
            var note = Notes.FirstOrDefault(n => n.Id == id);
            if (note != null)
            {
                Notes.Remove(note);
            }
            if (IsSearching)
            {
                OnSearchTextChanged(SearchText);
            }
        });
    }

    private void HandleNoteCreated(Note note)
    {
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            Notes.Insert(0, note);
        });
    }
}
