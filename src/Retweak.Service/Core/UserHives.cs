using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Retweak.Service.Core;

public static partial class UserHives
{
    /// <summary>
    /// Matches real interactive-account SIDs. Deliberately excludes the machine-local
    /// well-known accounts (S-1-5-18/19/20), the "_Classes" companion hives, and the
    /// .DEFAULT hive, none of which correspond to a logged-on human.
    /// </summary>
    [GeneratedRegex(@"^S-1-5-21-\d+(-\d+)+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UserSidPattern { get; }

    public static bool IsUserSid(string sid) => UserSidPattern.IsMatch(sid);

    /// <summary>
    /// Enumerates SIDs whose hive is currently loaded under HKEY_USERS.
    /// A hive being present here is the only reliable signal that the profile is
    /// mounted; a logon notification arrives before that is guaranteed.
    /// </summary>
    public static IReadOnlyList<string> GetLoadedUserSids(RegistryView view)
    {
        using RegistryKey hku = RegistryKey.OpenBaseKey(RegistryHive.Users, view);
        return GetLoadedUserSids(hku);
    }

    public static IReadOnlyList<string> GetLoadedUserSids(RegistryKey hku)
    {
        var result = new List<string>();
        foreach (string name in hku.GetSubKeyNames())
        {
            if (IsUserSid(name))
            {
                result.Add(name);
            }
        }

        return result;
    }

    /// <summary>
    /// Verifies a hive is not just listed but actually openable. A hive can be listed
    /// while it is being unloaded, and opening it then fails.
    /// </summary>
    public static bool IsHiveUsable(RegistryView view, string sid)
    {
        try
        {
            using RegistryKey hku = RegistryKey.OpenBaseKey(RegistryHive.Users, view);
            using RegistryKey? key = hku.OpenSubKey(sid, writable: false);
            return key is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }
}
