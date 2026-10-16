using System.IO;
using DeleInfo.Core.Cleaners;
using DeleInfo.Core.Engine;
using DeleInfo.Core.Model;
using Xunit;

namespace Cleaner.Tests;

/// <summary>
/// 扫描阶段的三条语义：不把自己的快照备份当登录态、同一路径不重复登记、
/// 低置信默认不勾选。第一条曾经让工具"清完还剩一堆"，第二条会让同一文件被删两次。
/// </summary>
public sealed class ScanEngineTests : IDisposable
{
    private readonly string _root;

    public ScanEngineTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "deleinfo-scan-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable(DeleInfo.Core.DataPaths.HomeVariable, Path.Combine(_root, "home"));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(DeleInfo.Core.DataPaths.HomeVariable, null);
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }

    private string Write(string name, int bytes = 64)
    {
        var full = Path.Combine(_root, name);
        File.WriteAllText(full, new string('a', bytes));
        return full;
    }

    private static CleanTarget Target(string path, bool low = false) => new()
    {
        CleanerId = "stub",
        App = "App",
        Profile = "Default",
        Item = "Cookie",
        Path = path,
        Level = TraceLevel.Session,
        Kind = TargetKind.File,
        NeedsSnapshot = true,
        LowConfidence = low,
    };

    private sealed class StubCleaner : ICleaner
    {
        private readonly IEnumerable<CleanTarget> _targets;

        public StubCleaner(IEnumerable<CleanTarget> targets) => _targets = targets;

        public string Id => "stub";
        public string DisplayName => "stub";
        public TraceLevel Level => TraceLevel.Session;
        public IEnumerable<CleanTarget> Discover(ScanOptions options) => _targets;
    }

    private static ScanResult Scan(IEnumerable<CleanTarget> targets, bool includeLow = false) =>
        new ScanEngine().Scan(new[] { new StubCleaner(targets) }, new ScanOptions
        {
            MaxLevel = TraceLevel.Credential,
            IncludeLowConfidence = includeLow,
        });

    [Fact]
    public void 自己的快照备份不会被当成登录态()
    {
        var live = Write("Cookies");
        var backup = Write("Cookies__dele_223419ab9be8");

        var result = Scan(new[] { Target(live), Target(backup) });

        var target = Assert.Single(result.Targets);
        Assert.Equal(live, target.Path);
    }

    [Fact]
    public void 同一路径只登记一次()
    {
        var path = Write("Login Data");

        var result = Scan(new[] { Target(path), Target(path) });

        Assert.Single(result.Targets);
    }

    [Fact]
    public void 低置信项默认不勾选_显式包含后才勾选()
    {
        var low = Write("guessme-session.json");

        var defaultScan = Scan(new[] { Target(low, low: true) });
        Assert.False(Assert.Single(defaultScan.Targets).Selected);

        var withFlag = Scan(new[] { Target(low, low: true) }, includeLow: true);
        Assert.True(Assert.Single(withFlag.Targets).Selected);
    }

    [Fact]
    public void 扫描会测出字节与文件数()
    {
        var path = Write("big.bin", 1000);

        var target = Assert.Single(Scan(new[] { Target(path) }).Targets);

        Assert.Equal(1000, target.Bytes);
        Assert.Equal(1, target.FileCount);
        Assert.True(target.Exists);
    }

    [Fact]
    public void 不存在的路径标记为不存在且不报错()
    {
        var missing = Path.Combine(_root, "gone", "Cookies");

        var target = Assert.Single(Scan(new[] { Target(missing) }).Targets);

        Assert.False(target.Exists);
        Assert.Equal(0, target.Bytes);
    }
}
