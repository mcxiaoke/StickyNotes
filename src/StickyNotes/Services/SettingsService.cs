using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.Messaging;
using StickyNotes.Infrastructure;
using StickyNotes.Messages;

namespace StickyNotes.Services;

/// <summary>
/// 应用程序全局配置实体
/// </summary>
public sealed class AppSettings
{
    /// <summary>
    /// 便签贴纸正文字体大小（pt）
    /// </summary>
    public double EditorFontSize { get; set; } = 14.0;
}

/// <summary>
/// 应用程序配置与设置服务
/// </summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _settingsPath;

    public AppSettings Settings { get; private set; }

    public double EditorFontSize
    {
        get => Settings.EditorFontSize;
        set => SetEditorFontSize(value);
    }

    public SettingsService(string? customSettingsPath = null)
    {
        _settingsPath = customSettingsPath ?? AppPaths.SettingsPath;
        Settings = LoadSettings(_settingsPath);
    }

    public void SetEditorFontSize(double newFontSize)
    {
        // 限制在安全有效字号区间 10 ~ 36 pt
        newFontSize = Math.Clamp(newFontSize, 10.0, 36.0);
        if (Math.Abs(Settings.EditorFontSize - newFontSize) > 0.1)
        {
            Settings.EditorFontSize = newFontSize;
            SaveSettings();
            WeakReferenceMessenger.Default.Send(new FontSizeChangedMessage(newFontSize));
        }
    }

    public void SaveSettings(string? customSettingsPath = null)
    {
        try
        {
            var targetPath = customSettingsPath ?? _settingsPath;
            var json = JsonSerializer.Serialize(Settings, JsonOptions);
            File.WriteAllText(targetPath, json);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SettingsService] 保存设置失败: {ex.Message}");
        }
    }


    private static AppSettings LoadSettings(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json);
                if (loaded != null)
                {
                    loaded.EditorFontSize = Math.Clamp(loaded.EditorFontSize, 10.0, 36.0);
                    return loaded;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SettingsService] 加载设置失败: {ex.Message}");
        }

        return new AppSettings();
    }
}
