using System;
using System.IO;
using Microsoft.Win32;
using StickyNotes.Infrastructure;

namespace StickyNotes.Services;

/// <summary>
/// Windows 开机自启动注册表管理服务
/// </summary>
public class AutoStartService
{
    private const string RunRegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "StickyNotes";

    /// <summary>
    /// 当前是否已配置开机自启动
    /// </summary>
    public bool IsAutoStartEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunRegistryKey, false);
                var value = key?.GetValue(AppName) as string;
                return !string.IsNullOrWhiteSpace(value);
            }
            catch (Exception ex)
            {
                AppLog.Error($"[AutoStartService] 读取开机自启动配置失败: {ex.Message}", ex);
                return false;
            }
        }
    }

    /// <summary>
    /// 设置开机自启动开关
    /// </summary>
    public bool SetAutoStart(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunRegistryKey, true);
            if (key == null)
            {
                AppLog.Warn("[AutoStartService] 无法打开注册表 Run 键");
                return false;
            }

            if (enable)
            {
                var exePath = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
                {
                    AppLog.Warn("[AutoStartService] 未获取到有效主程序路径，跳过自启动写入");
                    return false;
                }

                // 启动参数加上 --autostart
                var cmd = $"\"{exePath}\" --autostart";
                key.SetValue(AppName, cmd, RegistryValueKind.String);
                AppLog.Info($"[AutoStartService] 已启用开机自启: {cmd}");
            }
            else
            {
                if (key.GetValue(AppName) != null)
                {
                    key.DeleteValue(AppName, false);
                    AppLog.Info("[AutoStartService] 已禁用开机自启");
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error($"[AutoStartService] 设置开机自启动异常: {ex.Message}", ex);
            return false;
        }
    }
}
