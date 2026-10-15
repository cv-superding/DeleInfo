using System.IO;

namespace DeleInfo.Core.Util;

public static class Fs
{
    private static readonly EnumerationOptions Recursive = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
    };

    public static bool Exists(string path) =>
        Directory.Exists(path) || File.Exists(path);

    /// <summary>
    /// 单趟枚举求总字节与文件数。用 EnumerateFiles 返回的 FileInfo（数据来自目录枚举，无需二次 stat）。
    /// </summary>
    public static (long bytes, int files) Measure(string path)
    {
        try
        {
            if (File.Exists(path))
                return (NewFile(path).Length, 1);

            if (!Directory.Exists(path))
                return (0, 0);

            long bytes = 0;
            int files = 0;
            foreach (var f in new DirectoryInfo(path).EnumerateFiles("*", Recursive))
            {
                bytes += f.Length;
                files++;
            }
            return (bytes, files);
        }
        catch
        {
            return (0, 0);
        }
    }

    private static FileInfo NewFile(string path) => new(path);

    /// <summary>
    /// 尝试以独占方式打开。用于判断文件是否被运行中的程序锁住。
    /// 目录返回 false（目录本身不能这样打开，调用方应检查其中的关键文件）。
    /// </summary>
    public static bool TryExclusiveOpen(string path)
    {
        try
        {
            using var _ = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// 递归删除，但把顶层子项并行化。Directory.Delete(path, true) 是单线程遍历，
    /// 对 Cache / IndexedDB 这类"很多个小目录"的结构，并行能明显提速。
    /// </summary>
    public static void FastDelete(string path, int maxParallel)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
            return;
        }

        if (!Directory.Exists(path))
            return;

        var children = Directory.GetFileSystemEntries(path, "*", SearchOption.TopDirectoryOnly);

        if (children.Length > 1)
        {
            Parallel.ForEach(children,
                new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, maxParallel) },
                child => DeleteLeaf(child));
        }
        else
        {
            foreach (var child in children)
                DeleteLeaf(child);
        }

        // 并行删除后可能仍有子项因时序未删净，兜底一次。
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
    }

    private static void DeleteLeaf(string path)
    {
        if (File.Exists(path))
        {
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
            return;
        }

        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
    }

    /// <summary>同卷改名 = O(1)，这是"快照但不拷贝"能瞬间完成的原因。</summary>
    public static void MoveInto(string source, string destination)
    {
        if (Directory.Exists(source))
        {
            Directory.Move(source, destination);
            return;
        }

        if (File.Exists(source))
        {
            var parent = Path.GetDirectoryName(destination);
            if (parent is not null)
                Directory.CreateDirectory(parent);
            File.Move(source, destination);
            return;
        }

        throw new FileNotFoundException($"源不存在: {source}");
    }

    public static string Human(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB",
    };
}
