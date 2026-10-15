using System.IO;
using DeleInfo.Core.Model;
using DeleInfo.Core.Util;

namespace DeleInfo.Core.Engine;

public sealed record RestoreOutcome(SnapshotEntry Entry, bool Ok, string? Error);

public sealed class RestoreEngine
{
    private readonly SnapshotStore _store;

    public RestoreEngine(SnapshotStore store) => _store = store;

    public bool HasSnapshot(string runId) => File.Exists(_store.ManifestPath(runId));

    public (int ok, int failed, List<RestoreOutcome> outcomes) Restore(string runId)
    {
        var manifest = SnapshotStore.LoadManifest(_store.ManifestPath(runId));
        if (manifest is null)
            return (0, 0, new List<RestoreOutcome>());

        var results = new List<RestoreOutcome>();
        foreach (var e in manifest.Entries.OrderByDescending(x => x.Original.Length))
        {
            try
            {
                if (!Fs.Exists(e.Bak))
                {
                    results.Add(new RestoreOutcome(e, false, "快照文件已不存在（可能已被 purge）"));
                    continue;
                }

                if (Fs.Exists(e.Original))
                {
                    results.Add(new RestoreOutcome(e, false, "原位置已被占用，未覆盖"));
                    continue;
                }

                var parent = Path.GetDirectoryName(e.Original);
                if (parent is not null)
                    Directory.CreateDirectory(parent);

                Fs.MoveInto(e.Bak, e.Original);
                results.Add(new RestoreOutcome(e, true, null));
            }
            catch (Exception ex)
            {
                results.Add(new RestoreOutcome(e, false, ex.Message));
            }
        }

        var ok = results.Count(r => r.Ok);
        var failed = results.Count - ok;
        if (failed == 0)
            TryFinishManifest(runId, manifest);

        return (ok, failed, results);
    }

    /// <summary>还原干净后把快照条目改名为 .restored，避免 purge 再动它们。</summary>
    private void TryFinishManifest(string runId, SnapshotManifest manifest)
    {
        try
        {
            var path = _store.ManifestPath(runId);
            File.Move(path, path + ".restored", overwrite: true);
        }
        catch
        {
            /* 不影响还原结果 */
        }
    }

    public (int deleted, long bytes, string? error) Purge(string runId)
    {
        var manifest = SnapshotStore.LoadManifest(_store.ManifestPath(runId));
        if (manifest is null)
            return (0, 0, $"找不到快照 {runId}");

        int deleted = 0;
        long bytes = 0;
        foreach (var e in manifest.Entries)
        {
            try
            {
                if (!Fs.Exists(e.Bak))
                    continue;
                Fs.FastDelete(e.Bak, 4);
                deleted++;
                bytes += e.Bytes;
            }
            catch
            {
                /* 单个失败不阻断 */
            }
        }

        if (deleted == manifest.Entries.Count)
        {
            try { File.Delete(_store.ManifestPath(runId)); } catch { /* ignore */ }
        }

        return (deleted, bytes, null);
    }
}
