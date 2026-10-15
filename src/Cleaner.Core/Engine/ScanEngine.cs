using System.Collections.Concurrent;
using System.Diagnostics;
using DeleInfo.Core.Cleaners;
using DeleInfo.Core.Model;
using DeleInfo.Core.Util;

namespace DeleInfo.Core.Engine;

/// <summary>
/// 扫描分两段：先并行枚举路径（几乎零成本），再并行 stat 求大小。
/// 大小计算是唯一可能慢的环节，所以只在需要预览时做，并且跳过缓存类目录之外的东西。
/// </summary>
public sealed class ScanEngine
{
    public int MaxParallelism { get; init; } = Math.Clamp(Environment.ProcessorCount, 4, 32);

    public ScanResult Scan(IReadOnlyList<ICleaner> cleaners, ScanOptions options)
    {
        var sw = Stopwatch.StartNew();
        var notes = new ConcurrentQueue<string>();
        var bag = new ConcurrentBag<CleanTarget>();

        Parallel.ForEach(cleaners,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, cleaners.Count) },
            cleaner =>
            {
                try
                {
                    foreach (var t in cleaner.Discover(options))
                        bag.Add(t);
                }
                catch (Exception ex)
                {
                    notes.Enqueue($"{cleaner.DisplayName} 枚举失败：{ex.Message}");
                }
            });

        // 同一路径可能被多个定位器命中（浏览器根目录与 Electron 根目录判定条件有交集）。
        // 同时排除本工具的快照备份：通配符规则（session.db*、login/*）会把 __dele_ 备份再扫一遍，
        // 看起来就像"清完还剩一堆"，再清一次还会给快照做快照。
        var targets = bag
            .Where(t => !t.Path.Contains(SnapshotStore.ArtifactMarker, StringComparison.Ordinal))
            .GroupBy(t => t.Path, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        Parallel.ForEach(targets,
            new ParallelOptions { MaxDegreeOfParallelism = MaxParallelism },
            t =>
            {
                if (t.Kind == TargetKind.External)
                {
                    t.Exists = true;
                    t.Bytes = 0;
                    t.FileCount = 1;
                    return;
                }

                t.Exists = Fs.Exists(t.Path);
                if (!t.Exists)
                    return;

                if (t.Kind == TargetKind.File && !Fs.TryExclusiveOpen(t.Path))
                {
                    t.Locked = true;
                    t.Error = "文件被占用";
                }

                var (bytes, files) = Fs.Measure(t.Path);
                t.Bytes = bytes;
                t.FileCount = files;

                if (t.HostRunning && !t.Locked)
                {
                    t.Locked = true;
                    t.Error = "宿主正在运行";
                }

                // --include-low 表示用户明确要把低置信目标纳入处理范围。
                t.Selected = !t.LowConfidence || options.IncludeLowConfidence;
            });

        sw.Stop();
        return new ScanResult
        {
            Targets = targets
                .OrderBy(t => t.App, StringComparer.OrdinalIgnoreCase)
                .ThenBy(t => t.Level)
                .ThenByDescending(t => t.Bytes)
                .ToList(),
            Elapsed = sw.Elapsed,
            Notes = notes.ToList(),
        };
    }
}
