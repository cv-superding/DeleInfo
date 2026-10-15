using DeleInfo.Core.Model;

namespace DeleInfo.Core.Cleaners;

public interface ICleaner
{
    string Id { get; }
    string DisplayName { get; }
    TraceLevel Level { get; }

    /// <summary>只做路径枚举，不 stat 大目录；求大小由 ScanEngine 并行完成。</summary>
    IEnumerable<CleanTarget> Discover(ScanOptions options);
}

public sealed record ScanOptions
{
    public TraceLevel MaxLevel { get; init; } = TraceLevel.Credential;
    public bool IncludeLowConfidence { get; init; }
    public string? Only { get; init; }
    public string? Except { get; init; }

    /// <summary>逗号分隔多值：--only "微信,企业微信,QQ" 一次圈定一组应用。</summary>
    public bool MatchesApp(string app)
    {
        if (Only is { Length: > 0 } && !AnyMatch(app, Only))
            return false;
        if (Except is { Length: > 0 } && AnyMatch(app, Except))
            return false;
        return true;
    }

    private static bool AnyMatch(string app, string patterns) =>
        patterns.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(p => app.Contains(p, StringComparison.OrdinalIgnoreCase));
}
