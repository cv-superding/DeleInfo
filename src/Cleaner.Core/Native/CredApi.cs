using System.Runtime.InteropServices;

namespace DeleInfo.Core.Native;

/// <summary>
/// Windows 凭据管理器（advapi32 Cred* 系列）。只删除条目，绝不读取或转存 CredentialBlob：
/// 密码明文一旦进入本进程内存再落盘，就等于把"清理"变成了"备份泄露"。
/// 代价是凭据删除不可还原——这一点必须在报告里如实写明。
/// </summary>
public static class CredApi
{
    public const int CRED_TYPE_GENERIC = 1;
    public const int CRED_TYPE_DOMAIN_PASSWORD = 2;
    public const int CRED_TYPE_DOMAIN_VISIBLE_PASSWORD = 3;
    public const int CRED_TYPE_GENERIC_CERTIFICATE = 4;
    public const int CRED_TYPE_DOMAIN_CERTIFICATE = 5;
    public const int CRED_ENUMERATE_ALL_CREDENTIALS = 0x1;

    /// <summary>
    /// wincred.h 的真实字段顺序（TargetName 紧跟在 Type 之后，不在结构体末尾）：
    /// Flags, Type, TargetName, Comment, LastWritten, CredentialBlobSize, CredentialBlob,
    /// Persist, AttributeCount, Attributes, TargetAlias, UserName —— x64 上 80 字节。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct CREDENTIAL_RAW
    {
        public int Flags;
        public int Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public long LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredEnumerateW(
        string? filter, uint flags, out uint count, out IntPtr credentials);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDeleteW(string targetName, int type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr cred);

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORY_BASIC_INFORMATION
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public nuint RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nuint VirtualQuery(IntPtr address, out MEMORY_BASIC_INFORMATION info, nuint length);

    public sealed record Entry(
        string TargetName, string? UserName, string? Comment, int Type, int Persist, DateTime LastWritten)
    {
        public string TypeLabel => Type switch
        {
            CRED_TYPE_GENERIC => "普通凭据",
            CRED_TYPE_DOMAIN_PASSWORD => "域凭据(密码)",
            CRED_TYPE_DOMAIN_VISIBLE_PASSWORD => "域凭据(可见)",
            CRED_TYPE_GENERIC_CERTIFICATE => "普通证书",
            CRED_TYPE_DOMAIN_CERTIFICATE => "域证书",
            _ => $"类型 {Type}",
        };

        public string PersistLabel => Persist switch
        {
            1 => "仅本次登录",
            2 => "本机",
            3 => "企业(可漫游)",
            _ => "未知",
        };
    }

    public static List<Entry> Enumerate() => Enumerate(all: true);

    /// <param name="all">true = 含域凭据等全部类型；false = 仅本用户交互式凭据。</param>
    public static List<Entry> Enumerate(bool all)
    {
        var list = new List<Entry>();

        // 偏移错一位就会把 DWORD 当指针解引用；AccessViolation 在 .NET Core 里不可捕获，会直接杀掉进程。
        if (Marshal.SizeOf<CREDENTIAL_RAW>() != 80)
            return list;

        if (!CredEnumerateW(null, all ? CRED_ENUMERATE_ALL_CREDENTIALS : 0u, out var count, out var arrayPtr))
            return list;

        try
        {
            if (arrayPtr == IntPtr.Zero)
                return list;

            for (var i = 0; i < (int)count; i++)
            {
                try
                {
                    var entryPtr = Marshal.ReadIntPtr(arrayPtr, i * IntPtr.Size);
                    if (entryPtr == IntPtr.Zero)
                        continue;

                    var cred = Marshal.PtrToStructure<CREDENTIAL_RAW>(entryPtr);
                    var target = ReadString(cred.TargetName);
                    if (string.IsNullOrWhiteSpace(target))
                        continue;

                    list.Add(new Entry(
                        target!,
                        ReadString(cred.UserName),
                        ReadString(cred.Comment),
                        cred.Type,
                        cred.Persist,
                        DateTime.FromFileTimeUtc(cred.LastWritten == 0 ? 1 : cred.LastWritten)));
                }
                catch
                {
                    /* 单条异常不影响其余凭据 */
                }
            }
        }
        finally
        {
            CredFree(arrayPtr);
        }

        return list;
    }

    /// <summary>解引用前先确认内存页可读：宁可漏一条，也不要带着未定义行为跑。</summary>
    private static string? ReadString(IntPtr ptr)
    {
        if (ptr == IntPtr.Zero)
            return null;
        if (VirtualQuery(ptr, out var info, (nuint)Marshal.SizeOf<MEMORY_BASIC_INFORMATION>()) == 0)
            return null;
        const uint memCommit = 0x1000;
        const uint noAccessOrGuard = 0x101;
        if (info.State != memCommit || (info.Protect & noAccessOrGuard) != 0)
            return null;
        return Marshal.PtrToStringUni(ptr);
    }

    public static bool Delete(string targetName, int type, out string? error)
    {
        error = null;
        if (CredDeleteW(targetName, type, 0))
            return true;

        error = $"Win32Error {Marshal.GetLastWin32Error()}";
        return false;
    }
}
