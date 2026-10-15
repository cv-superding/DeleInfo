using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeleInfo.Core.Model;
using DeleInfo.Core.Util;

namespace DeleInfo.Core.Engine;

public sealed record SnapshotEntry(
    [property: JsonPropertyName("original")] string Original,
    [property: JsonPropertyName("bak")] string Bak,
    [property: JsonPropertyName("dir")] bool IsDirectory,
    [property: JsonPropertyName("bytes")] long Bytes,
    [property: JsonPropertyName("app")] string App,
    [property: JsonPropertyName("item")] string Item);

public sealed record SnapshotManifest(
    [property: JsonPropertyName("runId")] string RunId,
    [property: JsonPropertyName("createdAt")] DateTime CreatedAt,
    [property: JsonPropertyName("entries")] List<SnapshotEntry> Entries);

public sealed record SnapshotInfo(string RunId, DateTime CreatedAt, int Count, long Bytes, string ManifestPath);

/// <summary>
/// 快照实现方式：同目录改名，而不是复制到快照文件夹。
/// 原因是改名在同一卷上是 O(1) 的元数据操作，与数据量无关——GB 级 IndexedDB 也能"瞬间备份"；
/// 同时路径长度不变，不会把深层 profile 路径推过 MAX_PATH。
/// </summary>
public sealed class SnapshotStore
{
    /// <summary>快照改名标记：&lt;原名&gt;__dele_&lt;运行号&gt;。</summary>
    public const string Suffix = "__dele";

    /// <summary>路径里出现这个片段就说明它是本工具的快照备份，不是活的登录态。</summary>
    public const string ArtifactMarker = "__dele_";
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public string Root { get; }

    public SnapshotStore(string? root = null)
    {
        // 清单必须落在与 exe 位置无关的稳定目录，否则从 U 盘/另一个副本运行就找不到快照、
        // 既不能还原也不能 purge——而那些令牌还原地躺在磁盘上。
        Root = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DeleInfo", "snapshots");
        Directory.CreateDirectory(Root);
    }

    public string Take(CleanTarget target, string runId)
    {
        var parent = Path.GetDirectoryName(target.Path.TrimEnd(Path.DirectorySeparatorChar))
                     ?? throw new IOException($"无法定位父目录: {target.Path}");
        var leaf = Path.GetFileName(target.Path.TrimEnd(Path.DirectorySeparatorChar));
        // runId 形如 20261015-210110-a697f8；只取日期会让同一天的两轮清理撞名。
        var dash = runId.IndexOf('-');
        var token = dash > 0 ? runId[(dash + 1)..].Replace("-", "") : runId;
        var bak = Path.Combine(parent, $"{leaf}{Suffix}_{token}");

        Fs.MoveInto(target.Path, bak);
        return bak;
    }

    public void WriteManifest(string runId, IEnumerable<SnapshotEntry> entries, DateTime createdAt)
    {
        var list = entries.ToList();
        var manifest = new SnapshotManifest(runId, createdAt, list);
        File.WriteAllText(ManifestPath(runId), JsonSerializer.Serialize(manifest, JsonOpts));
    }

    public string ManifestPath(string runId) => Path.Combine(Root, $"{runId}.json");

    public IReadOnlyList<SnapshotInfo> List()
    {
        var list = new List<SnapshotInfo>();
        if (!Directory.Exists(Root))
            return list;

        foreach (var file in Directory.EnumerateFiles(Root, "*.json"))
        {
            try
            {
                var m = JsonSerializer.Deserialize<SnapshotManifest>(File.ReadAllText(file));
                if (m is null)
                    continue;
                list.Add(new SnapshotInfo(
                    m.RunId, m.CreatedAt, m.Entries.Count,
                    m.Entries.Sum(e => e.Bytes), file));
            }
            catch
            {
                /* 忽略损坏的清单 */
            }
        }

        return list.OrderByDescending(s => s.CreatedAt).ToList();
    }

    public static SnapshotManifest? LoadManifest(string manifestPath)
    {
        try
        {
            return JsonSerializer.Deserialize<SnapshotManifest>(File.ReadAllText(manifestPath));
        }
        catch
        {
            return null;
        }
    }
}
