using System.IO;
using System.Windows;

namespace DeleInfo.Gui;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            var log = Path.Combine(AppContext.BaseDirectory, "deleinfo-gui-error.log");
            try
            {
                File.AppendAllText(log, $"{DateTime.Now:O} {args.Exception}\n\n");
            }
            catch
            {
                /* 日志写不了也不能挡住报错框 */
            }

            MessageBox.Show(args.Exception.Message, "DeleInfo 出错了",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };
    }
}
