using DeleInfo.Core.Util;
using Xunit;

namespace Cleaner.Tests;

/// <summary>
/// 令牌识别既不能漏（漏了等于隐私没清）也不能错（错了会删掉别人的正常配置）。
/// 这里用明显伪造的字符串，不含任何可用凭据。
/// </summary>
public class SecretsTests
{
    private const string Fake40 = "FAKEFAKEFAKEFAKEFAKEFAKEFAKEFAKEFAKEFAKE";

    [Theory]
    [InlineData("ghp_" + Fake40, "GitHub PAT")]
    [InlineData("github_pat_" + Fake40 + Fake40, "GitHub 细粒度 PAT")]
    [InlineData("npm_" + Fake40, "npm 令牌")]
    [InlineData("AKIA" + Fake40, "AWS 访问密钥")]
    [InlineData("glpat-" + Fake40, "GitLab PAT")]
    [InlineData("hf_" + Fake40, "HuggingFace 令牌")]
    [InlineData("xoxb-" + Fake40 + Fake40, "Slack Bot 令牌")]
    public void 认出真实令牌形状(string token, string expected)
    {
        Assert.Equal(expected, Secrets.FindToken($"host=x\nauthToken={token}\n"));
    }

    [Theory]
    [InlineData("ghp_short")]                       // 前缀对但长度不足
    [InlineData("sk-")]                             // 只有前缀
    [InlineData("task-scheduler config")]           // 含 sk- 但不是密钥
    [InlineData("This describes a break-through")]  // 含 sk- 的普通英文
    [InlineData("")]
    [InlineData("theme=dark")]
    public void 不误报普通文本(string text)
    {
        Assert.Null(Secrets.FindToken(text));
    }

    [Fact]
    public void 一行里有多个令牌也能认出()
    {
        var line = $"github=ghp_{Fake40} gitlab=glpat-{Fake40}";
        Assert.NotNull(Secrets.FindToken(line));
    }
}
