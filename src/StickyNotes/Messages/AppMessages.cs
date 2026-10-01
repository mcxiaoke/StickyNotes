using CommunityToolkit.Mvvm.Messaging.Messages;
using StickyNotes.Models;

namespace StickyNotes.Messages;

/// <summary>
/// 便签更新消息（正文、颜色、置顶变更）
/// </summary>
public sealed class NoteUpdatedMessage : ValueChangedMessage<Note>
{
    public NoteUpdatedMessage(Note value) : base(value) { }
}

/// <summary>
/// 便签删除消息
/// </summary>
public sealed class NoteDeletedMessage : ValueChangedMessage<Guid>
{
    public NoteDeletedMessage(Guid noteId) : base(noteId) { }
}

/// <summary>
/// 新便签创建消息
/// </summary>
public sealed class NoteCreatedMessage : ValueChangedMessage<Note>
{
    public NoteCreatedMessage(Note value) : base(value) { }
}
