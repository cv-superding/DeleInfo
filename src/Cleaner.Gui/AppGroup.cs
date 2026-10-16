using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using DeleInfo.Core.Util;

namespace DeleInfo.Gui;

/// <summary>
/// 一个应用一张卡片。原来的平铺 GridView 既看不出"哪些属于同一个应用"，
/// 也没法整体勾选，所以换成带三态选择卡片的分组视图。
/// </summary>
public sealed class AppGroup : INotifyPropertyChanged
{
    private bool? _selected = true;
    private bool _isExpanded;

    public AppGroup(string app, string kind)
    {
        App = app;
        Kind = kind;
        Rows.CollectionChanged += (_, _) => RefreshFromRows();
    }

    public string App { get; }

    /// <summary>浏览器 / 桌面应用 / 凭据文件 / 系统凭据，用作卡片副标题。</summary>
    public string Kind { get; }

    public ObservableCollection<RowItem> Rows { get; } = new();

    public bool IsExpanded
    {
        get => _isExpanded;
        set { _isExpanded = value; OnPropertyChanged(); }
    }

    public bool? Selected
    {
        get => _selected;
        set
        {
            if (_selected == value)
                return;
            _selected = value;
            OnPropertyChanged();

            if (value is null)
                return;
            foreach (var row in Rows.Where(r => !r.Done))
                row.Selected = value.Value;
        }
    }

    /// <summary>
    /// 头像用应用名首字母而不是图标字体：MDL2 码点一旦选错就是豆腐块，
    /// 首字母圆块既现代又不依赖任何字体。
    /// </summary>
    public string Initial
    {
        get
        {
            var name = App;
            var slash = name.LastIndexOf('/');
            if (slash >= 0 && slash + 1 < name.Length)
                name = name[(slash + 1)..];
            name = name.TrimStart('@', ' ');
            if (name.Length == 0)
                return "?";
            return char.IsAsciiLetter(name[0]) ? char.ToUpperInvariant(name[0]).ToString() : name[..1];
        }
    }

    public string SizeText => Fs.Human(Rows.Where(r => r.Selected).Sum(r => r.Target.Bytes));

    public string TotalText => Fs.Human(Rows.Sum(r => r.Target.Bytes));

    public int SelectedCount => Rows.Count(r => r.Selected);

    public int FileCount => Rows.Where(r => r.Selected).Sum(r => r.Target.FileCount);

    /// <summary>卡片右上角状态：先提示占用，再提示需确认，最后才是可清理。</summary>
    public string BadgeText
    {
        get
        {
            if (Rows.All(r => r.Done))
                return "已处理";
            if (Rows.Any(r => r.Target.HostRunning && !r.Done))
                return "宿主运行中";
            if (Rows.All(r => r.Target.LowConfidence))
                return "需确认";
            return "可清理";
        }
    }

    /// <summary>行内任意勾选变化都要回灌到组的三态，否则"全选"和卡片会不一致。</summary>
    public void RefreshFromRows()
    {
        foreach (var row in Rows)
        {
            row.PropertyChanged -= OnRowChanged;
            row.PropertyChanged += OnRowChanged;
        }

        var active = Rows.Where(r => !r.Done).ToList();
        _selected = active.Count == 0
            ? false
            : active.All(r => r.Selected)
                ? true
                : active.Any(r => r.Selected) ? null : false;

        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(TotalText));
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(FileCount));
        OnPropertyChanged(nameof(BadgeText));
        OnPropertyChanged(nameof(Selected));
        OnPropertyChanged(nameof(Initial));
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RowItem.Selected) or nameof(RowItem.Done))
            RefreshFromRows();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
