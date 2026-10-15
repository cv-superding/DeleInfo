using System.IO;
using DeleInfo.Core.Model;

namespace DeleInfo.Core.Cleaners;

/// <summary>
/// 原生 Win32 应用的登录态。不能依赖进程是否启动——令牌是落在磁盘上的，
/// 软件关没关都在；也不能只认 Chromium 目录特征（本机实测 QQ 的 login.enc、
/// 抖音的 tt_Cookies、腾讯 CTLogin 都不在那套结构里）。
///
/// 两条来源：
/// 1. 已在本机实测存在的路径规则表（高置信，默认可清）。
/// 2. 通用命名/内容签名扫描（覆盖未知应用；证据不足时降为低置信，需 --include-low）。
/// </summary>
public sealed class NativeAppCleaner : ICleaner
{
    public string Id => "app.native";
    public string DisplayName => "原生应用登录态";
    public TraceLevel Level => TraceLevel.Credential;

    private sealed record Rule(string App, string PathToken, bool IsDirectory, TraceLevel Level, string Item, string Why);

    /// <summary>规则表里的每一条都是本机 find 实测出来的，不是凭印象写的。</summary>
    private static readonly Rule[] Rules =
    {
        new("QQ", "%APPDATA%/QQ/auth", true, TraceLevel.Credential, "QQ 登录令牌目录", "本机存在 auth/login.enc"),
        new("QQ", "%APPDATA%/QQNT/auth", true, TraceLevel.Credential, "QQNT 登录令牌目录", "同名规则，存在则清"),
        // 注意：不要整目录清 QQEX\users——实测 371MB 里绝大部分是聊天记录与图片。
        // 每个账号目录由 Electron 清理器当 profile 精确处理（Session Storage / Cookies / Local Storage）。
        new("腾讯系登录 SDK", "%APPDATA%/Tencent/CTLogin", true, TraceLevel.Credential, "腾讯统一登录缓存", "QQ/微信/WeGame 共用登录态"),
        new("微信(新版)", "%APPDATA%/Tencent/xwechat/login", true, TraceLevel.Credential, "微信登录目录", "本机实测存在"),
        new("微信(新版)", "%APPDATA%/Tencent/xwechat/config", true, TraceLevel.Session, "微信配置(含账号)", "本机实测存在"),
        new("WeGame", "%APPDATA%/Tencent/WeGame/login_pic", true, TraceLevel.Credential, "WeGame 登录头像/账号残留", "本机实测存在"),
        new("抖音", "%APPDATA%/douyin/tt_Cookies", false, TraceLevel.Session, "抖音自有 Cookie 容器", "非 Chromium 结构，本机存在"),
        new("WXDrive", "%LOCALAPPDATA%/WXDrive/auth", true, TraceLevel.Credential, "微信网盘登录目录", "同名规则，存在则清"),

        // 微信 4.0 / 企业微信 / QQ NT 的登录态在「文档」里，不在 AppData。
        // 本机实测：xwechat_files 下有 3 个不同 wxid 的账号、共 815MB，其中登录态只占几 MB，
        // 所以规则精确到 login_config / session.db 这一级，聊天与媒体文件一律不动。
        new("微信(4.0)", "%DOCUMENTS%/xwechat_files/all_users/login/*", true, TraceLevel.Credential, "微信登录会话目录", "本机存在 all_users/login/<wxid>"),
        new("微信(4.0)", "%DOCUMENTS%/xwechat_files/*/config/login_config*", false, TraceLevel.Credential, "微信登录凭据配置", "本机实测 login_config / login_configv2 及 .crc"),
        new("微信(4.0)", "%DOCUMENTS%/xwechat_files/*/db_storage/session", true, TraceLevel.Credential, "微信会话数据库", "本机实测 session.db 及 material 文件"),
        new("企业微信", "%DOCUMENTS%/WXWork/*/Data/session.db*", false, TraceLevel.Credential, "企微会话数据库", "本机实测 WXWork/<corpid>/Data"),
        new("企业微信", "%DOCUMENTS%/WXWork/*/Index/session_index*", false, TraceLevel.Credential, "企微会话索引", "本机实测"),
        new("企业微信", "%DOCUMENTS%/WXWork/Global/qrcode_login_user_avator", true, TraceLevel.Session, "企微扫码登录账号残留", "本机实测存在"),
        new("QQ(NT)", "%DOCUMENTS%/Tencent Files/nt_qq/global/nt_db/login.db*", false, TraceLevel.Credential, "QQ 登录数据库", "本机实测 nt_db/login.db"),
        new("QQ(NT)", "%DOCUMENTS%/Tencent Files/nt_qq/global/nt_data/Login", true, TraceLevel.Credential, "QQ 登录数据目录", "本机实测 nt_data/Login"),
    };

    /// <summary>命名签名：目录或文件名命中即视为登录态候选。</summary>
    private static readonly string[] NameSignatures =
    {
        "auth", "authorization", "login", "signin", "token", "tokens", "credential", "credentials",
        "session", "sessions", "cookie", "cookies", "wtlogin", "passp", "uin", "account", "accounts",
        "oauth", "keyfile", "deviceid", "identity",
    };

    /// <summary>命中的路径里若含这些片段，多半是崩溃/日志/统计而非登录态。</summary>
    private static readonly string[] NoiseFragments =
    {
        "crash", "crashpad", "crash-feedback", "sentry", "updater", "update/", "logs", "/log/",
        "analytics", "telemetry", "tmp", "temp", "backup", "report", "dumps", "metrics",
        "localappdata/packages", "iconcache", "shadercache",
    };

    /// <summary>内嵌 WebView 的缓存目录：属于宿主应用自己的渲染缓存，不是"别人登录留下的东西"。</summary>
    private static readonly string[] WebViewFragments =
    {
        "ebwebview", "webviewcache", $"{Path.DirectorySeparatorChar}cef{Path.DirectorySeparatorChar}",
        "webview2", "msedgewebview",
    };

    /// <summary>读小文件找令牌特征词；命中即升级为高置信并留下证据。</summary>
    private static readonly string[] EvidenceKeys =
    {
        "access_token", "refresh_token", "id_token", "accesskey", "sessionid", "session_key",
        "set-cookie", "wtlogin", "uin", "open_id", "unionid", "expires_in", "authorization",
        "password", "secret", "bearer ", "grant_type", "device_token", "auth_token",
    };

    private const long MaxProbeBytes = 256 * 1024;

    public IEnumerable<CleanTarget> Discover(ScanOptions options)
    {
        foreach (var t in RuleTargets(options))
            yield return t;
        foreach (var t in SignatureTargets(options))
            yield return t;
    }

    private IEnumerable<CleanTarget> RuleTargets(ScanOptions options)
    {
        foreach (var rule in Rules)
        {
            if (rule.Level > options.MaxLevel)
                continue;
            if (!options.MatchesApp(rule.App))
                continue;

            foreach (var path in Expand(rule.PathToken, rule.IsDirectory))
            {
                yield return new CleanTarget
                {
                    CleanerId = Id,
                    App = rule.App,
                    // 把 wxid / corpid / QQ 号提到账号列，预览里才看得出"这是谁登录留下的"。
                    Profile = AccountOf(rule.PathToken, path, rule.IsDirectory),
                    Item = $"{rule.Item}（规则：{rule.Why}）",
                    Path = path,
                    Level = rule.Level,
                    Kind = rule.IsDirectory ? TargetKind.Directory : TargetKind.File,
                    NeedsSnapshot = true,
                };
            }
        }
    }

    /// <summary>取通配符所在段实际匹配到的那一级名字作为账号标识；文件规则取它所在的目录。</summary>
    private static string AccountOf(string tokenPath, string resolved, bool isDirectory)
    {
        var star = tokenPath.IndexOf('*');
        if (star < 0)
            return Path.GetFileName(Path.GetDirectoryName(resolved)) is { Length: > 0 } p ? p : "固定路径";

        var prefix = tokenPath[..star];
        var cut = prefix.LastIndexOf('/');
        var keep = cut > 0 ? prefix[..(cut + 1)] : prefix;
        var realRoot = ResolveRootToken(keep);
        if (realRoot is null)
            return "固定路径";

        // 通配符落在文件名上时（login.db*），账号是它所在的目录，不是文件本身。
        var subject = isDirectory ? resolved : Path.GetDirectoryName(resolved) ?? resolved;
        if (subject.Length <= realRoot.Length)
            return Path.GetFileName(subject);

        var tail = subject[realRoot.Length..].TrimStart('\\', '/');
        var first = tail.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return first.Length > 0 && first[0].Length > 0 ? first[0] : "固定路径";
    }

    private IEnumerable<CleanTarget> SignatureTargets(ScanOptions options)
    {
        foreach (var root in AppRoots())
        {
            string[] apps;
            try
            {
                apps = Directory.GetDirectories(root, "*", new EnumerationOptions
                {
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint,
                });
            }
            catch
            {
                continue;
            }

            foreach (var app in apps)
            {
                var appName = Path.GetFileName(app);
                if (IsDeniedApp(appName) || !options.MatchesApp(appName))
                    continue;

                foreach (var hit in WalkSignatures(app))
                {
                    var (path, isDir, evidence) = hit;

                    // 规则表已经覆盖过的路径不再重复登记。
                    if (CoveredByRule(path))
                        continue;

                    var confident = evidence is not null || IsUnambiguousName(path);
                    if (!confident && !options.IncludeLowConfidence)
                        continue;

                    yield return new CleanTarget
                    {
                        CleanerId = Id,
                        App = appName,
                        Profile = isDir ? "目录" : "文件",
                        Item = evidence is null
                            ? $"命名命中「{Path.GetFileName(path)}」"
                            : $"命名命中「{Path.GetFileName(path)}」，内容含「{evidence}」",
                        Path = path,
                        Level = TraceLevel.Credential,
                        Kind = isDir ? TargetKind.Directory : TargetKind.File,
                        NeedsSnapshot = true,
                        LowConfidence = !confident,
                    };
                }
            }
        }
    }

    private static IEnumerable<(string Path, bool IsDir, string? Evidence)> WalkSignatures(string appDir)
    {
        var stack = new Stack<(string Dir, int Depth)>();
        stack.Push((appDir, 0));
        var emitted = 0;

        while (stack.Count > 0 && emitted < 40)
        {
            var (dir, depth) = stack.Pop();
            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(dir, "*", new EnumerationOptions
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

            foreach (var entry in entries)
            {
                var name = Path.GetFileName(entry);
                var probe = entry.Replace('\\', '/').ToLowerInvariant();
                if (NoiseFragments.Any(n => probe.Contains(n, StringComparison.Ordinal)) ||
                    WebViewFragments.Any(n => probe.Contains(n, StringComparison.Ordinal)))
                    continue;
                if (name.Equals("Network", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("Partitions", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("User Data", StringComparison.OrdinalIgnoreCase))
                    continue; // Chromium 结构由专用清理器负责

                // 带 Local State 的目录是 Chromium/Electron 内核目录，其会话文件已被专用清理器覆盖，
                // 通用签名再扫一遍只会把 WebView2 缓存这类无关东西误报进来。
                if (File.Exists(Path.Combine(entry, "Local State")))
                    continue;

                if (MatchesSignature(name))
                {
                    var isDir = Directory.Exists(entry);
                    var evidence = isDir ? null : Probe(entry);
                    yield return (entry, isDir, evidence);
                    emitted++;
                    if (emitted >= 40)
                        yield break;
                }

                if (Directory.Exists(entry) && depth < 3)
                    stack.Push((entry, depth + 1));
            }
        }
    }

    private static bool MatchesSignature(string name)
    {
        var lower = name.ToLowerInvariant();
        return NameSignatures.Any(s =>
            lower.Equals(s, StringComparison.Ordinal) ||
            lower.StartsWith(s + ".", StringComparison.Ordinal) ||
            lower.StartsWith(s + "_", StringComparison.Ordinal) ||
            lower.StartsWith(s + "-", StringComparison.Ordinal) ||
            lower.EndsWith("_" + s, StringComparison.Ordinal) ||
            lower.EndsWith("_" + s + ".json", StringComparison.Ordinal));
    }

    /// <summary>目录/文件名本身就是 auth|login|credentials|token 这类，认定为登录容器。</summary>
    private static bool IsUnambiguousName(string path)
    {
        var lower = Path.GetFileName(path).ToLowerInvariant();
        return lower.StartsWith("auth", StringComparison.Ordinal) ||
               lower.StartsWith("login", StringComparison.Ordinal) ||
               lower.StartsWith("credential", StringComparison.Ordinal) ||
               lower.StartsWith("token", StringComparison.Ordinal) ||
               lower.StartsWith("wtlogin", StringComparison.Ordinal) ||
               lower.Contains("session", StringComparison.Ordinal) ||
               lower.Contains("cookie", StringComparison.Ordinal);
    }

    private static string? Probe(string file)
    {
        try
        {
            var info = new FileInfo(file);
            if (info.Length <= 0 || info.Length > MaxProbeBytes)
                return null;

            var text = File.ReadAllText(file);
            foreach (var key in EvidenceKeys)
            {
                if (text.Contains(key, StringComparison.OrdinalIgnoreCase))
                    return key;
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<string> AppRoots()
    {
        foreach (var special in new[]
                 {
                     Environment.SpecialFolder.ApplicationData,
                     Environment.SpecialFolder.LocalApplicationData,
                 })
        {
            var path = Environment.GetFolderPath(special);
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                yield return path;
        }
    }

    /// <summary>这些应用的登录态由专用清理器负责，或属于系统组件，不能双头处理。</summary>
    private static readonly string[] DeniedApps =
    {
        "Qoder", "com.qodercn", "Microsoft", "Mozilla", "Packages", "Temp", "ConnectedDevicesPlatform",
        "PeerDistRepub", "D3DSCache", "CrashDumps", "IconCacheServices", "Kingsoft", "Tencent",
        "Programs", "Publishers", "NVIDIA", "AMD", "Intel", "Lenovo", "Huawei",
    };

    private static bool IsDeniedApp(string name) =>
        DeniedApps.Any(d => name.StartsWith(d, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<string> Expand(string tokenPath, bool isDirectory)
    {
        var full = ResolveRootToken(tokenPath);
        if (full is null)
            return Array.Empty<string>();

        var segments = full.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2)
            return Array.Empty<string>();

        var current = new List<string> { segments[0] + Path.DirectorySeparatorChar };

        for (var i = 1; i < segments.Length; i++)
        {
            var pattern = segments[i];
            var last = i == segments.Length - 1;
            var next = new List<string>();

            foreach (var dir in current)
            {
                try
                {
                    if (!Directory.Exists(dir))
                        continue;
                    if (last && !isDirectory)
                        next.AddRange(Directory.GetFiles(dir, pattern));
                    else
                        next.AddRange(Directory.GetDirectories(dir, pattern));
                }
                catch
                {
                    /* 无权访问的分支直接放弃 */
                }
            }

            current = next;
            if (current.Count == 0)
                break;
        }

        return current.Where(p => isDirectory ? Directory.Exists(p) : File.Exists(p));
    }

    /// <summary>把 %APPDATA% 之类的记号换回真实路径，供通配符逐段展开。</summary>
    private static string? ResolveRootToken(string tokenPath)
    {
        foreach (var (token, real) in SpecialRoots())
        {
            if (string.IsNullOrEmpty(real) || !tokenPath.StartsWith(token, StringComparison.Ordinal))
                continue;
            var rest = tokenPath[token.Length..].TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
            return rest.Length == 0 ? real : Path.Combine(real, rest);
        }
        return null;
    }

    private static IEnumerable<(string Token, string Path)> SpecialRoots()
    {
        yield return ("%APPDATA%", Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
        yield return ("%LOCALAPPDATA%", Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        yield return ("%USERPROFILE%", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        yield return ("%DOCUMENTS%", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        yield return ("%COMMONAPPDATA%", Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
    }

    /// <summary>规则表实际命中的路径；通用签名扫描据此避免重复登记同一样东西。</summary>
    private static readonly Lazy<HashSet<string>> RulePathSet = new(() =>
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in Rules)
            foreach (var path in Expand(rule.PathToken, rule.IsDirectory))
                set.Add(path.TrimEnd(Path.DirectorySeparatorChar));
        return set;
    });

    private static bool CoveredByRule(string path)
    {
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar);
        foreach (var rulePath in RulePathSet.Value)
        {
            if (string.Equals(trimmed, rulePath, StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith(rulePath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
