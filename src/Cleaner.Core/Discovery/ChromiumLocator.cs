using System.IO;
using DeleInfo.Core.Util;

namespace DeleInfo.Core.Discovery;

public sealed record BrowserHost(
    string UserDataRoot,
    string DisplayName,
    bool Running,
    bool LowConfidence,
    string? ProcessName);

/// <summary>
/// 在 %LOCALAPPDATA% / %APPDATA% 中定位 Chromium 系内核的宿主目录。
/// 本机实测存在 25+ 个 "User Data"，其中混有 CEF / WebView2 内嵌宿主（如直播伴侣、Clash Verge），
/// 因此不能靠"名字像浏览器"来判断，必须同时满足：目录特征 + 厂商白名单，二者缺一律降为低置信。
/// </summary>
public static class ChromiumLocator
{
    /// <summary>已确认为独立浏览器的宿主目录名（大小写不敏感，取 User Data 的上一级目录名）。</summary>
    private static readonly HashSet<string> KnownBrowsers = new(StringComparer.OrdinalIgnoreCase)
    {
        "Chrome", "Edge", "Brave-Browser", "Vivaldi", "Opera Stable", "Opera GX Stable",
        "Chromium", "Chrome Beta", "Chrome Dev", "Chrome SxS", "Edge Beta", "Edge Dev", "EdgeCanary",
        "360Chrome", "360ChromeX", "360SpeedDragon", "QQBrowser", "SogouExplorer", "SogouBrowser",
        "CentBrowser", "Maxthon", "TheWorld6", "TheWorld", "liebao", "LiebaoAI", "Quark", "UCBrowser",
        "HuaweiBrowser", "SLBrowser", "DcBrowser", "TSBrowser", "Twinkstar", "xbaibrowser", "xbbrowser",
        "BaiBeiBrowser", "Qingniao Chrome", "115Chrome", "2345Chrome", "GreenCore", "Sputnik",
        "Yandex", "CocCoc", "Whale", "Dragon", "puffin",
    };

    /// <summary>内嵌内核宿主：清掉它们会弄坏别人的应用登录，永不处理。</summary>
    private static readonly string[] DenyPathFragments =
    {
        "EBWebView", "webviewcache", $"{Path.DirectorySeparatorChar}CEF{Path.DirectorySeparatorChar}",
        "MediaSDK_Server", "WebView2", "MsEdgeWebView", $"{Path.DirectorySeparatorChar}Packages{Path.DirectorySeparatorChar}",
    };

    public static IReadOnlyList<BrowserHost> Discover()
    {
        var results = new List<BrowserHost>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var base_ in SearchBases())
        {
            foreach (var userData in FindUserDataDirs(base_))
            {
                if (!seen.Add(userData))
                    continue;

                var host = ResolveHost(userData);
                if (host is null)
                    continue;

                results.Add(host);
            }
        }

        return results
            .OrderBy(h => h.LowConfidence)
            .ThenBy(h => h.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IEnumerable<string> SearchBases()
    {
        foreach (var folder in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                 })
        {
            if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
                yield return folder;
        }
    }

    /// <summary>
    /// 有限深度 BFS：只找名为 "User Data" 的目录，不做全盘遍历。
    ///
    /// 注意不能跳过 Hidden/System —— 360/搜狗/腾讯/Cent/Maxthon 的厂商目录全都带 Hidden+System，
    /// 按属性过滤会正好漏掉最该清理的浏览器。需要防的是 junction 回环（Application Data 之类），
    /// 那只用 ReparsePoint 排除即可；噪声目录用显式名单剪枝，避免钻进 Temp 的深处。
    /// </summary>
    private static IEnumerable<string> FindUserDataDirs(string root, int maxDepth = 4)
    {
        var queue = new Queue<(string Dir, int Depth)>();
        queue.Enqueue((root, 0));
        var visited = 0;

        while (queue.Count > 0 && visited < VisitBudget)
        {
            var (dir, depth) = queue.Dequeue();
            visited++;

            string[] children;
            try
            {
                children = Directory.GetDirectories(dir, "*", new EnumerationOptions
                {
                    IgnoreInaccessible = true,
                    RecurseSubdirectories = false,
                    AttributesToSkip = FileAttributes.ReparsePoint,
                });
            }
            catch
            {
                continue;
            }

            foreach (var child in children)
            {
                var name = Path.GetFileName(child);
                if (IsDenied(child) || Prune.Contains(name) ||
                    name.StartsWith("CachedData", StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith("packages", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (string.Equals(name, "User Data", StringComparison.OrdinalIgnoreCase))
                {
                    yield return child;
                    continue; // 不再深入 profile 内部找嵌套的 "User Data"
                }

                if (depth + 1 < maxDepth)
                    queue.Enqueue((child, depth + 1));
            }
        }
    }

    private const int VisitBudget = 200000;

    private static readonly HashSet<string> Prune = new(StringComparer.OrdinalIgnoreCase)
    {
        "Temp", "temp", "Temptmp", "Cache", "cache", "Caches", "GPUCache", "GrShaderCache",
        "ShaderCache", "CrashDumps", "Crashpad", "INetCache", "IconCacheServices", "PeerDistRepub",
        "D3DSCache", "DeliveryOptimization", "WebCache", "PubCache", "TileStore", "WER",
        "node_modules", ".git", "Code Cache", "Service Worker", "blob_storage", "Default",
    };

    private static bool IsDenied(string path) =>
        DenyPathFragments.Any(f => path.Contains(f, StringComparison.OrdinalIgnoreCase));

    private static BrowserHost? ResolveHost(string userDataRoot)
    {
        // 目录特征：Chromium 内核必然有 Local State 或至少一个像样的 profile。
        var hasLocalState = File.Exists(Path.Combine(userDataRoot, "Local State"));
        var profiles = ProfileDirs(userDataRoot);
        if (profiles.Count == 0 && !hasLocalState)
            return null;
        if (profiles.Count == 0)
            return null;

        var parentDir = Path.GetDirectoryName(userDataRoot) ?? userDataRoot;
        var hostName = Path.GetFileName(parentDir);
        var processName = GuessProcessName(hostName, parentDir);
        var running = IsRunning(userDataRoot, processName);
        var lowConfidence = !KnownBrowsers.Contains(hostName);

        var display = BuildDisplay(parentDir, hostName);
        return new BrowserHost(userDataRoot, display, running, lowConfidence, processName);
    }

    private static string BuildDisplay(string parentDir, string hostName)
    {
        var vendor = Path.GetFileName(Path.GetDirectoryName(parentDir));
        if (string.IsNullOrEmpty(vendor) || vendor.Equals(hostName, StringComparison.OrdinalIgnoreCase))
            return hostName;
        return $"{vendor}/{hostName}";
    }

    /// <summary>
    /// Electron/国产客户端常把每个账号或内嵌浏览器隔离成一个子目录，容器名不止 Partitions 一种
    /// （本机实测 QQ 用 Partitions，QQEX 用 users，QQEX 的目录名就是 QQ 号）。
    /// 逐容器展开成 profile，比"整个 users 目录一删了之"精确得多——
    /// 那里面 371MB 主要是聊天记录和图片，不属于登录态。
    /// </summary>
    private static readonly string[] ProfileContainers =
        ["Partitions", "users", "Users", "Profiles", "profiles"];

    public static List<string> PartitionDirs(string root)
    {
        var list = new List<string>();

        foreach (var container in ProfileContainers)
        {
            var dir = Path.Combine(root, container);
            if (!Directory.Exists(dir))
                continue;

            try
            {
                foreach (var sub in Directory.GetDirectories(dir))
                {
                    if (LooksLikeProfile(sub))
                        list.Add(sub);
                }
            }
            catch
            {
                /* ignore */
            }
        }

        return list;
    }

    private static bool LooksLikeProfile(string dir) =>
        Directory.Exists(Path.Combine(dir, "Local Storage")) ||
        Directory.Exists(Path.Combine(dir, "Session Storage")) ||
        File.Exists(Path.Combine(dir, "Network", "Cookies")) ||
        File.Exists(Path.Combine(dir, "Cookies")) ||
        File.Exists(Path.Combine(dir, "Preferences"));

    /// <summary>报告里区分隔离分区，避免和 Default 混淆。</summary>
    public static string ContainerLabel(string profileDir, string root)
    {
        var rel = Path.GetRelativePath(root, profileDir).Replace(Path.DirectorySeparatorChar, '/');
        var slash = rel.IndexOf('/');
        return slash > 0 ? rel[..slash] + "/" + rel[(slash + 1)..] : rel;
    }

    private static string? LoginStateMarker(string profileDir)
    {
        foreach (var candidate in new[]
                 {
                     Path.Combine(profileDir, "Network", "Cookies"),
                     Path.Combine(profileDir, "Cookies"),
                 })
        {
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    /// <summary>该 profile 是否存在标准登录态文件；hosts 用它区分"没有登录态"与"有但被占用"。</summary>
    public static bool HasLoginState(string userDataRoot) =>
        ProfileDirs(userDataRoot).Any(p => LoginStateMarker(p) is not null);

    public static List<string> ProfileDirs(string userDataRoot)
    {
        var list = new List<string>();
        try
        {
            foreach (var dir in Directory.GetDirectories(userDataRoot))
            {
                var name = Path.GetFileName(dir);
                if (name.Equals("Default", StringComparison.OrdinalIgnoreCase) ||
                    (name.StartsWith("Profile ", StringComparison.OrdinalIgnoreCase) && name.Length <= 10))
                {
                    // profile 目录必须像 Chromium 的（有 Preferences），否则是厂商私有子目录。
                    if (File.Exists(Path.Combine(dir, "Preferences")) || File.Exists(Path.Combine(dir, "Web Data")))
                        list.Add(dir);
                }
            }
        }
        catch
        {
            /* ignore */
        }
        return list;
    }

    private static string? GuessProcessName(string hostName, string hostDir)
    {
        // 从厂商目录下的 Application\*.exe 猜主程序名，用于"正在运行"判断与提示。
        foreach (var sub in new[] { "Application", "Bin", "." })
        {
            try
            {
                var dir = Path.Combine(hostDir, sub);
                if (!Directory.Exists(dir))
                    continue;
                foreach (var exe in Directory.EnumerateFiles(dir, "*.exe", SearchOption.TopDirectoryOnly))
                {
                    var name = Path.GetFileNameWithoutExtension(exe);
                    if (name.Equals("setup", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("update", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("crash", StringComparison.OrdinalIgnoreCase))
                        continue;
                    return name;
                }
            }
            catch
            {
                /* ignore */
            }
        }
        return hostName.Contains("chrome", StringComparison.OrdinalIgnoreCase) ? "chrome" : hostName;
    }

    /// <summary>
    /// Lockfile 只在浏览器持有 profile 时存在且无法独占打开——比猜进程名可靠得多。
    /// </summary>
    private static bool IsRunning(string userDataRoot, string? processName)
    {
        var lockFile = Path.Combine(userDataRoot, "Lockfile");
        if (File.Exists(lockFile) && !Fs.TryExclusiveOpen(lockFile))
            return true;

        if (processName is null)
            return false;

        try
        {
            foreach (var p in System.Diagnostics.Process.GetProcessesByName(processName))
                return true;
        }
        catch
        {
            /* ignore */
        }
        return false;
    }
}
