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

/// <summary>
/// 便签已归档消息
/// </summary>
public sealed class NoteArchivedMessage : ValueChangedMessage<Guid>
{
    public NoteArchivedMessage(Guid noteId) : base(noteId) { }
}

/// <summary>
/// 便签已从归档恢复消息
/// </summary>
public sealed class NoteRestoredMessage : ValueChangedMessage<Note>
{
    public NoteRestoredMessage(Note value) : base(value) { }
}

/// <summary>
/// 便签编辑器字体大小变更消息
/// </summary>
public sealed class FontSizeChangedMessage : ValueChangedMessage<double>
{
    public FontSizeChangedMessage(double newFontSize) : base(newFontSize) { }
}

