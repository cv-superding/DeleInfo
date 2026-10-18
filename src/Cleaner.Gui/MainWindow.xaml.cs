using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using DeleInfo.Core;
using DeleInfo.Core.Cleaners;
using DeleInfo.Core.Engine;
using DeleInfo.Core.Model;
using DeleInfo.Core.Util;
using TraceLevel = DeleInfo.Core.Model.TraceLevel;

namespace DeleInfo.Gui;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<AppGroup> _groups = new();
    private readonly SnapshotStore _store = new();
    private string? _lastRunId;
    private CancellationTokenSource? _cts;

    public MainWindow()
    {
        InitializeComponent();
        Groups.ItemsSource = _groups;
        ChkRunning.Checked += (_, _) => ApplyRunningFilter();
        ChkRunning.Unchecked += (_, _) => ApplyRunningFilter();
        ThemeManager.ApplyTitleBar(this);
        BtnTheme.Content = ThemeManager.IsDark ? "浅色模式" : "深色模式";
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
        // 默认焦点会落到第一个芯片上，那圈焦点环看着像画歪了；交给主操作按钮更说得通。
        // 用 Activated 而不是 ContentRendered：窗口没被真正激活时 Focus() 会静默失败。
        Activated += (_, _) => BtnScan.Focus();

        Log($"快照目录：{_store.Root}");
        UpdateStats(null);
        RefreshSnapshotButtons();
        SelfCaptureIfRequested();
    }

    private void Log(string message)
    {
        LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\n");
        LogBox.ScrollToEnd();
    }

    private void SetBusy(bool busy, int total = 0)
    {
        BtnScan.IsEnabled = !busy;
        BtnClean.IsEnabled = !busy && _groups.SelectMany(g => g.Rows).Any(r => r.Selected && !r.Done);
        BtnHard.IsEnabled = BtnClean.IsEnabled;
        BtnCancel.IsEnabled = busy;
        Progress.Maximum = Math.Max(1, total);
        Progress.Value = 0;
    }

    private void OnToggleTheme(object sender, RoutedEventArgs e)
    {
        ThemeManager.Toggle();
        BtnTheme.Content = ThemeManager.IsDark ? "浅色模式" : "深色模式";
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        BtnCancel.IsEnabled = false;
        Log("正在取消：已经开始处理的项目会完成，剩下的不动。");
    }

    private void OnHistory(object sender, RoutedEventArgs e)
    {
        var runs = RunHistory.Load(20);
        if (runs.Count == 0)
        {
            Log("还没有清理记录。");
            return;
        }

        LogPanel.IsExpanded = true;
        Log($"最近 {runs.Count} 轮清理：");
        foreach (var r in runs)
        {
            Log($"  {r.At:yyyy-MM-dd HH:mm:ss}  {r.RunId}  {r.Mode}，共 {r.Items} 项：" +
                $"擦除 {r.Removed}、快照 {r.Snapshotted}、占用跳过 {r.Locked}、失败 {r.Failed}，{Fs.Human(r.Bytes)}");
            if (r.Snapshotted > 0)
                Log($"      ⚠ 该轮有 {r.Snapshotted} 项进了快照，未 purge 前令牌仍在磁盘上");
        }
    }

    // ===================== 扫描 =====================

    private async void OnScan(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        _groups.Clear();

        var options = new ScanOptions
        {
            MaxLevel = ChkLevel2.IsChecked == true ? TraceLevel.Credential : TraceLevel.Session,
            IncludeLowConfidence = ChkHeuristic.IsChecked == true,
        };

        try
        {
            var sw = Stopwatch.StartNew();
            var result = await Task.Run(() => new ScanEngine().Scan(CleanerRegistry.All, options));
            sw.Stop();

            foreach (var group in BuildGroups(result.Targets))
                _groups.Add(group);

            ApplyRunningFilter();
            foreach (var note in result.Notes)
                Log("! " + note);

            UpdateStats(result, sw.Elapsed);
            EmptyHint.Visibility = _groups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            Log("扫描失败：" + ex.Message);
            ReportError(ex);
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>
    /// 异常除了写进（可能被折叠的）界面日志，也落一份到 exe 旁边。
    /// 只往面板里写的话，用户看不到、也没法反馈到底崩在哪。
    /// </summary>
    private void ReportError(Exception ex)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(AppContext.BaseDirectory, "deleinfo-gui-error.log"),
                $"{DateTime.Now:O} {ex}\n\n");
        }
        catch
        {
            /* 日志写不了也不能再抛 */
        }
    }

    private static IEnumerable<AppGroup> BuildGroups(IReadOnlyList<CleanTarget> targets) =>
        targets.GroupBy(t => t.App, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Sum(t => t.Bytes))
            .Select(g =>
            {
                var group = new AppGroup(g.Key, KindOf(g.First().CleanerId));
                foreach (var target in g.OrderByDescending(t => t.Bytes))
                    group.Rows.Add(new RowItem(target));
                group.RefreshFromRows();
                return group;
            });

    private static string KindOf(string cleanerId) => cleanerId switch
    {
        "chromium.browser" or "firefox" => "浏览器",
        "chromium.electron" or "app.native" => "桌面应用",
        "dev.credentials" => "凭据文件",
        "win.credman" => "系统凭据",
        _ => "其他",
    };

    private void ApplyRunningFilter()
    {
        if (ChkRunning.IsChecked == true)
            return;
        foreach (var row in _groups.SelectMany(g => g.Rows).Where(r => r.Target.HostRunning && !r.Done))
        {
            row.Selected = false;
            row.Status = "运行中，已排除";
        }
    }

    private void UpdateStats(ScanResult? scan, TimeSpan? elapsed = null)
    {
        var rows = _groups.SelectMany(g => g.Rows).ToList();
        var selected = rows.Where(r => r.Selected && !r.Done).ToList();

        StatSize.Text = selected.Count == 0 ? "—" : Fs.Human(selected.Sum(r => r.Target.Bytes));
        StatCount.Text = selected.Count == 0 ? "—" : selected.Count.ToString("N0");
        StatApps.Text = _groups.Count == 0 ? "—" :
            _groups.Count(g => g.Rows.Any(r => r.Selected && !r.Done)).ToString("N0");

        var running = rows.Count(r => r.Target.HostRunning && !r.Done);
        if (scan is null)
        {
            SummaryText.Text = "尚未扫描。点上方「扫描」开始。";
            return;
        }

        var seconds = elapsed ?? scan.Elapsed;
        SummaryText.Text = $"共 {rows.Count} 项 / {Fs.Human(rows.Sum(r => r.Target.Bytes))}，" +
                           $"耗时 {seconds.TotalSeconds:0.##}s" +
                           (running > 0 ? $"；{running} 项因宿主运行中已排除。" : "。");
    }

    // ===================== 清理 =====================

    private async void OnClean(object sender, RoutedEventArgs e) => await RunClean(reversible: true);

    private async void OnHard(object sender, RoutedEventArgs e) => await RunClean(reversible: false);

    private async Task RunClean(bool reversible)
    {
        var rows = _groups.SelectMany(g => g.Rows).ToList();
        foreach (var row in rows)
            row.Target.Selected = row.Selected && !row.Done;

        var targets = rows.Where(r => r.Selected && !r.Done).Select(r => r.Target).ToList();
        if (targets.Count == 0)
        {
            MessageBox.Show(this, "没有勾选任何项目。", "DeleInfo");
            return;
        }

        var locked = targets.Count(t => t.Locked);
        var confirm = MessageBox.Show(
            this,
            $"将清理 {targets.Count} 项 / {Fs.Human(targets.Sum(t => t.Bytes))}。\n" +
            (reversible
                ? "关键项先改名进快照：瞬间完成、可还原，但令牌此刻仍在磁盘上，需再执行「清空所有快照」才算真正擦除。"
                : "不生成快照，直接物理删除，不可还原。") +
            $"\n其中 {locked} 项的宿主正在运行{(ChkRunning.IsChecked == true ? "（已选择强删）" : "（将被跳过）")}。\n\n" +
            "书签、扩展、浏览历史与应用聊天记录不在范围内。确认继续？",
            "确认清理", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes)
            return;

        SetBusy(true, targets.Count);
        _cts = new CancellationTokenSource();
        var settings = new CleanSettings
        {
            Reversible = reversible,
            Force = ChkRunning.IsChecked == true,
        };

        try
        {
            var progress = new Progress<int>(n => Progress.Value = n);
            var result = await Task.Run(
                () => new CleanEngine(CleanerRegistry.All).Execute(targets, settings, progress, _cts.Token),
                _cts.Token);
            _lastRunId = result.RunId;

            var byPath = new Dictionary<string, RowItem>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in rows)
                byPath[row.Target.Path] = row;

            foreach (var outcome in result.Outcomes)
            {
                if (byPath.TryGetValue(outcome.Target.Path, out var row))
                    row.MarkDone(Describe(outcome.Status));
                if (outcome.Error is not null)
                    Log($"  {outcome.Target.App} / {outcome.Target.Item} — {outcome.Error}");
            }

            SummaryText.Text = $"本轮完成 {result.Succeeded} 项（{Fs.Human(result.FreedBytes)}），" +
                               $"占用跳过 {result.Locked}，失败 {result.Failed}，耗时 {result.Elapsed.TotalSeconds:0.##}s。";
            Log(SummaryText.Text);
            if (result.Cancelled)
                Log($"已取消：{targets.Count - result.Outcomes.Count} 项完全没动过，重新扫描后仍可清理。");
            if (result.Outcomes.Any(o => o.Status == OutcomeStatus.Snapshotted))
                Log($"快照号 {result.RunId}：可点「还原上次快照」回滚；确认可用后用「清空所有快照」完成最终擦除。");

            foreach (var group in _groups)
                group.RefreshFromRows();
            UpdateStats(null);
            RefreshSnapshotButtons();
        }
        catch (Exception ex)
        {
            Log("清理失败：" + ex.Message);
            ReportError(ex);
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            SetBusy(false);
        }
    }

    private static string Describe(OutcomeStatus status) => status switch
    {
        OutcomeStatus.Removed => "已擦除",
        OutcomeStatus.Snapshotted => "已进快照",
        OutcomeStatus.Locked => "占用，跳过",
        OutcomeStatus.Failed => "失败",
        _ => "已不存在",
    };

    // ===================== 快照 =====================

    private async void OnRestore(object sender, RoutedEventArgs e)
    {
        var runId = _lastRunId ?? _store.List().FirstOrDefault()?.RunId;
        if (runId is null)
        {
            MessageBox.Show(this, "没有可还原的快照。", "DeleInfo");
            return;
        }

        var engine = new RestoreEngine(_store);
        if (!engine.HasSnapshot(runId))
        {
            MessageBox.Show(this, $"找不到快照 {runId}。", "DeleInfo");
            return;
        }

        if (MessageBox.Show(this, $"把快照 {runId} 的登录态放回原位？", "确认还原",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        SetBusy(true);
        var (ok, failed, outcomes) = await Task.Run(() => engine.Restore(runId));
        foreach (var o in outcomes.Where(o => !o.Ok))
            Log($"  还原失败 {o.Entry.Original} — {o.Error}");
        Log($"还原完成：成功 {ok}，失败 {failed}。重新扫描即可看到它们回来。");
        _lastRunId = null;
        RefreshSnapshotButtons();
        SetBusy(false);
    }

    private async void OnPurge(object sender, RoutedEventArgs e)
    {
        var list = _store.List();
        if (list.Count == 0)
        {
            MessageBox.Show(this, "没有快照。", "DeleInfo");
            return;
        }

        if (MessageBox.Show(this,
                $"物理擦除 {list.Count} 轮快照，共 {list.Sum(s => s.Count)} 项 / {Fs.Human(list.Sum(s => s.Bytes))}。\n" +
                "执行后无法再还原，且这才是真正完成隐私目标的一步。确认？",
                "确认彻底删除", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        SetBusy(true);
        var engine = new RestoreEngine(_store);
        foreach (var snapshot in list)
        {
            var (deleted, bytes, error) = await Task.Run(() => engine.Purge(snapshot.RunId));
            Log(error ?? $"快照 {snapshot.RunId}：擦除 {deleted} 项 / {Fs.Human(bytes)}");
        }
        _lastRunId = null;
        RefreshSnapshotButtons();
        SetBusy(false);
    }

    private void OnOpenSnapshots(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_store.Root);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_store.Root}\"") { UseShellExecute = true });
    }

    private void RefreshSnapshotButtons()
    {
        var any = _store.List().Count > 0;
        BtnRestore.IsEnabled = any;
        BtnPurge.IsEnabled = any;
    }

    // ===================== 选择 =====================

    private void OnSelectAll(object sender, RoutedEventArgs e) => SetSelection(r => !r.Done, "已全选");

    private void OnSelectNone(object sender, RoutedEventArgs e) => SetSelection(_ => false, "已全不选");

    private void OnSelectL1(object sender, RoutedEventArgs e) =>
        SetSelection(r => r.Target.Level == TraceLevel.Session && !r.Done, "已只勾选 L1 登出项");

    private void SetSelection(Func<RowItem, bool> predicate, string message)
    {
        foreach (var row in _groups.SelectMany(g => g.Rows))
            row.Selected = predicate(row);
        foreach (var group in _groups)
            group.RefreshFromRows();
        UpdateStats(null);
        Log(message);
    }

    // ===================== UI 自检 =====================

    /// <summary>
    /// 设 DELEINFO_SHOT=&lt;png&gt; 时由窗口自己渲染成图片。
    /// 屏幕截图会被其它置顶窗口挡住，PrintWindow 对 WPF 的硬件合成分离区只能截到全白，
    /// RenderTargetBitmap 拿的是窗口自身像素。正常运行不设这个变量。
    /// </summary>
    private void SelfCaptureIfRequested()
    {
        var path = Environment.GetEnvironmentVariable("DELEINFO_SHOT");
        if (string.IsNullOrEmpty(path))
            return;

        var delay = int.TryParse(Environment.GetEnvironmentVariable("DELEINFO_SHOT_DELAY"), out var d) ? d : 0;
        if (delay > 0)
        {
            // 指定了延迟就只按延迟拍一次，否则即时那张空状态图会先落盘，
            // 让外部脚本误以为已经拿到"扫描后"的界面。
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(delay) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                CaptureTo(path);
            };
            timer.Start();
            return;
        }

        Dispatcher.BeginInvoke(() => CaptureTo(path), DispatcherPriority.ApplicationIdle);
    }

    private void CaptureTo(string path)
    {
        try
        {
            UpdateLayout();
            var target = (Visual)this.Content ?? this;
            var frame = new System.Windows.Media.Imaging.RenderTargetBitmap(
                (int)Math.Max(1, ActualWidth), (int)Math.Max(1, ActualHeight),
                96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            frame.Render(target);

            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(frame));
            using var stream = File.Create(path);
            encoder.Save(stream);
        }
        catch (Exception ex)
        {
            // 自检失败要留下痕迹，否则"没截图"会被误读成"界面是空的"。
            try { File.WriteAllText(path + ".error", ex.ToString()); } catch { /* ignore */ }
        }
    }
}
