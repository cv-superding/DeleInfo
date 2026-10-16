using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace DeleInfo.Gui;

/// <summary>
/// 运行时换肤：只替换 MergedDictionaries[0] 那份令牌字典，控件样式不动。
/// 标题栏的深浅色走 DWM 属性，否则会出现"深色内容 + 亮色标题栏"的割裂感。
/// </summary>
public static class ThemeManager
{
    private const int UseImmersiveDarkMode = 20;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public static bool IsDark { get; private set; }

    public static event Action? Changed;

    private static string StorePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DeleInfo", "theme.txt");

    static ThemeManager()
    {
        try
        {
            IsDark = File.Exists(StorePath) &&
                     File.ReadAllText(StorePath).Trim().Equals("dark", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            IsDark = false;
        }
    }

    public static void ApplyTo(Application app)
    {
        Swap(app);
    }

    public static void Toggle()
    {
        IsDark = !IsDark;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            File.WriteAllText(StorePath, IsDark ? "dark" : "light");
        }
        catch
        {
            /* 存不下偏好下次启动回到默认，不影响本次切换 */
        }

        var app = Application.Current;
        if (app is null)
            return;

        Swap(app);
        foreach (Window window in app.Windows)
            ApplyTitleBar(window);
        Changed?.Invoke();
    }

    public static void ApplyTitleBar(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
            return;
        var on = IsDark ? 1 : 0;
        try { DwmSetWindowAttribute(hwnd, UseImmersiveDarkMode, ref on, sizeof(int)); } catch { /* Win10 1809 以前没有这个属性 */ }
    }

    private static void Swap(Application app)
    {
        var file = IsDark ? "Themes/Dark.xaml" : "Themes/Light.xaml";
        var tokens = new ResourceDictionary { Source = new Uri(file, UriKind.Relative) };

        var merged = app.Resources.MergedDictionaries;
        if (merged.Count == 0)
            merged.Add(tokens);
        else
            merged[0] = tokens;
    }
}
