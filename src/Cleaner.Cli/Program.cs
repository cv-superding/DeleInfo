using System.Text;
using System.Text.Json;
using DeleInfo.Core;
using DeleInfo.Core.Cleaners;
using DeleInfo.Core.Discovery;
using DeleInfo.Core.Engine;
using DeleInfo.Core.Model;
using DeleInfo.Core.Native;
using DeleInfo.Core.Util;

TrySetConsole();

var opts = Opts.Parse(args);
if (opts.Command is "help" or "--help" or "-h" or null)
{
    PrintHelp();
    return 0;
}

var cleaners = CleanerRegistry.All;

switch (opts.Command)
{
    case "scan":
        RenderScan(RunScan(opts), opts);
        return 0;

    case "clean":
        return RunClean(RunScan(opts), opts);

    case "hosts":
        RenderHosts(opts);
        return 0;

    case "creds":
        return RenderCreds(opts);

    case "snapshots":
        RenderSnapshots(opts);
        return 0;

    case "restore":
        return RunRestore(opts);

    case "purge":
        return RunPurge(opts);

    default:
        Console.Error.WriteLine($"未知命令：{opts.Command}");
        PrintHelp();
        return 2;
}

static ScanResult RunScan(Opts opts) =>
    new ScanEngine { MaxParallelism = opts.Workers }.Scan(CleanerRegistry.All, new ScanOptions
    {
        MaxLevel = opts.Level,
        IncludeLowConfidence = opts.Has("include-low"),
        Only = opts.Value("only"),
        Except = opts.Value("except"),
    });

static void TrySetConsole()
{
    try
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.InputEncoding = Encoding.UTF8;
    }
    catch
    {
        /* 重定向到文件时设置编码会失败，无碍 */
    }
}

static int RunClean(ScanResult scan, Opts opts)
{
    var selected = scan.Targets.Where(t => t.Selected).ToList();
    if (selected.Count == 0)
    {
        Console.WriteLine("没有可清理的登录态（或全部被 --only/--except 排除）。");
        return 0;
    }

    if (!opts.Has("yes"))
    {
        RenderScan(scan, opts);
        Console.WriteLine();
        Console.WriteLine("这是预览，未删除任何文件。确认无误后加 --yes 执行。");
        return 1;
    }

    var hard = opts.Has("hard");
    var settings = new CleanSettings
    {
        Reversible = !hard,
        Force = opts.Has("force"),
        SnapshotRoot = opts.Value("snapshot-dir"),
        MaxParallelism = opts.Workers,
    };

    var lockedCount = selected.Count(t => t.Locked && !settings.Force);
    if (lockedCount > 0)
        Console.WriteLine($"提示：{lockedCount} 项因宿主正在运行会被跳过。关闭对应程序后重跑，或加 --force 强删。");

    Console.WriteLine($"开始清理 {selected.Count} 项，模式：{(hard ? "直接物理删除（不可还原）" : "快照改名（可还原）")}，并发 {settings.MaxParallelism}");
    var result = new CleanEngine(cleaners: CleanerRegistry.All).Execute(selected, settings);

    RenderClean(result, opts);
    return result.Failed > 0 ? 3 : 0;
}

static void RenderClean(CleanResult result, Opts opts)
{
    Console.WriteLine();
    Console.WriteLine($"完成：{result.Succeeded} 项，失败 {result.Failed}，占用跳过 {result.Locked}，耗时 {result.Elapsed.TotalSeconds:0.##}s");

    var snapshotted = result.Outcomes.Where(o => o.Status == OutcomeStatus.Snapshotted).ToList();
    var removed = result.Outcomes.Where(o => o.Status == OutcomeStatus.Removed).ToList();

    Console.WriteLine($"  已物理擦除 : {removed.Count} 项 / {Fs.Human(removed.Sum(o => o.Bytes))}");
    Console.WriteLine($"  进入快照   : {snapshotted.Count} 项 / {Fs.Human(snapshotted.Sum(o => o.Bytes))}");

    if (snapshotted.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine($"快照运行号：{result.RunId}");
        Console.WriteLine($"  还原：deleinfo restore {result.RunId}");
        Console.WriteLine($"  彻底删除快照内容（完成隐私目标）：deleinfo purge {result.RunId}");
        Console.WriteLine("  注意：快照里的登录令牌此刻仍完整存在于磁盘，只是浏览器读不到了。");
        Console.WriteLine("        确认不需要回滚后，务必执行 purge。");
    }

    var locked = result.Outcomes.Where(o => o.Status == OutcomeStatus.Locked).ToList();
    if (locked.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine("以下项目因文件被占用未处理：");
        foreach (var o in locked.Take(20))
            Console.WriteLine($"  [{o.Target.App}] {o.Target.Item} — {o.Error}");
        if (locked.Count > 20)
            Console.WriteLine($"  ... 另有 {locked.Count - 20} 项");
    }

    var failed = result.Outcomes.Where(o => o.Status == OutcomeStatus.Failed).ToList();
    if (failed.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine("失败项：");
        foreach (var o in failed.Take(20))
            Console.WriteLine($"  [{o.Target.App}] {o.Target.Item} — {o.Error}");
    }
}

static int RenderCreds(Opts opts)
{
    var all = !opts.Has("generic-only");
    var entries = CredApi.Enumerate(all);
    Console.WriteLine($"凭据条目 {entries.Count} 项（{(all ? "全部类型" : "仅本用户交互式凭据")}）：");
    foreach (var e in entries.OrderBy(x => x.TargetName, StringComparer.OrdinalIgnoreCase))
    {
        var protectedOne = WindowsCredentialCleaner.IsProtected(e.TargetName);
        Console.WriteLine($"  {(protectedOne ? "[受保护] " : "          ")}{e.TypeLabel,-12} {Pad(e.TargetName, 44)} {e.UserName ?? "-"}  {e.PersistLabel}");
    }
    Console.WriteLine();
    Console.WriteLine("注：受保护条目属于系统登录会话，永不列入清理范围。凭据删除不可还原。");
    return 0;
}

static void RenderScan(ScanResult scan, Opts opts)
{
    if (opts.Has("json"))
    {
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            elapsedMs = scan.Elapsed.TotalMilliseconds,
            totalBytes = scan.TotalBytes,
            selected = scan.SelectedCount,
            targets = scan.Targets.Select(t => new
            {
                app = t.App, profile = t.Profile, item = t.Item, level = (int)t.Level,
                path = t.Path, bytes = t.Bytes, files = t.FileCount,
                locked = t.Locked, selected = t.Selected, snapshot = t.NeedsSnapshot,
            }),
        }, new JsonSerializerOptions
        {
            WriteIndented = true,
            // 默认会把中文转义成 \uXXXX，机器可读但人读不了。
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }));
        return;
    }

    Console.WriteLine($"扫描耗时 {scan.Elapsed.TotalSeconds:0.##}s，发现 {scan.Targets.Count} 项，" +
                      $"默认勾选 {scan.SelectedCount} 项，合计 {Fs.Human(scan.TotalBytes)}");
    if (scan.Notes.Count > 0)
        foreach (var n in scan.Notes)
            Console.WriteLine("  ! " + n);

    var groups = scan.Targets.GroupBy(t => t.App).OrderByDescending(g => g.Sum(t => t.Bytes));
    foreach (var g in groups)
    {
        Console.WriteLine();
        Console.WriteLine($"{Pad(g.Key, 28)} {Fs.Human(g.Sum(t => t.Bytes)).PadLeft(11)}  {Mark(g.First())}");
        foreach (var t in g.OrderByDescending(x => x.Bytes).Take(opts.Top))
            Console.WriteLine($"    L{(int)t.Level} {Pad(t.Item, 30)} {Fs.Human(t.Bytes).PadLeft(11)} " +
                              $"{(t.Selected ? "" : "[未勾选] ")}{(t.Locked ? "[占用] " : "")}{Shorten(t.Path)}");
        if (g.Count() > opts.Top)
            Console.WriteLine($"    ... 另有 {g.Count() - opts.Top} 项");
    }
}

static void RenderHosts(Opts opts)
{
    Console.WriteLine("Chromium 系宿主：");
    foreach (var h in ChromiumLocator.Discover())
    {
        var state = h.Running ? "运行中 " : ChromiumLocator.HasLoginState(h.UserDataRoot) ? "有登录态" : "无标准登录态";
        Console.WriteLine($"  {(h.LowConfidence ? "陌生应用" : "      ")} {state} {h.DisplayName,-28} {h.UserDataRoot}");
    }

    Console.WriteLine();
    Console.WriteLine("Firefox 宿主：");
    var ff = FirefoxLocator.Discover();
    if (ff.Count == 0)
        Console.WriteLine("  （未发现）");
    foreach (var p in ff)
        Console.WriteLine($"  {(p.Running ? "运行中 " : "      ")}{p.ProfileName,-28} {p.ProfileDir}");
}

static void RenderSnapshots(Opts opts)
{
    var store = new SnapshotStore(opts.Value("snapshot-dir"));
    var list = store.List();
    if (list.Count == 0)
    {
        Console.WriteLine($"没有快照。目录：{store.Root}");
        return;
    }

    Console.WriteLine($"快照目录：{store.Root}");
    foreach (var s in list)
        Console.WriteLine($"  {s.RunId}  {s.CreatedAt:yyyy-MM-dd HH:mm:ss}  {s.Count} 项  {Fs.Human(s.Bytes)}");
}

static int RunRestore(Opts opts)
{
    var runId = opts.Arg;
    if (runId is null)
    {
        Console.Error.WriteLine("用法：deleinfo restore <runId>");
        return 2;
    }

    var store = new SnapshotStore(opts.Value("snapshot-dir"));
    var engine = new RestoreEngine(store);
    if (!engine.HasSnapshot(runId))
    {
        Console.Error.WriteLine($"找不到快照 {runId}（可能已全部还原或已 purge）。运行 deleinfo snapshots 查看。");
        return 2;
    }

    var (ok, failed, outcomes) = engine.Restore(runId);
    Console.WriteLine($"还原完成：成功 {ok}，失败 {failed}");
    foreach (var o in outcomes.Where(o => !o.Ok))
        Console.WriteLine($"  ! {o.Entry.Original} — {o.Error}");
    return failed > 0 ? 3 : 0;
}

static int RunPurge(Opts opts)
{
    var runId = opts.Arg;
    if (runId is null)
    {
        Console.Error.WriteLine("用法：deleinfo purge <runId>");
        return 2;
    }

    var store = new SnapshotStore(opts.Value("snapshot-dir"));
    var engine = new RestoreEngine(store);
    if (!engine.HasSnapshot(runId))
    {
        Console.Error.WriteLine($"找不到快照 {runId}。运行 deleinfo snapshots 查看。");
        return 2;
    }

    var (deleted, bytes, error) = engine.Purge(runId);
    if (error is not null)
    {
        Console.Error.WriteLine(error);
        return 2;
    }
    Console.WriteLine($"已彻底删除快照内容：{deleted} 项 / {Fs.Human(bytes)}");
    return 0;
}

static string Mark(CleanTarget t) =>
    t.LowConfidence ? "需确认：默认不勾选"
    : t.HostRunning ? "宿主运行中"
    : t.UnknownHost ? "陌生应用：照样可清"
    : "可清理";

static string Pad(string s, int width) =>
    s.Length >= width ? s[..Math.Min(width - 1, s.Length)] + "…" : s.PadRight(width);

static string Shorten(string path) =>
    path.Length <= 88 ? path : "…" + path[^85..];

static void PrintHelp()
{
    Console.WriteLine(@"deleinfo — Windows 10 登录态清理（L1 会话 + L2 凭据）

用法：
  deleinfo scan      [选项]              扫描并预览要删的东西（不删）
  deleinfo clean     [选项] --yes        执行清理
  deleinfo hosts     [选项]              只列发现的宿主应用
  deleinfo creds     [--generic-only]    列出 Windows 凭据管理器条目
  deleinfo snapshots [--snapshot-dir p]  列出快照
  deleinfo restore   <runId>             从快照还原
  deleinfo purge     <runId>             彻底删除快照内容（完成真正的擦除）

选项：
  --level 1|2        1=仅会话登出，2=连保存的凭据一起清（默认 2）
  --only <子串>      只处理应用名包含该子串的项目
  --except <子串>    排除应用名包含该子串的项目
  --include-low      连低置信宿主（未知 Electron 应用/域凭据）一起处理
  --hard             跳过快照直接物理删除：更快达成隐私目标，但不可还原
  --force            宿主正在运行时也强行尝试删除
  --snapshot-dir <p> 快照清单目录（默认 %LOCALAPPDATA%\DeleInfo\snapshots，与 exe 位置无关）
  --workers <n>      并发度（默认 CPU 核数，4~32）
  --top <n>          每个应用打印的条目数（默认 8）
  --json             机器可读输出

说明：
  * 书签、扩展、历史、下载记录不在任何清理范围内。
  * WebView2/CEF 内嵌宿主（EBWebView、webviewcache 等）永不清理，避免弄坏别人的应用。
  * Windows 凭据删除不可还原：本工具不读取也不转存密码密文。");
}

/// <summary>零依赖的手写参数解析（不引 NuGet，保证离线可构建、单文件体积小）。</summary>
internal sealed class Opts
{
    private readonly Dictionary<string, string> _kv = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _flags = new(StringComparer.OrdinalIgnoreCase);

    public string? Command { get; private set; }
    public string? Arg { get; private set; }

    public bool Has(string name) => _flags.Contains(name);
    public string? Value(string name) => _kv.TryGetValue(name, out var v) ? v : null;

    public TraceLevel Level => Value("level") == "1" ? TraceLevel.Session : TraceLevel.Credential;

    public int Workers
    {
        get
        {
            var n = int.TryParse(Value("workers"), out var w) ? w : Environment.ProcessorCount;
            return Math.Clamp(n, 1, 32);
        }
    }

    public int Top
    {
        get
        {
            var n = int.TryParse(Value("top"), out var t) ? t : 8;
            return Math.Clamp(n, 1, 500);
        }
    }

    public static Opts Parse(string[] args)
    {
        var o = new Opts();
        var valueNext = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "level", "only", "except", "snapshot-dir", "workers", "top" };

        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a.StartsWith("--"))
            {
                var name = a[2..];
                var eq = name.IndexOf('=');
                if (eq > 0)
                {
                    o._kv[name[..eq]] = name[(eq + 1)..];
                }
                else if (valueNext.Contains(name) && i + 1 < args.Length)
                {
                    o._kv[name] = args[++i];
                }
                else
                {
                    o._flags.Add(name);
                }
            }
            else if (o.Command is null)
            {
                o.Command = a;
            }
            else if (o.Arg is null)
            {
                o.Arg = a;
            }
        }

        return o;
    }
}

