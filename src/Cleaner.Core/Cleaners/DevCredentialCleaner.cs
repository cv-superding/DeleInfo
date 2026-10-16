using System.IO;
using DeleInfo.Core.Model;
using DeleInfo.Core.Util;

namespace DeleInfo.Core.Cleaners;

/// <summary>
/// 开发者凭据：别人用你的电脑登录过 git / npm / aws / docker 之后，令牌往往以**明文**留在
/// 用户目录的点文件里。这类东西比浏览器 cookie 危险得多，也最容易被忽略。
///
/// 两条纪律：
/// 1. 判定靠内容不靠文件名——必须真在文件里认出 token 形状才算高置信；
///    误删一个恰好叫 token.json 的公开配置，代价是别人的工具被搞坏。
/// 2. 报告里只写"含 GitHub PAT"这种种类信息，绝不把令牌本身写进任何输出或快照清单。
///
/// 有意不碰：.ssh 私钥。私钥不是"登录态"，删掉是毁钥匙而不是登出，且不可恢复。
/// </summary>
public sealed class DevCredentialCleaner : ICleaner
{
    public string Id => "dev.credentials";
    public string DisplayName => "开发者凭据文件";
    public TraceLevel Level => TraceLevel.Credential;

    private sealed record DevFile(string RelPath, string App, string Item);

    /// <summary>各家官方文档写明的存放位置；本机当前一个都不存在，存在即处理。</summary>
    private static readonly DevFile[] KnownFiles =
    {
        new(".git-credentials", "Git", "Git 明文凭据库（credential.helper store）"),
        new(".netrc", "Git/网络", "netrc 登录凭据"),
        new("_netrc", "Git/网络", "netrc 登录凭据（Windows 习惯名）"),
        new(".npmrc", "npm", "npm 仓库令牌"),
        new(".yarnrc.yml", "yarn", "yarn 注册表认证"),
        new(".pypirc", "PyPI", "PyPI 上传凭据"),
        new(".docker/config.json", "Docker", "镜像仓库 auth（base64 明文）"),
        new(".aws/credentials", "AWS", "AWS 访问密钥"),
        new(".aws/config", "AWS", "AWS 配置（可能含会话令牌）"),
        new(".config/gh/hosts.yml", "GitHub CLI", "gh 的 oauth_token"),
        new(".config/gcloud/credentials.db", "gcloud", "Google Cloud 凭据库"),
        new(".config/gcloud/access_token.json", "gcloud", "Google 访问令牌"),
        new(".config/huggingface/token", "HuggingFace", "HF 访问令牌"),
        new(".cache/huggingface/token", "HuggingFace", "HF 访问令牌（缓存路径）"),
        new(".config/gitlab/token", "GitLab", "GitLab PAT"),
        new(".config/pypoetry/config.toml", "Poetry", "私有源凭据"),
        new(".pip/pip.conf", "pip", "pip 索引认证"),
        new(".config/fnm/.token", "fnm", "GitHub token"),
    };

    /// <summary>额外兜底扫描的目录（限深一层，避免遍历整个用户目录）。</summary>
    private static readonly string[] SweepDirs =
    {
        ".", ".config", ".aws", ".docker", ".azure", ".cache/huggingface", ".gradle", ".m2",
    };

    /// <summary>文件名看起来像配置，才值得读内容。</summary>
    private static readonly string[] ConfigNameHints =
    {
        "credential", "token", "auth", "secret", "config", ".rc", "hosts", "passwd", "password",
        "key", "login", "session",
    };

    private static readonly string[] ConfigExtensions =
        [".json", ".yml", ".yaml", ".toml", ".ini", ".conf", ".cfg", ".netrc", ".xml", ".properties"];

    private const long MaxProbeBytes = 256 * 1024;
    private const int SweepBudget = 400;

    public IEnumerable<CleanTarget> Discover(ScanOptions options)
    {
        if (options.MaxLevel < TraceLevel.Credential)
            yield break;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home))
            yield break;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var scanned = 0;

        foreach (var file in KnownFiles)
        {
            if (!options.MatchesApp(file.App))
                continue;

            var full = Path.Combine(home, file.RelPath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(full) || !seen.Add(full))
                continue;

            var target = Build(full, file.App, file.Item);
            if (target is not null)
                yield return target;
        }

        // 兜底：未收录的配置类文件，只要内容里有真令牌也揪出来。
        foreach (var dir in SweepDirs)
        {
            var absolute = Path.Combine(home, dir.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(absolute))
                continue;

            foreach (var raw in SafeEnumerateFiles(absolute))
            {
                if (scanned++ > SweepBudget)
                    yield break;

                var path = Path.GetFullPath(raw);
                if (!seen.Add(path) || IsExcluded(path, home))
                    continue;

                var name = Path.GetFileName(path);
                var looksConfig = ConfigExtensions.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase)
                                  || ConfigNameHints.Any(h => name.Contains(h, StringComparison.OrdinalIgnoreCase));
                if (!looksConfig)
                    continue;

                var evidence = Probe(path);
                if (evidence is null)
                    continue;

                var target = Build(path, "开发者配置", $"未收录的凭据文件（内容含 {evidence}）");
                if (target is not null)
                    yield return target;
            }
        }
    }

    private CleanTarget? Build(string path, string app, string item)
    {
        // 必须规范化：sweep 用 "." 作目录会拼出 home\.\.git-credentials，
        // 与规则表里的 home\.git-credentials 看起来是两个不同路径，同一文件就会被登记两次。
        var full = Path.GetFullPath(path);
        var evidence = Probe(full);
        if (evidence is null)
        {
            // 文件在但读不出令牌：可能是空文件、加密库或格式不认识。
            // 不默认删——删错了是别人的开发环境。
            return new CleanTarget
            {
                CleanerId = Id,
                App = app,
                Profile = "文件",
                Item = item + "（未检出令牌特征）",
                Path = full,
                Level = TraceLevel.Credential,
                Kind = TargetKind.File,
                NeedsSnapshot = true,
                LowConfidence = true,
            };
        }

        return new CleanTarget
        {
            CleanerId = Id,
            App = app,
            Profile = "文件",
            Item = $"{item}（内容含 {evidence}）",
            Path = full,
            Level = TraceLevel.Credential,
            Kind = TargetKind.File,
            NeedsSnapshot = true,
        };
    }

    /// <summary>只回令牌种类，绝不回内容——避免把密钥抄进日志、清单或界面。</summary>
    private static string? Probe(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Length <= 0 || info.Length > MaxProbeBytes)
                return null;

            var text = File.ReadAllText(path);
            var token = Secrets.FindToken(text);
            if (token is not null)
                return token;

            // 没有公开前缀的令牌，退一步认这些字段的键名。
            foreach (var key in new[]
                     {
                         "_authToken", "aws_secret_access_key", "password=", "passwd=", "token=",
                         "oauth_token", "access_token", "authorization:", "x-oauth-token",
                     })
            {
                if (text.Contains(key, StringComparison.OrdinalIgnoreCase))
                    return key.TrimEnd('=', ':');
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<string> SafeEnumerateFiles(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir, "*", new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = false,
                AttributesToSkip = FileAttributes.ReparsePoint,
            }).ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>私钥与已知非凭据文件永不列入。</summary>
    private static bool IsExcluded(string path, string home)
    {
        var rel = Path.GetRelativePath(home, path).Replace('\\', '/');
        if (rel.StartsWith(".ssh/", StringComparison.OrdinalIgnoreCase))
            return true;
        var name = Path.GetFileName(rel);
        return name.Equals("known_hosts", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("known_hosts.old", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("id_", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith(".pub", StringComparison.OrdinalIgnoreCase) ||
               rel.Contains("hooks", StringComparison.OrdinalIgnoreCase);
    }
}
