using System.IO;
using DeleInfo.Core;
using DeleInfo.Core.Cleaners;
using DeleInfo.Core.Engine;
using DeleInfo.Core.Model;
using Xunit;
using Xunit.Abstractions;

// 这些测试会设 DELEINFO_HOME 环境变量（进程级），必须串行跑，不能并行。
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Cleaner.Tests;

/// <summary>
/// 覆盖破坏性路径：可还原快照、硬删、文件占用、同日两轮不撞名、purge 真擦。
/// 这几条要是回退，工具就会开始悄悄毁数据而不是悄悄跳过。
/// </summary>
public sealed class SnapshotLifecycleTests : IDisposable
{
    private readonly string _root;
    private readonly string _snapRoot;

    public SnapshotLifecycleTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "deleinfo-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
        _snapRoot = Path.Combine(_root, "snap");

        // 把运行期数据改道到临时目录，别让测试往真用户目录写历史。
        Environment.SetEnvironmentVariable(DataPaths.HomeVariable, Path.Combine(_root, "home"));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(DataPaths.HomeVariable, null);
        try { Directory.Delete(_root, recursive: true); } catch { /* 临时目录交给系统回收 */ }
    }

    private string WriteFile(string relative, string content)
    {
        var full = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    private static CleanTarget Target(string path, bool needsSnapshot = true, TraceLevel level = TraceLevel.Session) => new()
    {
        CleanerId = "test",
        App = "TestApp",
        Profile = "Default",
        Item = "Cookie",
        Path = path,
        Level = level,
        Kind = File.Exists(path) ? TargetKind.File : TargetKind.Directory,
        NeedsSnapshot = needsSnapshot,
        Selected = true,
    };

    private CleanResult RunWithStore(IReadOnlyList<CleanTarget> targets, bool reversible = true) =>
        new CleanEngine(Array.Empty<ICleaner>()).Execute(targets, new CleanSettings
        {
            Reversible = reversible,
            SnapshotRoot = _snapRoot,
        });

    [Fact]
    public void 快照模式_原文件消失且可完整还原()
    {
        var file = WriteFile("profile/Network/Cookies", "secret-cookie-bytes");

        var result = RunWithStore(new[] { Target(file) });

        var outcome = Assert.Single(result.Outcomes);
        Assert.Equal(OutcomeStatus.Snapshotted, outcome.Status);
        Assert.False(File.Exists(file));
        Assert.True(File.Exists(outcome.SnapshotRef));

        var (ok, failed, _) = new RestoreEngine(new SnapshotStore(_snapRoot)).Restore(result.RunId);

        Assert.Equal(1, ok);
        Assert.Equal(0, failed);
        Assert.Equal("secret-cookie-bytes", File.ReadAllText(file));
    }

    [Fact]
    public void 硬删模式_不留快照也不可还原()
    {
        var file = WriteFile("profile/Login Data", "pwdb");

        var result = RunWithStore(new[] { Target(file, level: TraceLevel.Credential) }, reversible: false);

        Assert.Equal(OutcomeStatus.Removed, Assert.Single(result.Outcomes).Status);
        Assert.False(File.Exists(file));
        Assert.Empty(Directory.GetFiles(_snapRoot, "*.json"));
    }

    [Fact]
    public void 文件被占用时跳过而不是失败或半删()
    {
        var file = WriteFile("profile/Network/Cookies", "locked-by-browser");

        // 模拟浏览器持有该文件：独占打开，不给任何共享位。
        using (new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var result = RunWithStore(new[] { Target(file) });

            var outcome = Assert.Single(result.Outcomes);
            Assert.Equal(OutcomeStatus.Locked, outcome.Status);
            Assert.Equal(1, result.Locked);
            Assert.Equal(0, result.Failed);
        }

        Assert.Equal("locked-by-browser", File.ReadAllText(file));
    }

    [Fact]
    public void 不需要快照的大目录直接物理删除()
    {
        var dir = Path.Combine(_root, "profile", "Service Worker");
        Directory.CreateDirectory(dir);
        for (var i = 0; i < 50; i++)
            File.WriteAllText(Path.Combine(dir, $"f{i}.bin"), new string('x', 1024));

        var result = RunWithStore(new[] { Target(dir, needsSnapshot: false) });

        Assert.Equal(OutcomeStatus.Removed, Assert.Single(result.Outcomes).Status);
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public void 同一天两轮清理的快照不撞名()
    {
        var a = WriteFile("p1/Cookies", "first-run");
        var b = WriteFile("p2/Cookies", "second-run");

        var first = RunWithStore(new[] { Target(a) });
        var second = RunWithStore(new[] { Target(b) });

        Assert.NotEqual(first.RunId, second.RunId);
        Assert.Equal(OutcomeStatus.Snapshotted, Assert.Single(first.Outcomes).Status);
        Assert.Equal(OutcomeStatus.Snapshotted, Assert.Single(second.Outcomes).Status);

        var store = new SnapshotStore(_snapRoot);
        Assert.Equal(1, new RestoreEngine(store).Restore(first.RunId).ok);
        Assert.Equal(1, new RestoreEngine(store).Restore(second.RunId).ok);
        Assert.Equal("first-run", File.ReadAllText(a));
        Assert.Equal("second-run", File.ReadAllText(b));
    }

    [Fact]
    public void purge_之后快照内容真的没了()
    {
        var file = WriteFile("profile/Cookies", "to-be-gone");
        var result = RunWithStore(new[] { Target(file) });
        var bak = Assert.Single(result.Outcomes).SnapshotRef!;
        Assert.True(File.Exists(bak));

        var (deleted, _, error) = new RestoreEngine(new SnapshotStore(_snapRoot)).Purge(result.RunId);

        Assert.Null(error);
        Assert.Equal(1, deleted);
        Assert.False(File.Exists(bak));
    }

    [Fact]
    public void 清理会留下运行流水并可查()
    {
        var file = WriteFile("profile/Cookies", "logged");

        var result = RunWithStore(new[] { Target(file) });

        var record = Assert.Single(RunHistory.Load(10), r => r.RunId == result.RunId);
        Assert.Equal(1, record.Snapshotted);
        Assert.Equal("TestApp", Assert.Single(record.Apps));
    }
}
