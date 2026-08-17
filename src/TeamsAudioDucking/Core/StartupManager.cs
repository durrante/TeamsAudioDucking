using Microsoft.Win32;

namespace TeamsAudioDucking.Core;

/// <summary>
/// Per-user "start with Windows" via HKCU Run key. No admin rights required.
/// </summary>
public static class StartupManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "TeamsAudioDucking";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(ValueName) != null;
        }
        catch
        {
            return false;
        }
    }

    public static void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (enabled)
                key.SetValue(ValueName, $"\"{Environment.ProcessPath}\"");
            else
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            Logger.Info($"Start with Windows {(enabled ? "enabled" : "disabled")}");
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to update startup entry", ex);
        }
    }
}
