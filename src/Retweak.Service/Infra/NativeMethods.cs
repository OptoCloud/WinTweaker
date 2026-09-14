using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Retweak.Service.Infra;

internal static partial class NativeMethods
{
    internal static readonly IntPtr HKEY_LOCAL_MACHINE = new(unchecked((int)0x80000002));
    internal static readonly IntPtr HKEY_USERS = new(unchecked((int)0x80000003));

    internal const int KEY_NOTIFY = 0x0010;
    internal const int KEY_WOW64_64KEY = 0x0100;
    internal const int KEY_WOW64_32KEY = 0x0200;

    internal const int REG_NOTIFY_CHANGE_NAME = 0x00000001;
    internal const int REG_NOTIFY_CHANGE_LAST_SET = 0x00000004;

    /// <summary>
    /// Without this flag the notification is owned by the thread that armed it and is
    /// silently cancelled when that thread exits. Because re-arming happens on pooled
    /// threads, which come and go, omitting this produces a watcher that works in
    /// testing and then quietly stops firing in production. Requires Windows 8 / 2012
    /// and fAsynchronous = TRUE.
    /// </summary>
    internal const int REG_NOTIFY_THREAD_AGNOSTIC = 0x10000000;

    internal const int ERROR_SUCCESS = 0;
    internal const int ERROR_FILE_NOT_FOUND = 2;
    internal const int ERROR_ACCESS_DENIED = 5;
    internal const int ERROR_KEY_DELETED = 1018;

    [LibraryImport("advapi32.dll", EntryPoint = "RegOpenKeyExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial int RegOpenKeyEx(
        IntPtr hKey,
        string lpSubKey,
        int ulOptions,
        int samDesired,
        out IntPtr phkResult);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    internal static partial int RegNotifyChangeKeyValue(
        IntPtr hKey,
        [MarshalAs(UnmanagedType.Bool)] bool bWatchSubtree,
        int dwNotifyFilter,
        SafeWaitHandle hEvent,
        [MarshalAs(UnmanagedType.Bool)] bool fAsynchronous);

    [LibraryImport("advapi32.dll")]
    internal static partial int RegCloseKey(IntPtr hKey);

    [LibraryImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WTSQueryUserToken(uint sessionId, out IntPtr phToken);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(IntPtr hObject);
}
