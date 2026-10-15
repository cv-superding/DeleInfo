using DeleInfo.Core.Cleaners;
using DeleInfo.Core.Model;

namespace DeleInfo.Core;

public static class CleanerRegistry
{
    public static IReadOnlyList<ICleaner> All { get; } = new ICleaner[]
    {
        new ChromiumBrowserCleaner(),
        new ElectronAppCleaner(),
        new FirefoxCleaner(),
        new NativeAppCleaner(),
        new WindowsCredentialCleaner(),
    };

    /// <summary>每个清理器同时覆盖 L1/L2，分级由 ScanOptions.MaxLevel 在 Discover 内部决定。</summary>
    public static IReadOnlyList<ICleaner> For(ScanOptions options) => All;
}
