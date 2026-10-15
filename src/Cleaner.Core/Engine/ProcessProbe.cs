using System.Diagnostics;

namespace DeleInfo.Core.Engine;

/// <summary>
/// 进程占用探测。以"锁文件能否独占打开"为主判据，进程名匹配只作为补充提示——
/// 猜进程名对国产壳浏览器基本不可靠（同一内核可有 N 个可执行文件名）。
/// </summary>
public static class ProcessProbe
{
    private static Lazy<HashSet<string>> RunningNames = new(() =>
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    set.Add(p.ProcessName);
                }
                catch
                {
                    /* 进程已退出 */
                }
            }
        }
        catch
        {
            /* ignore */
        }
        return set;
    });

    public static bool IsRunning(string processName) =>
        !string.IsNullOrWhiteSpace(processName) && RunningNames.Value.Contains(processName);

    public static void Invalidate() => RunningNames = new Lazy<HashSet<string>>(() => new HashSet<string>(StringComparer.OrdinalIgnoreCase));
}
