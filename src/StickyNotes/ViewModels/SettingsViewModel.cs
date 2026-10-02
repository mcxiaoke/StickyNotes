using System.Diagnostics;
using System.Reflection;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Win32;
using StickyNotes.Infrastructure;
using StickyNotes.Messages;
using StickyNotes.Services;

namespace StickyNotes.ViewModels;

public record FontSizeOption(string Label, double Size);

/// <summary>
/// 应用程序独立设置窗口 ViewModel
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _settingsService;
    private readonly ExportImportService _exportImportService;

    public IReadOnlyList<FontSizeOption> FontSizeOptions { get; } = new List<FontSizeOption>
    {
        new("小 (12 pt)", 12.0),
        new("标准 (14 pt - 默认)", 14.0),
        new("中 (16 pt)", 16.0),
        new("大 (18 pt)", 18.0),
        new("特大 (20 pt)", 20.0),
        new("超大 (24 pt)", 24.0)
    };

    [ObservableProperty]
    private double _selectedFontSize;

    public string PreviewText => "这是一条便签示例文本：支持纯文本与多行输入，清晰易读 (StickyNotes 123 ABC)";

    public string AppVersion { get; }
    public string GitCommit { get; }
    public string BuildTime { get; }
    public string RuntimeInfo { get; }
    public string DataDirectoryPath => AppPaths.DataDirectory;

    public SettingsViewModel(SettingsService settingsService, ExportImportService exportImportService)
    {
        _settingsService = settingsService;
        _exportImportService = exportImportService;

        _selectedFontSize = _settingsService.EditorFontSize;

        // 获取程序集构建与版本信息
        var assembly = Assembly.GetExecutingAssembly();
        var infoVer = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        AppVersion = !string.IsNullOrWhiteSpace(infoVer) ? infoVer : (assembly.GetName().Version?.ToString(3) ?? "1.0.0");

        var metadata = assembly.GetCustomAttributes<AssemblyMetadataAttribute>().ToDictionary(m => m.Key, m => m.Value);
        GitCommit = metadata.TryGetValue("GitCommit", out var commit) && !string.IsNullOrWhiteSpace(commit) ? commit : "dev";
        BuildTime = metadata.TryGetValue("BuildTime", out var time) && !string.IsNullOrWhiteSpace(time) ? time : DateTime.Now.ToString("yyyy-MM-dd");

        RuntimeInfo = $".NET 8.0 ({System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture})";
    }

    partial void OnSelectedFontSizeChanged(double value)
    {
        _settingsService.SetEditorFontSize(value);
    }

    [RelayCommand]
    public async Task ExportJsonAsync()
    {
        var dialog = new SaveFileDialog
        {
            Title = "导出所有便签备份",
            Filter = "JSON 文件 (*.json)|*.json",
            FileName = $"StickyNotes_Backup_{DateTime.Now:yyyyMMdd_HHmmss}.json"
        };

        if (dialog.ShowDialog() == true)
        {
            try
            {
                int count = await _exportImportService.ExportNotesAsync(dialog.FileName);
                MessageBox.Show(
                    $"导出成功！\n共导出 {count} 条便签至文件：\n{dialog.FileName}",
                    "便签导出完成",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information
                );
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"导出失败: {ex.Message}",
                    "导出错误",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error
                );
            }
        }
    }

    [RelayCommand]
    public async Task ImportJsonAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "导入便签备份文件",
            Filter = "JSON 文件 (*.json)|*.json"
        };

        if (dialog.ShowDialog() == true)
        {
            try
            {
                int count = await _exportImportService.ImportNotesAsync(dialog.FileName);
                MessageBox.Show(
                    $"导入完成！\n成功导入/合并了 {count} 条便签。",
                    "便签导入完成",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information
                );

                // 通知主管理窗口重新拉取全量便签
                WeakReferenceMessenger.Default.Send(new NoteCreatedMessage(new Models.Note()));
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"导入失败: {ex.Message}",
                    "导入错误",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error
                );
            }
        }
    }

    [RelayCommand]
    public void OpenDataFolder()
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", AppPaths.DataDirectory)
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"打开目录失败: {ex.Message}", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
