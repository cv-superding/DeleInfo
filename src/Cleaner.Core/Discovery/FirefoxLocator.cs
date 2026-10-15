using System.IO;

namespace DeleInfo.Core.Discovery;

public sealed record FirefoxProfile(string ProfileDir, string ProfileName, bool Running);

/// <summary>
/// Firefox 用 profiles.ini 记录 profile，路径相对 %APPDATA%\Mozilla\Firefox。
/// 同时覆盖其他基于 Firefox 源码的变体目录（Waterfox / Basilisk / Thunderbird 不含登录态，忽略）。
/// </summary>
public static class FirefoxLocator
{
    public static IReadOnlyList<FirefoxProfile> Discover()
    {
        var list = new List<FirefoxProfile>();
        var appdata = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrEmpty(appdata))
            return list;

        foreach (var vendor in new[] { "Mozilla", "Waterfox", "Basilisk" })
        {
            var profilesRoot = Path.Combine(appdata, vendor, "Firefox", "Profiles");
            if (!Directory.Exists(profilesRoot))
                continue;

            var names = ReadProfileNames(Path.Combine(appdata, vendor, "Firefox", "profiles.ini"));

            try
            {
                foreach (var dir in Directory.GetDirectories(profilesRoot))
                {
                    if (!Directory.Exists(dir))
                        continue;
                    var name = Path.GetFileName(dir);
                    if (names.TryGetValue(name, out var friendly))
                        name = $"{vendor}/{friendly}";
                    else
                        name = $"{vendor}/{name}";

                    list.Add(new FirefoxProfile(dir, name, IsRunning(dir)));
                }
            }
            catch
            {
                /* ignore */
            }
        }

        return list;
    }

    private static Dictionary<string, string> ReadProfileNames(string iniPath)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(iniPath))
            return map;

        try
        {
            foreach (var line in File.ReadLines(iniPath))
            {
                var idx = line.IndexOf('=');
                if (idx <= 0)
                    continue;
                var key = line[..idx].Trim();
                var val = line[(idx + 1)..].Trim();
                if (key.Equals("Path", StringComparison.OrdinalIgnoreCase))
                {
                    var leaf = Path.GetFileName(val.TrimEnd('\\', '/'));
                    if (leaf.Length > 0)
                        map[leaf] = leaf;
                }
            }
        }
        catch
        {
            /* ignore */
        }
        return map;
    }

    private static bool IsRunning(string profileDir)
    {
        var lockFile = Path.Combine(profileDir, ".parentlock");
        if (File.Exists(lockFile))
            return true;
        var legacy = Path.Combine(profileDir, "lock");
        return Directory.Exists(legacy);
    }
}
