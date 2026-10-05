namespace StickyNotes.Sync;

/// <summary>
/// 端到端防偷窥加密保险箱专属内置密钥提供器。
/// <para>
/// 采用分部类模式：公开仓库仅包含此文件（含开源公共回落占位），
/// 开发者本地私密专属密钥定义在已加入 .gitignore 的 <c>VaultSecret.local.cs</c> 中，
/// 编译时自动合入程序集，避免开源时暴露专属密钥。
/// </para>
/// </summary>
public static partial class VaultSecret
{
    /// <summary>
    /// 本地私密分部方法：若本地存在 VaultSecret.local.cs 则由其注入专属密钥。
    /// </summary>
    static partial void ResolveCustomSecret(ref string? secret);

    /// <summary>
    /// 获取当前生效的保险箱加密密钥（优先返回本地注入的专属密钥，未配置则使用默认回落值）。
    /// </summary>
    public static string GetSecret()
    {
        string? custom = null;
        ResolveCustomSecret(ref custom);
        return custom ?? "StickyNotes-Vault-Public-Fallback-Key#2026";
    }
}
