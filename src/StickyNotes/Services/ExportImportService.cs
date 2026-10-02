using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using StickyNotes.Data;
using StickyNotes.Models;

namespace StickyNotes.Services;

/// <summary>
/// 便签导出备份项 DTO
/// </summary>
public sealed class NoteBackupItem
{
    public Guid Id { get; set; }
    public string Content { get; set; } = string.Empty;
    public NoteColor Color { get; set; } = NoteColor.Yellow;
    public bool IsPinned { get; set; }
    public bool IsDeleted { get; set; }
    public double WindowX { get; set; } = 150;
    public double WindowY { get; set; } = 150;
    public double WindowWidth { get; set; } = 380;
    public double WindowHeight { get; set; } = 420;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// 便签备份归档包容器
/// </summary>
public sealed class NoteBackupPackage
{
    public string App { get; set; } = "StickyNotes";
    public string Version { get; set; } = "1.0";
    public DateTime ExportedAt { get; set; } = DateTime.UtcNow;
    public int TotalCount { get; set; }
    public List<NoteBackupItem> Notes { get; set; } = new();
}

/// <summary>
/// 便签 JSON 导入与导出服务
/// </summary>
public sealed class ExportImportService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly INoteRepository _repository;

    public ExportImportService(INoteRepository repository)
    {
        _repository = repository;
    }

    /// <summary>
    /// 将所有便签（包含活动与已归档）完整导出为 JSON 文件
    /// </summary>
    public async Task<int> ExportNotesAsync(string targetFilePath, CancellationToken cancellationToken = default)
    {
        var allNotes = await _repository.GetAllAsync(cancellationToken);
        var package = new NoteBackupPackage
        {
            App = "StickyNotes",
            Version = "1.0",
            ExportedAt = DateTime.UtcNow,
            TotalCount = allNotes.Count,
            Notes = allNotes.Select(n => new NoteBackupItem
            {
                Id = n.Id,
                Content = n.Content,
                Color = n.Color,
                IsPinned = n.IsPinned,
                IsDeleted = n.IsDeleted,
                WindowX = n.WindowX,
                WindowY = n.WindowY,
                WindowWidth = n.WindowWidth,
                WindowHeight = n.WindowHeight,
                CreatedAt = n.CreatedAt,
                UpdatedAt = n.UpdatedAt
            }).ToList()
        };

        var json = JsonSerializer.Serialize(package, JsonOptions);
        await File.WriteAllTextAsync(targetFilePath, json, cancellationToken);
        return package.TotalCount;
    }

    /// <summary>
    /// 从 JSON 文件解析并导入便签，支持增量合并与覆盖更新
    /// </summary>
    public async Task<int> ImportNotesAsync(string sourceFilePath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(sourceFilePath))
            throw new FileNotFoundException("导入的备份文件不存在", sourceFilePath);

        var json = await File.ReadAllTextAsync(sourceFilePath, cancellationToken);

        List<NoteBackupItem>? items = null;

        // 优先尝试标准 NoteBackupPackage 容器反序列化
        try
        {
            var package = JsonSerializer.Deserialize<NoteBackupPackage>(json, JsonOptions);
            if (package?.Notes != null && package.Notes.Count > 0)
            {
                items = package.Notes;
            }
        }
        catch
        {
            // 降级尝试顶层数组反序列化
        }

        if (items == null)
        {
            items = JsonSerializer.Deserialize<List<NoteBackupItem>>(json, JsonOptions);
        }

        if (items == null || items.Count == 0)
            return 0;

        int importedCount = 0;
        foreach (var item in items)
        {
            var note = new Note
            {
                Id = item.Id == Guid.Empty ? Guid.NewGuid() : item.Id,
                Content = item.Content ?? string.Empty,
                Color = item.Color,
                IsPinned = item.IsPinned,
                IsDeleted = item.IsDeleted,
                WindowX = item.WindowX > 0 ? item.WindowX : 150,
                WindowY = item.WindowY > 0 ? item.WindowY : 150,
                WindowWidth = item.WindowWidth >= 280 ? item.WindowWidth : 380,
                WindowHeight = item.WindowHeight >= 240 ? item.WindowHeight : 420,
                IsOpen = false,
                CreatedAt = item.CreatedAt != default ? item.CreatedAt : DateTime.UtcNow,
                UpdatedAt = item.UpdatedAt != default ? item.UpdatedAt : DateTime.UtcNow
            };

            await _repository.SaveAsync(note, cancellationToken);
            importedCount++;
        }

        return importedCount;
    }
}
