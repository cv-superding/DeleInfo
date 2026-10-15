using DeleInfo.Core.Model;
using DeleInfo.Core.Native;

namespace DeleInfo.Core.Cleaners;

/// <summary>由清理器自己负责删除的非文件系统目标。</summary>
public interface ISelfRemovingCleaner
{
    bool TryRemove(CleanTarget target, out string? error);
}

/// <summary>
/// Windows 凭据管理器条目：git/gh 令牌、映射驱动器、各类 App 存的账号。
///
/// 两条硬约束：
/// 1. 删除不可还原（不转存密文是有意为之），报告必须如实标注。
/// 2. 系统级凭据一律不列进来——误删 MicrosoftAccount / OneDrive / LIVESSO 会把使用者
///    自己的登录会话打断，这不是"清理别人留下的痕迹"，而是自伤。
/// </summary>
public sealed class WindowsCredentialCleaner : ICleaner, ISelfRemovingCleaner
{
    public string Id => "win.credman";
    public string DisplayName => "Windows 凭据管理器";
    public TraceLevel Level => TraceLevel.Credential;

    /// <summary>
    /// 硬拒绝名单。本机实测存在的三条都属于系统登录会话，删掉会让使用者自己被踢下线：
    ///   MicrosoftAccount:target=SSO_POP_Device
    ///   WindowsLive:target=virtualapp/didlogical
    ///   LegacyGeneric:target=&lt;随机名&gt;（同一条虚拟应用容器凭据的另一种视图）
    /// </summary>
    private static readonly string[] ProtectedPrefixes =
    {
        "MicrosoftAccount", "OneDrive", "LIVESSO:", "Primary Refresh Token",
        "Primary Identity Provider", "windows:", "WindowsLive:", "LegacyGeneric:",
        "TPM:", "{DF1C4D60-", "SMB CLIP",
    };

    public static bool IsProtected(string targetName) =>
        ProtectedPrefixes.Any(p => targetName.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    public IEnumerable<CleanTarget> Discover(ScanOptions options)
    {
        if (options.MaxLevel < TraceLevel.Credential)
            yield break;
        if (!options.MatchesApp(DisplayName) && !options.MatchesApp("credman"))
            yield break;

        foreach (var e in CredApi.Enumerate())
        {
            if (IsProtected(e.TargetName) || IsWindowsLiveContainer(e))
                continue;

            var low = e.Type != CredApi.CRED_TYPE_GENERIC || IsSystemOwned(e);
            if (low && !options.IncludeLowConfidence)
                continue;

            yield return new CleanTarget
            {
                CleanerId = Id,
                App = "Windows 凭据管理器",
                Profile = $"{e.TypeLabel} / {e.PersistLabel}",
                Item = $"{e.TargetName}{(string.IsNullOrEmpty(e.UserName) ? "" : $"（{e.UserName}）")}",
                Path = $"credman://{e.TargetName}",
                Tag = e.Type.ToString(),
                Level = TraceLevel.Credential,
                Kind = TargetKind.External,
                NeedsSnapshot = false,
                LowConfidence = low,
            };
        }
    }

    /// <summary>同一容器凭据在部分枚举视图里只剩随机名，靠 Comment 里的命名空间兜底识别。</summary>
    private static bool IsWindowsLiveContainer(CredApi.Entry e) =>
        e.Comment?.Contains("Microsoft_WindowsLive", StringComparison.OrdinalIgnoreCase) == true ||
        e.TargetName.Contains("virtualapp", StringComparison.OrdinalIgnoreCase);

    /// <summary>看起来属于系统/网络登录而非"别人留下的网站账号"，默认不勾选。</summary>
    private static bool IsSystemOwned(CredApi.Entry e) =>
        e.TargetName.StartsWith("Domain:", StringComparison.OrdinalIgnoreCase) ||
        e.TargetName.Contains("Enterprise", StringComparison.OrdinalIgnoreCase);

    public bool TryRemove(CleanTarget target, out string? error)
    {
        var name = target.Path.Length > "credman://".Length ? target.Path["credman://".Length..] : target.Path;
        var type = int.TryParse(target.Tag, out var t) ? t : CredApi.CRED_TYPE_GENERIC;
        return CredApi.Delete(name, type, out error);
    }
}
