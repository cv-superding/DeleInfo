using System.IO;
using DeleInfo.Core.Util;
using Xunit;

namespace Cleaner.Tests;

public sealed class FsTests : IDisposable
{
    private readonly string _root;

    public FsTests() => _root = Path.Combine(Path.GetTempPath(), "deleinfo-fs-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }

    private string Tree(int files, int bytesEach)
    {
        var dir = Path.Combine(_root, "tree");
        for (var i = 0; i < files; i++)
        {
            var sub = Path.Combine(dir, $"g{i % 5}");
            Directory.CreateDirectory(sub);
            File.WriteAllBytes(Path.Combine(sub, $"f{i}.bin"), new byte[bytesEach]);
        }
        return dir;
    }

    [Fact]
    public void Measure_递归求和字节与文件数()
    {
        var dir = Tree(20, 512);

        var (bytes, files) = Fs.Measure(dir);

        Assert.Equal(20, files);
        Assert.Equal(20L * 512, bytes);
    }

    [Fact]
    public void Measure_单个文件算一个()
    {
        var file = Path.Combine(_root, "one.bin");
        Directory.CreateDirectory(_root);
        File.WriteAllBytes(file, new byte[7]);

        Assert.Equal((7L, 1), Fs.Measure(file));
    }

    [Fact]
    public void FastDelete_能删掉整棵子树()
    {
        var dir = Tree(30, 128);

        Fs.FastDelete(dir, 4);

        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public void FastDelete_路径已消失时不抛异常()
    {
        Fs.FastDelete(Path.Combine(_root, "never-existed"), 2);
    }

    [Fact]
    public void TryExclusiveOpen_能识别被占用的文件()
    {
        Directory.CreateDirectory(_root);
        var file = Path.Combine(_root, "locked.bin");
        File.WriteAllText(file, "x");

        using (new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.False(Fs.TryExclusiveOpen(file));
        }

        Assert.True(Fs.TryExclusiveOpen(file));
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(2048, "2 KB")]
    [InlineData(5 * 1024 * 1024, "5 MB")]
    [InlineData(3L * 1024 * 1024 * 1024, "3 GB")]
    public void Human_可读体积(long bytes, string expected) => Assert.Equal(expected, Fs.Human(bytes));
}
