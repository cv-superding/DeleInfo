using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using DeleInfo.Core.Cleaners;
using DeleInfo.Core.Model;
using DeleInfo.Core.Util;

namespace DeleInfo.Core.Engine;

public sealed record CleanSettings
{
    /// <summary>true = 需快照的项目改名入快照（瞬间完成、可还原，但数据仍在磁盘上）；
    /// false = 直接物理删除（不可还原，但真正擦除）。</summary>
    public bool Reversible { get; init; } = true;

    /// <summary>即便宿主在运行也强行尝试删除。</summary>
    public bool Force { get; init; }

    public string? SnapshotRoot { get; init; }

    public int MaxParallelism { get; init; } = Math.Clamp(Environment.ProcessorCount, 4, 32);
}

public sealed class CleanEngine
{
    private readonly IReadOnlyList<ICleaner> _cleaners;

    public CleanEngine(IReadOnlyList<ICleaner> cleaners) => _cleaners = cleaners;

    public CleanResult Execute(IReadOnlyList<CleanTarget> targets, CleanSettings settings, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var runId = DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6];
        var store = new SnapshotStore(settings.SnapshotRoot);
        var outcomes = new ConcurrentBag<TargetOutcome>();
        var snapshotEntries = new ConcurrentBag<SnapshotEntry>();

        var innerParallel = Math.Clamp(settings.MaxParallelism / 4, 2, 8);

        Parallel.ForEach(
            targets.Where(t => t.Selected),
            new ParallelOptions { MaxDegreeOfParallelism = settings.MaxParallelism, CancellationToken = ct },
            t =>
            {
                outcomes.Add(RemoveOne(t, settings, store, runId, snapshotEntries, innerParallel));
            });

        sw.Stop();

        if (!snapshotEntries.IsEmpty)
            store.WriteManifest(runId, snapshotEntries.OrderBy(e => e.Original, StringComparer.OrdinalIgnoreCase), DateTime.Now);

        return new CleanResult
        {
            RunId = runId,
            Outcomes = outcomes
                .OrderBy(o => o.Target.App, StringComparer.OrdinalIgnoreCase)
                .ThenBy(o => o.Status)
                .ToList(),
            Elapsed = sw.Elapsed,
        };
    }

    private TargetOutcome RemoveOne(
        CleanTarget t,
        CleanSettings settings,
        SnapshotStore store,
        string runId,
        ConcurrentBag<SnapshotEntry> snapshotEntries,
        int innerParallel)
    {
        if (t.Kind == TargetKind.External)
            return RemoveExternal(t);

        if (!Fs.Exists(t.Path))
            return new TargetOutcome(t, OutcomeStatus.Skipped, 0, null, "已不存在");

        if (t.Locked && !settings.Force)
            return new TargetOutcome(t, OutcomeStatus.Locked, t.Bytes, null,
                t.Error ?? "被占用" + (t.ProcessHint is null ? "" : $"（请先关闭 {t.ProcessHint}）"));

        try
        {
            if (settings.Reversible && t.NeedsSnapshot)
            {
                var bak = store.Take(t, runId);
                snapshotEntries.Add(new SnapshotEntry(t.Path, bak, t.Kind == TargetKind.Directory, t.Bytes, t.App, t.Item));
                return new TargetOutcome(t, OutcomeStatus.Snapshotted, t.Bytes, bak, null);
            }

            Fs.FastDelete(t.Path, innerParallel);
            return new TargetOutcome(t, OutcomeStatus.Removed, t.Bytes, null, null);
        }
        catch (Exception ex)
        {
            return new TargetOutcome(t, IsLock(ex) ? OutcomeStatus.Locked : OutcomeStatus.Failed, t.Bytes, null, Describe(ex));
        }
    }

    private TargetOutcome RemoveExternal(CleanTarget t)
    {
        if (_cleaners.FirstOrDefault(c => c.Id == t.CleanerId) is not ISelfRemovingCleaner self)
            return new TargetOutcome(t, OutcomeStatus.Failed, 0, null, "找不到对应的清理器");

        return self.TryRemove(t, out var error)
            ? new TargetOutcome(t, OutcomeStatus.Removed, 0, null, null)
            : new TargetOutcome(t, OutcomeStatus.Failed, 0, null, error);
    }

    /// <summary>32 = ERROR_SHARING_VIOLATION，33 = ERROR_LOCK_VIOLATION。</summary>
    private static bool IsLock(Exception ex) =>
        ex is IOException && (Marshal.GetHRForException(ex) & 0xFFFF) is 32 or 33;

    private static string Describe(Exception ex) => ex switch
    {
        UnauthorizedAccessException => "权限不足",
        IOException io => io.Message,
        _ => $"{ex.GetType().Name}: {ex.Message}",
    };
}
