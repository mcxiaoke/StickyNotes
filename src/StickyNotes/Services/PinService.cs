using System.Security.Cryptography;
using StickyNotes.Infrastructure;

namespace StickyNotes.Services;

/// <summary>
/// PIN 锁定服务（防偷窥轻量保护）
/// 仅提供设置/验证/清除能力，不管理会话解锁状态；锁定呈现由窗口内遮罩控件负责。
/// PIN 不存明文：PBKDF2-SHA256 + 随机盐后写入 settings.json。
/// </summary>
public sealed class PinService
{
    /// <summary>PBKDF2 迭代次数（万级即可，验证耗时毫秒级，符合轻量定位）</summary>
    private const int Pbkdf2Iterations = 20_000;
    private const int SaltSizeBytes = 16;
    private const int HashSizeBytes = 32;

    /// <summary>PIN 最小长度</summary>
    public const int MinPinLength = 4;

    /// <summary>PIN 最大长度</summary>
    public const int MaxPinLength = 20;

    private readonly SettingsService _settingsService;

    public PinService(SettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    /// <summary>是否已设置过 PIN（存在盐与哈希）</summary>
    public bool IsPinSet =>
        !string.IsNullOrEmpty(_settingsService.Settings.PinHash) &&
        !string.IsNullOrEmpty(_settingsService.Settings.PinSalt);

    /// <summary>PIN 锁定是否生效（开关打开且确实存在有效 PIN 数据）</summary>
    public bool IsPinEnabled => _settingsService.Settings.PinEnabled && IsPinSet;

    /// <summary>PIN 合法性：4~20 位，内容不限</summary>
    public static bool IsValidPin(string? pin) =>
        !string.IsNullOrEmpty(pin) && pin.Length >= MinPinLength && pin.Length <= MaxPinLength;

    /// <summary>
    /// 设置（或修改）PIN：校验长度 → 生成随机盐 → 哈希入库并自动启用锁定
    /// </summary>
    public bool SetPin(string pin)
    {
        if (!IsValidPin(pin)) return false;

        var salt = RandomNumberGenerator.GetBytes(SaltSizeBytes);
        _settingsService.Settings.PinSalt = Convert.ToBase64String(salt);
        _settingsService.Settings.PinHash = Convert.ToBase64String(Hash(pin, salt));
        _settingsService.Settings.PinEnabled = true;
        _settingsService.SaveSettings();
        return true;
    }

    /// <summary>
    /// 验证 PIN（恒定时间比较，避免时序侧信道）
    /// </summary>
    public bool VerifyPin(string? pin)
    {
        if (!IsPinSet || string.IsNullOrEmpty(pin)) return false;

        try
        {
            var salt = Convert.FromBase64String(_settingsService.Settings.PinSalt);
            var expected = Convert.FromBase64String(_settingsService.Settings.PinHash);
            var actual = Hash(pin, salt);
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch (FormatException)
        {
            // settings.json 中盐/哈希字段被破坏时视为验证失败
            return false;
        }
    }

    /// <summary>
    /// 清除 PIN（需先通过当前 PIN 验证），清除后锁定自动失效
    /// </summary>
    public bool ClearPin(string currentPin)
    {
        if (!VerifyPin(currentPin)) return false;

        _settingsService.Settings.PinEnabled = false;
        _settingsService.Settings.PinSalt = string.Empty;
        _settingsService.Settings.PinHash = string.Empty;
        _settingsService.SaveSettings();
        return true;
    }

    private static byte[] Hash(string pin, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(pin, salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, HashSizeBytes);
}
