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

    /// <summary>
    /// 关闭主窗口时最小化到系统托盘
    /// </summary>
    public bool MinimizeToTrayOnClose { get; set; } = true;

    /// <summary>
    /// 启用全局快捷键（Win+Alt+N, Win+Alt+H）
    /// </summary>
    public bool EnableGlobalHotKeys { get; set; } = true;

    /// <summary>
    /// 开机自启时最小化到托盘
    /// </summary>
    public bool StartMinimized { get; set; } = true;

    /// <summary>
    /// 是否启用 PIN 锁定（防偷窥轻量保护，实际有效性以 PinHash/PinSalt 存在为准）
    /// </summary>
    public bool PinEnabled { get; set; }

    /// <summary>
    /// PIN 哈希盐（base64，16 字节随机值），为空表示未设置过 PIN
    /// </summary>
    public string PinSalt { get; set; } = string.Empty;

    /// <summary>
    /// PIN 的 PBKDF2-SHA256 哈希（base64），不存明文
    /// </summary>
    public string PinHash { get; set; } = string.Empty;

    /// <summary>
    /// 列表窗口不在前台 N 分钟后自动关闭（0 表示不启用），下次打开时重新锁定
    /// </summary>
    public int ListAutoCloseMinutes { get; set; } = 10;
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

    public bool MinimizeToTrayOnClose
    {
        get => Settings.MinimizeToTrayOnClose;
        set
        {
            if (Settings.MinimizeToTrayOnClose != value)
            {
                Settings.MinimizeToTrayOnClose = value;
                SaveSettings();
            }
        }
    }

    public bool EnableGlobalHotKeys
    {
        get => Settings.EnableGlobalHotKeys;
        set
        {
            if (Settings.EnableGlobalHotKeys != value)
            {
                Settings.EnableGlobalHotKeys = value;
                SaveSettings();
            }
        }
    }

    public bool StartMinimized
    {
        get => Settings.StartMinimized;
        set
        {
            if (Settings.StartMinimized != value)
            {
                Settings.StartMinimized = value;
                SaveSettings();
            }
        }
    }

    /// <summary>
    /// 列表窗口不在前台自动关闭的分钟数（0 = 不启用）
    /// </summary>
    public int ListAutoCloseMinutes
    {
        get => Settings.ListAutoCloseMinutes;
        set
        {
            var clamped = Math.Clamp(value, 0, 120);
            if (Settings.ListAutoCloseMinutes != clamped)
            {
                Settings.ListAutoCloseMinutes = clamped;
                SaveSettings();
            }
        }
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
            AppLog.Warn($"[SettingsService] 保存设置失败: {ex.Message}", ex);
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
            AppLog.Warn($"[SettingsService] 加载设置失败: {ex.Message}", ex);
        }

        return new AppSettings();
    }
}
