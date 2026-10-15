using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using DeleInfo.Core;
using DeleInfo.Core.Cleaners;
using DeleInfo.Core.Engine;
using DeleInfo.Core.Model;
using DeleInfo.Core.Util;
using TraceLevel = DeleInfo.Core.Model.TraceLevel;

namespace DeleInfo.Gui;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<RowItem> _rows = new();
    private readonly SnapshotStore _store = new();
    private string? _lastRunId;

    public MainWindow()
    {
        InitializeComponent();
        Rows.ItemsSource = _rows;
        ChkRunning.Checked += (_, _) => ApplyRunningFilter();
        ChkRunning.Unchecked += (_, _) => ApplyRunningFilter();
        Log($"快照目录：{_store.Root}");
        Log("书签、扩展、浏览历史、应用聊天记录不在任何清理范围内。");
        RefreshSnapshotButtons();
    }

    private void Log(string message)
    {
        LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\n");
        LogBox.ScrollToEnd();
    }

    private void SetBusy(bool busy)
    {
        BtnScan.IsEnabled = !busy;
        BtnClean.IsEnabled = !busy && _rows.Any(r => r.Selected);
        BtnHard.IsEnabled = BtnClean.IsEnabled;
    }

    private async void OnScan(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        _rows.Clear();
        BtnClean.IsEnabled = BtnHard.IsEnabled = false;

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

            foreach (var target in result.Targets)
                _rows.Add(new RowItem(target));

            ApplyRunningFilter();

            foreach (var note in result.Notes)
                Log("! " + note);

            var selected = _rows.Where(r => r.Selected).ToList();
            SummaryText.Text =
                $"发现 {result.Targets.Count} 项，勾选 {selected.Count} 项，合计 {Fs.Human(selected.Sum(r => r.Target.Bytes))}；" +
                $"扫描耗时 {sw.Elapsed.TotalSeconds:0.##}s（内核 {result.Elapsed.TotalSeconds:0.##}s）。" +
                $"正在运行的应用有 {_rows.Count(r => r.Target.HostRunning)} 项。";
            Log(SummaryText.Text);
            BtnClean.IsEnabled = BtnHard.IsEnabled = selected.Count > 0;
        }
        catch (Exception ex)
        {
            Log("扫描失败：" + ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void ApplyRunningFilter()
    {
        if (ChkRunning.IsChecked == true)
            return;
        foreach (var row in _rows.Where(r => r.Target.HostRunning && !r.Done))
        {
            row.Selected = false;
            row.Status = "运行中，已排除";
        }
    }

    private async void OnClean(object sender, RoutedEventArgs e) => await RunClean(reversible: true);

    private async void OnHard(object sender, RoutedEventArgs e) => await RunClean(reversible: false);

    private async Task RunClean(bool reversible)
    {
        foreach (var row in _rows)
            row.Target.Selected = row.Selected;

        var targets = _rows.Where(r => r.Selected && !r.Done).Select(r => r.Target).ToList();
        if (targets.Count == 0)
        {
            MessageBox.Show(this, "没有勾选任何项目。", "DeleInfo");
            return;
        }

        var bytes = targets.Sum(t => t.Bytes);
        var mode = reversible
            ? "关键项先改名进快照（瞬间完成、可还原，但令牌此刻仍在磁盘上，需再执行\"清空所有快照\"才算真正擦除）。"
            : "不生成快照，直接物理删除，不可还原。";
        var locked = targets.Count(t => t.Locked);
        var confirm = MessageBox.Show(
            this,
            $"将清理 {targets.Count} 项 / {Fs.Human(bytes)}。\n{mode}\n" +
            $"其中 {locked} 项的宿主正在运行{(ChkRunning.IsChecked == true ? "（已选择强删）" : "（将被跳过）")}。\n\n" +
            "确认继续？",
            "确认清理", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes)
            return;

        SetBusy(true);
        var settings = new CleanSettings
        {
            Reversible = reversible,
            Force = ChkRunning.IsChecked == true,
        };

        try
        {
            var result = await Task.Run(() => new CleanEngine(CleanerRegistry.All).Execute(targets, settings));
            _lastRunId = result.RunId;

            var byPath = _rows.ToDictionary(r => r.Target.Path, r => r);
            foreach (var outcome in result.Outcomes)
            {
                if (byPath.TryGetValue(outcome.Target.Path, out var row))
                    row.MarkDone(Describe(outcome.Status, outcome.Target.Level));
                if (outcome.Error is not null)
                    Log($"  {outcome.Target.App} / {outcome.Target.Item} — {outcome.Error}");
            }

            SummaryText.Text =
                $"本轮完成 {result.Succeeded} 项（{Fs.Human(result.FreedBytes)}），占用跳过 {result.Locked}，失败 {result.Failed}，" +
                $"耗时 {result.Elapsed.TotalSeconds:0.##}s。";
            Log(SummaryText.Text);
            if (result.Outcomes.Any(o => o.Status == OutcomeStatus.Snapshotted))
                Log($"快照号 {result.RunId}：可用\"还原上次快照\"回滚；确认可用后请用\"清空所有快照\"完成最终擦除。");

            RefreshSnapshotButtons();
        }
        catch (Exception ex)
        {
            Log("清理失败：" + ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private static string Describe(OutcomeStatus status, TraceLevel level) => status switch
    {
        OutcomeStatus.Removed => "已擦除",
        OutcomeStatus.Snapshotted => "已进快照",
        OutcomeStatus.Locked => "占用，跳过",
        OutcomeStatus.Failed => "失败",
        _ => "已不存在",
    };

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

        var bytes = list.Sum(s => s.Bytes);
        if (MessageBox.Show(this,
                $"物理擦除 {list.Count} 轮快照，共 {list.Sum(s => s.Count)} 项 / {Fs.Human(bytes)}。\n执行后无法再还原。确认？",
                "确认彻底删除", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        SetBusy(true);
        var engine = new RestoreEngine(_store);
        foreach (var snapshot in list)
        {
            var (deleted, purged, error) = await Task.Run(() => engine.Purge(snapshot.RunId));
            Log(error ?? $"快照 {snapshot.RunId}：擦除 {deleted} 项 / {Fs.Human(purged)}");
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

    private void OnSelectAll(object sender, RoutedEventArgs e) =>
        SetSelection(r => !r.Done, "已全选");

    private void OnSelectNone(object sender, RoutedEventArgs e) =>
        SetSelection(_ => false, "已全不选");

    private void OnSelectL1(object sender, RoutedEventArgs e) =>
        SetSelection(r => r.Target.Level == TraceLevel.Session && !r.Done, "已只勾选 L1 登出项");

    private void SetSelection(Func<RowItem, bool> predicate, string message)
    {
        foreach (var row in _rows)
            row.Selected = predicate(row);
        UpdateSummaryFromSelection();
        Log(message);
    }

    private void UpdateSummaryFromSelection()
    {
        var selected = _rows.Where(r => r.Selected).ToList();
        SummaryText.Text = $"勾选 {selected.Count} 项，合计 {Fs.Human(selected.Sum(r => r.Target.Bytes))}。";
        BtnClean.IsEnabled = BtnHard.IsEnabled = selected.Count > 0;
    }

    private void RefreshSnapshotButtons()
    {
        var any = _store.List().Count > 0;
        BtnRestore.IsEnabled = any;
        BtnPurge.IsEnabled = any;
    }
}
