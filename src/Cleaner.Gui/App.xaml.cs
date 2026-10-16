using System.Windows;

namespace DeleInfo.Gui;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // 换肤必须在 base.OnStartup 建窗之前完成，否则首帧会先闪一下亮色。
        // 也不能放进 App 构造函数：WPF 生成的 Main 是先 new App() 再 InitializeComponent()，
        // 那时资源表还是空的，按下标替换会直接索引越界把进程干掉。
        ThemeManager.ApplyTo(this);

        DispatcherUnhandledException += (_, args) =>
        {
            var log = System.IO.Path.Combine(AppContext.BaseDirectory, "deleinfo-gui-error.log");
            try
            {
                System.IO.File.AppendAllText(log, $"{args.Exception}\n\n");
            }
            catch
            {
                /* 日志写不了也不能挡住报错框 */
            }

            MessageBox.Show(args.Exception.Message, "DeleInfo 出错了",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };

        base.OnStartup(e);
    }
}
