namespace DeleInfo.Core.Util;

/// <summary>
/// 判断一个文本里是否真的含有可用凭据。
///
/// 为什么按前缀而不是按文件名：各家 access token 的格式前缀是公开约定
/// （GitHub 的 ghp_、AWS 的 AKIA、npm 的 npm_），认出前缀就等于确认"这里有一个能登录的令牌"，
/// 比猜文件名可靠得多——而误删一个恰好叫 token.json 的公开配置，代价是别人的应用被搞坏。
/// </summary>
public static class Secrets
{
    private readonly record struct Shape(string Prefix, int MinTotal, string Name);

    private static readonly Shape[] Shapes =
    {
        new("ghp_", 40, "GitHub PAT"),
        new("gho_", 40, "GitHub OAuth 令牌"),
        new("ghs_", 40, "GitHub App 令牌"),
        new("ghu_", 40, "GitHub 用户令牌"),
        new("github_pat_", 70, "GitHub 细粒度 PAT"),
        new("glpat-", 24, "GitLab PAT"),
        new("npm_", 36, "npm 令牌"),
        new("pypi-AgEIcHlwaXA", 60, "PyPI 上传令牌"),
        new("AKIA", 20, "AWS 访问密钥"),
        new("ASIA", 20, "AWS 临时密钥"),
        new("xoxb-", 50, "Slack Bot 令牌"),
        new("xoxp-", 50, "Slack 用户令牌"),
        new("xapp-", 50, "Slack App 令牌"),
        new("sk-proj-", 60, "OpenAI 项目密钥"),
        new("sk-", 40, "OpenAI/Stripe 密钥"),
        new("hf_", 40, "HuggingFace 令牌"),
        new("SG.", 60, "SendGrid 密钥"),
        new("AC-", 40, "Twilio 密钥"),
        new("ya29.", 60, "Google OAuth 令牌"),
        new("dpo-", 40, "DigitalOcean PAT"),
        new("dop_v1_", 60, "DigitalOcean OAuth"),
        new("rsecret_live", 40, "RESTIC 密钥"),
        new("docker_pat_", 40, "Docker PAT"),
    };

    private const string WordChars =
        "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_-.";

    /// <summary>返回命中的凭据种类，没命中返回 null。</summary>
    public static string? FindToken(string text)
    {
        if (string.IsNullOrEmpty(text))
            return null;

        foreach (var shape in Shapes)
        {
            for (var i = text.IndexOf(shape.Prefix, StringComparison.Ordinal);
                 i >= 0;
                 i = text.IndexOf(shape.Prefix, i + 1, StringComparison.Ordinal))
            {
                var run = i + shape.Prefix.Length;
                while (run < text.Length && WordChars.IndexOf(text[run]) >= 0)
                    run++;

                if (run - i >= shape.MinTotal)
                    return shape.Name;
            }
        }

        return null;
    }
}
