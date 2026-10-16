namespace DeleInfo.Core.Model;

/// <summary>清理强度分级。Session = 登出；Credential = 抹掉保存的凭据。</summary>
public enum TraceLevel
{
    Session = 1,
    Credential = 2,
}

public enum TargetKind
{
    Directory,
    File,
    /// <summary>非文件系统的目标，例如 Windows 凭据管理器条目。</summary>
    External,
}

/// <summary>一个可被清理的具体对象：某应用某 profile 下的一处登录态。</summary>
public sealed class CleanTarget
{
    public required string CleanerId { get; init; }
    public required string App { get; init; }
    public required string Profile { get; init; }
    public required string Item { get; init; }
    public required string Path { get; init; }
    public TraceLevel Level { get; init; }
    public TargetKind Kind { get; init; } = TargetKind.Directory;

    /// <summary>删除前是否需要可回滚快照。缓存类大目录为 false（无价值且拖慢速度）。</summary>
    public bool NeedsSnapshot { get; init; }

    /// <summary>
    /// 需要显式确认才处理（启发式命中的原生文件、域凭据）。默认不勾选。
    /// 注意：陌生应用不算低置信——Cookies 就是 Cookies，跟厂商有不出名无关。
    /// </summary>
    public bool LowConfidence { get; init; }

    /// <summary>厂商不在已知清单里，仅用于标注，不影响是否勾选。</summary>
    public bool UnknownHost { get; init; }

    /// <summary>宿主应用此刻正在运行；此时任何删除都会撞上文件锁。</summary>
    public bool HostRunning { get; init; }

    public string? ProcessHint { get; init; }

    /// <summary>非文件系统目标的附加信息，例如凭据类型编号。</summary>
    public string? Tag { get; init; }

    public long Bytes { get; set; }
    public int FileCount { get; set; }
    public bool Exists { get; set; }
    public bool Locked { get; set; }
    public string? Error { get; set; }
    public bool Selected { get; set; } = true;

    public string DisplayPath => Kind == TargetKind.External ? Path : Path;
}

/// <summary>一轮扫描的产物。</summary>
public sealed class ScanResult
{
    public required IReadOnlyList<CleanTarget> Targets { get; init; }
    public required TimeSpan Elapsed { get; init; }
    public required IReadOnlyList<string> Notes { get; init; }

    public long TotalBytes => Targets.Where(t => t.Selected).Sum(t => t.Bytes);
    public int SelectedCount => Targets.Count(t => t.Selected);
}

/// <summary>一轮清理的产物。</summary>
public sealed class CleanResult
{
    public required string RunId { get; init; }
    public required IReadOnlyList<TargetOutcome> Outcomes { get; init; }
    public required TimeSpan Elapsed { get; init; }

    /// <summary>用户中途取消：Outcomes 里是已完成的部分，未处理的项保持原样。</summary>
    public bool Cancelled { get; init; }

    public long FreedBytes => Outcomes.Where(o => o.Status == OutcomeStatus.Removed || o.Status == OutcomeStatus.Snapshotted).Sum(o => o.Bytes);
    public int Succeeded => Outcomes.Count(o => o.Status is OutcomeStatus.Removed or OutcomeStatus.Snapshotted);
    public int Failed => Outcomes.Count(o => o.Status == OutcomeStatus.Failed);
    public int Locked => Outcomes.Count(o => o.Status == OutcomeStatus.Locked);
}

public enum OutcomeStatus
{
    Removed,
    /// <summary>已移入快照、可通过 restore 还原，尚未物理擦除。</summary>
    Snapshotted,
    Locked,
    Failed,
    Skipped,
}

public sealed record TargetOutcome(
    CleanTarget Target,
    OutcomeStatus Status,
    long Bytes,
    string? SnapshotRef,
    string? Error);
