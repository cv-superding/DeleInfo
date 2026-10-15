using System.IO;
using DeleInfo.Core.Discovery;
using DeleInfo.Core.Engine;
using DeleInfo.Core.Model;
using DeleInfo.Core.Specs;

namespace DeleInfo.Core.Cleaners;

/// <summary>Chrome / Edge / 360 / 搜狗 / QQ 等所有 Chromium 系浏览器。</summary>
public sealed class ChromiumBrowserCleaner : ICleaner
{
    public string Id => "chromium.browser";
    public string DisplayName => "Chromium 系浏览器";
    public TraceLevel Level => TraceLevel.Credential;

    public IEnumerable<CleanTarget> Discover(ScanOptions options)
    {
        foreach (var host in ChromiumLocator.Discover())
        {
            if (!options.MatchesApp(host.DisplayName))
                continue;

            foreach (var profile in ChromiumLocator.ProfileDirs(host.UserDataRoot)
                         .Concat(ChromiumLocator.PartitionDirs(host.UserDataRoot)))
            {
                var profileName = ChromiumLocator.ContainerLabel(profile, host.UserDataRoot);
                foreach (var t in Emit(host.DisplayName, profileName, profile, host, options))
                    yield return t;
            }
        }
    }

    internal static IEnumerable<CleanTarget> Emit(
        string app, string profileName, string profileDir, BrowserHost? host, ScanOptions options)
    {
        foreach (var spec in ChromiumSpecs.For(options.MaxLevel))
        {
            foreach (var (path, isDir) in Resolve(profileDir, spec))
            {
                yield return new CleanTarget
                {
                    CleanerId = "chromium.browser",
                    App = app,
                    Profile = profileName,
                    Item = spec.Item,
                    Path = path,
                    Level = spec.Level,
                    Kind = isDir ? TargetKind.Directory : TargetKind.File,
                    NeedsSnapshot = spec.NeedsSnapshot,
                    UnknownHost = host?.LowConfidence ?? false,
                    HostRunning = host?.Running ?? false,
                    ProcessHint = host?.ProcessName,
                };
            }
        }
    }

    /// <summary>把 -journal / -wal / -shm 旁挂文件一并纳入，否则残留可被恢复。</summary>
    private static IEnumerable<(string Path, bool IsDir)> Resolve(string profileDir, ChromiumItemSpec spec)
    {
        var full = Path.Combine(profileDir, spec.RelativePath.Replace('/', Path.DirectorySeparatorChar));

        if (spec.IsDirectory)
        {
            if (Directory.Exists(full))
                yield return (full, true);
            yield break;
        }

        if (File.Exists(full))
        {
            yield return (full, false);
            foreach (var suffix in spec.WithJournal
                         ? new[] { "-journal", "-wal", "-shm" }
                         : new[] { "-wal", "-shm" })
            {
                if (File.Exists(full + suffix))
                    yield return (full + suffix, false);
            }
        }
    }
}

/// <summary>
/// Electron / Chromium 内嵌的独立应用（微信新版、Discord、Slack、抖音、QQ…）。
/// 它们的登录态就在 %APPDATA%\&lt;App&gt;\ 下，结构与浏览器 profile 完全相同。
/// </summary>
public sealed class ElectronAppCleaner : ICleaner
{
    public string Id => "chromium.electron";
    public string DisplayName => "Electron 应用";
    public TraceLevel Level => TraceLevel.Credential;

    private static readonly HashSet<string> KnownApps = new(StringComparer.OrdinalIgnoreCase)
    {
        "Discord", "Slack", "Telegram Desktop", "WhatsApp", "dingtalk", "QQ", "QQNT",
        "douyin", "webcast_mate", "微信", "WeChat", "Weixin", "Lark", "Feishu",
        "Spotify", "zoom", "Microsoft Teams", "Teams", "Postman", "Notion", "Obs",
        "douyin_widget", "抖店工作台", "直播伴侣", "BaiduYunGuanjia", "baiduyun",
    };

    /// <summary>绝不清理：会把自己正在用的工具踢下线。</summary>
    private static readonly string[] Deny =
    {
        "Qoder", "com.qodercn", "Code", "Mozilla", "Microsoft", "Tencent", "Kingsoft",
    };

    public IEnumerable<CleanTarget> Discover(ScanOptions options)
    {
        var appdata = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrEmpty(appdata))
            yield break;

        string[] roots;
        try
        {
            roots = Directory.GetDirectories(appdata, "*", new EnumerationOptions
            {
                IgnoreInaccessible = true,
                // 只跳 junction；隐藏目录里同样藏着应用（与 ChromiumLocator 同样的坑）。
                AttributesToSkip = FileAttributes.ReparsePoint,
            });
        }
        catch
        {
            yield break;
        }

        foreach (var root in roots)
        {
            var name = Path.GetFileName(root);
            if (Deny.Any(d => name.Contains(d, StringComparison.OrdinalIgnoreCase)))
                continue;
            if (!LooksLikeChromiumRoot(root))
                continue;
            if (!options.MatchesApp(name))
                continue;

            // 陌生应用照样处理：它的 Cookies 就是登录态，等着按名字白名单只会漏清。
            var unknown = !KnownApps.Contains(name);

            foreach (var t in ElectronTargets(name, root, unknown, ProcessProbe.IsRunning(name), options))
                yield return t;
        }
    }

    private static bool LooksLikeChromiumRoot(string dir) =>
        File.Exists(Path.Combine(dir, "Local State")) &&
        (File.Exists(Path.Combine(dir, "Network", "Cookies")) ||
         File.Exists(Path.Combine(dir, "Cookies")) ||
         ChromiumLocator.ProfileDirs(dir).Count > 0);

    private static IEnumerable<CleanTarget> ElectronTargets(
        string app, string root, bool unknown, bool running, ScanOptions options)
    {
        var profiles = new List<(string Name, string Dir)> { ($"{app}(根)", root) };
        foreach (var p in ChromiumLocator.ProfileDirs(root).Concat(ChromiumLocator.PartitionDirs(root)))
            profiles.Add((ChromiumLocator.ContainerLabel(p, root), p));

        foreach (var (profileName, dir) in profiles)
        {
            foreach (var spec in ChromiumSpecs.For(options.MaxLevel))
            {
                var full = Path.Combine(dir, spec.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                var hit = spec.IsDirectory ? Directory.Exists(full) : File.Exists(full);
                if (!hit)
                    continue;

                yield return new CleanTarget
                {
                    CleanerId = IdStatic,
                    App = app,
                    Profile = profileName,
                    Item = spec.Item,
                    Path = full,
                    Level = spec.Level,
                    Kind = spec.IsDirectory ? TargetKind.Directory : TargetKind.File,
                    NeedsSnapshot = spec.NeedsSnapshot,
                    UnknownHost = unknown,
                    HostRunning = running,
                };
            }
        }
    }

    private const string IdStatic = "chromium.electron";
}
