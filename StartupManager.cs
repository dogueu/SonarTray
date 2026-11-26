using Microsoft.Win32;

namespace SonarTray;

/// <summary>
/// Manages Windows current-user startup registration for SonarTray.
/// </summary>
public static class StartupManager
{
    private const string RunRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupApprovedRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string SonarTrayValueName = "SonarTray";

    public static bool IsSonarTrayStartupEnabled()
    {
        try
        {
            using var runKey = Registry.CurrentUser.OpenSubKey(RunRegistryPath, writable: false);
            if (runKey?.GetValue(SonarTrayValueName) is not string value || string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            using var approvedKey = Registry.CurrentUser.OpenSubKey(StartupApprovedRegistryPath, writable: false);
            if (approvedKey?.GetValue(SonarTrayValueName) is byte[] bytes &&
                bytes.Length >= 1 &&
                (bytes[0] & 1) == 1)
            {
                return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool SetSonarTrayStartupEnabled(bool enabled)
    {
        try
        {
            using var runKey = Registry.CurrentUser.CreateSubKey(RunRegistryPath, writable: true);
            if (runKey is null)
            {
                return false;
            }

            if (enabled)
            {
                var exePath = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(exePath))
                {
                    return false;
                }

                runKey.SetValue(SonarTrayValueName, $"\"{exePath}\"", RegistryValueKind.String);

                using var approvedKey = Registry.CurrentUser.CreateSubKey(StartupApprovedRegistryPath, writable: true);
                if (approvedKey?.GetValue(SonarTrayValueName) is not null)
                {
                    var enabledBytes = new byte[12];
                    enabledBytes[0] = 0x02;
                    approvedKey.SetValue(SonarTrayValueName, enabledBytes, RegistryValueKind.Binary);
                }
            }
            else
            {
                runKey.DeleteValue(SonarTrayValueName, throwOnMissingValue: false);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }
}
