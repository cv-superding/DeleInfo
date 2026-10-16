using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeleInfo.Core.Model;

namespace DeleInfo.Core.Engine;

public sealed record RunRecord(
    [property: JsonPropertyName("runId")] string RunId,
    [property: JsonPropertyName("at")] DateTime At,
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("forced")] bool Forced,
    [property: JsonPropertyName("items")] int Items,
    [property: JsonPropertyName("removed")] int Removed,
    [property: JsonPropertyName("snapshotted")] int Snapshotted,
    [property: JsonPropertyName("locked")] int Locked,
    [property: JsonPropertyName("failed")] int Failed,
    [property: JsonPropertyName("bytes")] long Bytes,
    [property: JsonPropertyName("elapsedMs")] double ElapsedMs,
    [property: JsonPropertyName("apps")] string[] Apps);

/// <summary>
/// 每轮清理都追加到 %LOCALAPPDATA%\DeleInfo\history\runs.jsonl。
/// 用途不是统计，而是回答"上次到底清了什么、还剩多少在快照里没擦"——
/// 一个可回滚的清理工具如果不自记流水，用户就没法知道隐私目标完成了没有。
/// </summary>
public static class RunHistory
{
    public static string HistoryDir => DataPaths.History;

    private static string FilePath => Path.Combine(HistoryDir, "runs.jsonl");

    public static void Record(CleanResult result, CleanSettings settings, IReadOnlyList<CleanTarget> targets)
    {
        try
        {
            Directory.CreateDirectory(HistoryDir);
            var record = new RunRecord(
                result.RunId,
                DateTime.Now,
                settings.Reversible ? "快照可还原" : "直接物理删除",
                settings.Force,
                targets.Count,
                result.Outcomes.Count(o => o.Status == OutcomeStatus.Removed),
                result.Outcomes.Count(o => o.Status == OutcomeStatus.Snapshotted),
                result.Outcomes.Count(o => o.Status == OutcomeStatus.Locked),
                result.Outcomes.Count(o => o.Status == OutcomeStatus.Failed),
                result.FreedBytes,
                result.Elapsed.TotalMilliseconds,
                targets.Select(t => t.App).Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(a => a, StringComparer.OrdinalIgnoreCase).Take(60).ToArray());

            File.AppendAllText(FilePath, JsonSerializer.Serialize(record) + "\n");
        }
        catch
        {
            /* 记不上历史不能影响已经执行的清理 */
        }
    }

    public static IReadOnlyList<RunRecord> Load(int take = 30)
    {
        try
        {
            if (!File.Exists(FilePath))
                return Array.Empty<RunRecord>();

            var lines = File.ReadAllLines(FilePath);
            var list = new List<RunRecord>();
            for (var i = Math.Max(0, lines.Length - take); i < lines.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i]))
                    continue;
                try
                {
                    var parsed = JsonSerializer.Deserialize<RunRecord>(lines[i]);
                    if (parsed is not null)
                        list.Add(parsed);
                }
                catch
                {
                    /* 跳过写坏的一行 */
                }
            }

            list.Reverse();
            return list;
        }
        catch
        {
            return Array.Empty<RunRecord>();
        }
    }
}
