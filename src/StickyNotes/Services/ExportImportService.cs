using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using StickyNotes.Data;
using StickyNotes.Infrastructure;
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

    /// <summary>
    /// 旧版（v1 备份）唯一的置顶标志，导出时写为「列表置顶」以保持向下兼容。
    /// 导入时**仅在 V2 两列缺失**的回退路径上被读取，禁止用它直接赋值给 <see cref="Note.IsPinned"/>。
    /// </summary>
    public bool IsPinned { get; set; }

    /// <summary>是否在便签列表中置顶（V2 拆列后必须独立往返，null = 备份未记录该字段）</summary>
    public bool? IsPinnedInList { get; set; }

    /// <summary>是否在桌面最顶层悬浮（null = 备份未记录该字段）</summary>
    public bool? AlwaysOnTop { get; set; }

    /// <summary>
    /// 导出时该便签是否处于打开状态（null = 旧备份未记录，导入时按既有行为回落为不还原窗口）
    /// </summary>
    public bool? IsOpen { get; set; }

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
/// 便签导入结果统计
/// </summary>
public sealed record ImportResult(int Total, int Imported, int Skipped);

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
        try
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
                    // 兼容字段：旧版备份只有 IsPinned，写为「列表置顶」，避免旧版本误把桌面置顶也一起打开
                    IsPinned = n.IsPinnedInList,
                    // V2 三态独立往返（原 F-P1-2 子问题 1、3）
                    IsPinnedInList = n.IsPinnedInList,
                    AlwaysOnTop = n.AlwaysOnTop,
                    IsOpen = n.IsOpen,
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
            AppLog.Info($"[ExportImportService] 成功导出 {package.TotalCount} 条便签至 {targetFilePath}");
            return package.TotalCount;
        }
        catch (Exception ex)
        {
            AppLog.Error($"[ExportImportService] 导出便签失败 (目标: {targetFilePath}): {ex.Message}", ex);
            throw;
        }
    }

    /// <summary>
    /// 从 JSON 文件解析并导入便签，支持时间戳冲突裁决（较新记录保留）与原子事务批量导入
    /// </summary>
    public async Task<int> ImportNotesAsync(string sourceFilePath, CancellationToken cancellationToken = default)
    {
        var result = await ImportNotesWithResultAsync(sourceFilePath, cancellationToken);
        return result.Imported;
    }

    /// <summary>
    /// 从 JSON 文件导入并返回详细统计结果（总数、导入数、因冲突跳过数）
    /// </summary>
    public async Task<ImportResult> ImportNotesWithResultAsync(string sourceFilePath, CancellationToken cancellationToken = default)
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
        catch (Exception ex)
        {
            // 降级尝试顶层数组反序列化（非静默：记录以便排查格式不符的备份文件）
            AppLog.Warn($"[ExportImportService] 备份文件不符合 NoteBackupPackage 容器格式，降级按顶层数组解析: {ex.Message}");
        }

        if (items == null)
        {
            items = JsonSerializer.Deserialize<List<NoteBackupItem>>(json, JsonOptions);
        }

        if (items == null || items.Count == 0)
        {
            AppLog.Warn($"[ExportImportService] 导入文件未解析出任何便签记录: {sourceFilePath}");
            return new ImportResult(0, 0, 0);
        }

        var toSave = new List<Note>();
        int skippedCount = 0;

        foreach (var item in items)
        {
            var noteId = item.Id == Guid.Empty ? Guid.NewGuid() : item.Id;

            // 冲突裁决：若本地已存在相同 ID 的便签，且本地修改时间不早于备份中的时间，则保留本地最新内容
            var existing = await _repository.GetByIdAsync(noteId, cancellationToken);
            if (existing != null && existing.UpdatedAt >= item.UpdatedAt)
            {
                skippedCount++;
                continue;
            }

            var note = new Note
            {
                Id = noteId,
                Content = item.Content ?? string.Empty,
                Color = item.Color,
                // 置顶：优先读 V2 独立字段；旧备份缺该字段时回落 IsPinned（回落到「列表置顶」，
                // 绝不再触发 Note.IsPinned 兼容 setter 的「一写两改」强行合并两种置顶语义）。原 F-P1-2 子问题 1。
                IsPinnedInList = item.IsPinnedInList ?? item.IsPinned,
                // 旧备份只记录了一个合并语义的 IsPinned：一律回落到「列表置顶」，
                // 绝不顺带打开桌面置顶（否则旧备份会把桌面窗口全部弹开）。
                AlwaysOnTop = item.AlwaysOnTop ?? false,
                IsDeleted = item.IsDeleted,
                // 虚拟桌面坐标允许为负（主屏左侧的副屏是合法位置，P1-5），
                // 仅对 NaN/非有限值回落默认坐标，不得用 > 0 判定合法性
                WindowX = double.IsFinite(item.WindowX) ? item.WindowX : 150,
                WindowY = double.IsFinite(item.WindowY) ? item.WindowY : 150,
                WindowWidth = item.WindowWidth >= 280 ? item.WindowWidth : 380,
                WindowHeight = item.WindowHeight >= 240 ? item.WindowHeight : 420,
                // 旧备份未记录 IsOpen 时保持既有行为（导入后不自动弹出窗口），仅在备份明确记录时还原
                IsOpen = item.IsOpen ?? false,
                CreatedAt = item.CreatedAt != default ? item.CreatedAt : DateTime.UtcNow,
                UpdatedAt = item.UpdatedAt != default ? item.UpdatedAt : DateTime.UtcNow
            };

            toSave.Add(note);
        }

        if (toSave.Count > 0)
        {
            await _repository.SaveBatchAsync(toSave, cancellationToken);
        }

        AppLog.Info($"[ExportImportService] 导入完成：总计 {items.Count} 条，成功导入/更新 {toSave.Count} 条，因本地较新跳过 {skippedCount} 条");
        return new ImportResult(items.Count, toSave.Count, skippedCount);
    }
}
