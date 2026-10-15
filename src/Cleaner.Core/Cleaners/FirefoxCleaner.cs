using System.IO;
using DeleInfo.Core.Discovery;
using DeleInfo.Core.Model;

namespace DeleInfo.Core.Cleaners;

/// <summary>
/// Firefox 的登录态分散在 cookies.sqlite / sessionstore / storage-permission 里，
/// 密码在 key4.db + logins.json。places.sqlite 装着书签与历史，属于明确保留项，绝不列进来。
/// </summary>
public sealed class FirefoxCleaner : ICleaner
{
    public string Id => "firefox";
    public string DisplayName => "Firefox";
    public TraceLevel Level => TraceLevel.Credential;

    private static readonly (string Rel, TraceLevel Level, string Item, bool Dir, bool Snapshot)[] Specs =
    {
        ("cookies.sqlite", TraceLevel.Session, "Cookie（登录令牌）", false, true),
        ("sessionstore.jsonlz4", TraceLevel.Session, "会话状态", false, true),
        ("sessionstore.js", TraceLevel.Session, "会话状态（旧版）", false, true),
        ("sessionstore-backups", TraceLevel.Session, "会话备份目录", true, true),
        ("storage/default", TraceLevel.Session, "站点独立存储（含 IDB/localStorage）", true, false),
        ("key4.db", TraceLevel.Credential, "密码主密钥库", false, true),
        ("logins.json", TraceLevel.Credential, "保存的密码", false, true),
        ("logins.db", TraceLevel.Credential, "保存的密码（旧版）", false, true),
    };

    public IEnumerable<CleanTarget> Discover(ScanOptions options)
    {
        foreach (var profile in FirefoxLocator.Discover())
        {
            if (!options.MatchesApp(profile.ProfileName))
                continue;

            foreach (var (rel, level, item, isDir, snapshot) in Specs)
            {
                if (level > options.MaxLevel)
                    continue;

                var full = Path.Combine(profile.ProfileDir, rel.Replace('/', Path.DirectorySeparatorChar));
                if (isDir ? !Directory.Exists(full) : !File.Exists(full))
                    continue;

                yield return Build(profile, level, item, full, isDir, snapshot);

                if (!isDir)
                {
                    foreach (var suffix in new[] { "-journal", "-wal", "-shm" })
                    {
                        if (File.Exists(full + suffix))
                            yield return Build(profile, level, $"{item}（旁挂文件）", full + suffix, false, snapshot);
                    }
                }
            }
        }
    }

    private static CleanTarget Build(FirefoxProfile profile, TraceLevel level, string item,
        string full, bool isDir, bool snapshot) => new()
    {
        CleanerId = "firefox",
        App = profile.ProfileName,
        Profile = Path.GetFileName(profile.ProfileDir),
        Item = item,
        Path = full,
        Level = level,
        Kind = isDir ? TargetKind.Directory : TargetKind.File,
        NeedsSnapshot = snapshot,
        HostRunning = profile.Running,
    };
}
