using System.Security.Cryptography;
using System.Text;

namespace Kinora.Core.Settings;

/// <summary>
/// Keys kept in settings.json. On Windows they are encrypted for the current user (DPAPI): a
/// copy of the file is useless elsewhere. On Linux and macOS they are stored as they are, in a
/// file only the user can read.
/// </summary>
public static class SecretText
{
    private const string WindowsPrefix = "dpapi:";
    private const string PlainPrefix = "plain:";
    private static readonly byte[] Entropy = "Kinora settings"u8.ToArray();

    public static string Protect(string value)
    {
        if (value.Length == 0)
            return "";
        if (OperatingSystem.IsWindows())
        {
            var data = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser);
            return WindowsPrefix + Convert.ToBase64String(data);
        }
        return PlainPrefix + value;
    }

    public static string Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored))
            return "";
        if (stored.StartsWith(PlainPrefix, StringComparison.Ordinal))
            return stored[PlainPrefix.Length..];
        if (stored.StartsWith(WindowsPrefix, StringComparison.Ordinal) && OperatingSystem.IsWindows())
        {
            try
            {
                var data = ProtectedData.Unprotect(Convert.FromBase64String(stored[WindowsPrefix.Length..]), Entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(data);
            }
            catch (Exception e) when (e is CryptographicException or FormatException)
            {
                return "";
            }
        }
        return "";
    }
}
