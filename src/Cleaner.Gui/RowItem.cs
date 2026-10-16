using System.ComponentModel;
using System.Runtime.CompilerServices;
using DeleInfo.Core.Model;
using DeleInfo.Core.Util;

namespace DeleInfo.Gui;

/// <summary>CleanTarget 是内核模型，不带通知；界面用一层薄包装持有勾选与状态文本。</summary>
public sealed class RowItem : INotifyPropertyChanged
{
    private bool _selected;
    private string _status;
    private bool _done;

    public RowItem(CleanTarget target)
    {
        Target = target;
        _selected = target.Selected;
        _status = Target.LowConfidence ? "需确认"
                 : Target.Locked ? "占用"
                 : Target.UnknownHost ? "陌生应用"
                 : "可清理";
    }

    public CleanTarget Target { get; }

    public bool Selected
    {
        get => _selected;
        set { _selected = value; OnPropertyChanged(); }
    }

    public string Status
    {
        get => _status;
        set { _status = value; OnPropertyChanged(); }
    }

    public bool Done
    {
        get => _done;
        set { _done = value; OnPropertyChanged(); }
    }

    public string App => Target.App;
    public string Profile => Target.Profile;
    public string Item => Target.Item;
    public string Level => Target.Level == TraceLevel.Session ? "L1 登出" : "L2 凭据";
    public string Size => Fs.Human(Target.Bytes);
    public int Files => Target.FileCount;

    /// <summary>不叫 Path：XAML 里 {Binding Path} 会被解析成绑定表达式自己的 Path 属性。</summary>
    public string Location => Target.Path;

    public bool Reversible => Target.NeedsSnapshot;

    public void MarkDone(string text)
    {
        Status = text;
        Done = true;
        Selected = false;
        OnPropertyChanged(nameof(Size));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
