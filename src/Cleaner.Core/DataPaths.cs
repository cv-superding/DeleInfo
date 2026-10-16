namespace DeleInfo.Core;

/// <summary>
/// 运行期数据的唯一出处：快照清单与清理流水都放这里。
///
/// 设 DELEINFO_HOME 可以整体改道——测试用它把副作用隔离到临时目录，
/// 从 U 盘上跑也可以让数据跟着 exe 走，而不是写进别人的用户目录。
/// </summary>
public static class DataPaths
{
    public const string HomeVariable = "DELEINFO_HOME";

    public static string Root
    {
        get
        {
            var fromEnv = Environment.GetEnvironmentVariable(HomeVariable);
            if (!string.IsNullOrWhiteSpace(fromEnv))
                return fromEnv;

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeleInfo");
        }
    }

    public static string Snapshots => Path.Combine(Root, "snapshots");

    public static string History => Path.Combine(Root, "history");
}
