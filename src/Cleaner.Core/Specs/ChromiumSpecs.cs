using DeleInfo.Core.Model;

namespace DeleInfo.Core.Specs;

/// <summary>Chromium 内核中一处登录态的声明式描述。</summary>
public sealed record ChromiumItemSpec(
    string RelativePath,
    TraceLevel Level,
    string Item,
    bool IsDirectory,
    bool NeedsSnapshot,
    bool WithJournal = true);

/// <summary>
/// 登录态存储位置清单（本机 + Chromium 源码实测口径）。
/// 关键取舍：只列"能让人保持登录"的东西，缓存/书签/历史/扩展一律不碰——
/// 缓存既不是登录态，又是体积大头，列进来只会让清理变慢。
/// </summary>
public static class ChromiumSpecs
{
    public static readonly ChromiumItemSpec[] Session =
    {
        new("Network/Cookies", TraceLevel.Session, "Cookie（登录令牌）", false, true),
        new("Cookies", TraceLevel.Session, "Cookie（旧版路径）", false, true),
        new("Sessions", TraceLevel.Session, "会话恢复数据", true, true, false),
        new("Current Session", TraceLevel.Session, "当前会话", false, true),
        new("Current Tabs", TraceLevel.Session, "当前标签页", false, true),
        new("Last Session", TraceLevel.Session, "上次会话", false, true),
        new("Last Tabs", TraceLevel.Session, "上次标签页", false, true),
        new("Local Storage", TraceLevel.Session, "LocalStorage（令牌常藏这里）", true, true, false),
        new("Session Storage", TraceLevel.Session, "SessionStorage", true, true, false),
        new("IndexedDB", TraceLevel.Session, "IndexedDB", true, false, false),
        new("Service Worker", TraceLevel.Session, "Service Worker", true, false, false),
        new("blob_storage", TraceLevel.Session, "Blob 存储", true, false, false),
        new("platform_notifications/db", TraceLevel.Session, "通知订阅", true, false, false),
        new("DIPS", TraceLevel.Session, "DIPS 状态", false, false),
        new("Shared Storage", TraceLevel.Session, "Shared Storage", true, false, false),
        // Trust Tokens 可被站点用作免密身份凭证，属于登录态；体积极小，一并清。
        new("Network/Trust Tokens", TraceLevel.Session, "Trust Tokens", false, false),
        new("Network Device Bound Session Credentials", TraceLevel.Session, "设备绑定会话(网络层)", true, true, false),
    };

    public static readonly ChromiumItemSpec[] Credential =
    {
        new("Login Data", TraceLevel.Credential, "保存的密码库", false, true),
        new("Login Data For Account", TraceLevel.Credential, "账号密码库", false, true),
        new("Account Login Data", TraceLevel.Credential, "账号密码库", false, true),
        new("Web Data", TraceLevel.Credential, "自动填充（姓名/地址/卡号）", false, true),
        new("Account Web Data", TraceLevel.Credential, "账号自动填充", false, true),
        new("Device Bound Session Credentials", TraceLevel.Credential, "设备绑定会话凭据", true, true, false),
    };

    public static IEnumerable<ChromiumItemSpec> For(TraceLevel maxLevel)
    {
        foreach (var s in Session)
            yield return s;
        if (maxLevel >= TraceLevel.Credential)
            foreach (var s in Credential)
                yield return s;
    }
}
