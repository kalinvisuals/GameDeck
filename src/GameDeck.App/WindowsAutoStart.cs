using Microsoft.Win32;

namespace GameDeck.App;

/// <summary>
/// "Start with Windows" for the current user. Uses the same Run value as the installer's option, so
/// the installer and the Settings switch always agree.
/// </summary>
public sealed class WindowsAutoStart(string keyPath = WindowsAutoStart.RunKey, string? exePath = null)
{
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "GameDeck";

    public bool Enabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(keyPath);
            return key?.GetValue(ValueName) is string { Length: > 0 };
        }
    }

    public void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(keyPath);
        if (enabled)
        {
            var exe = exePath ?? Environment.ProcessPath ?? throw new InvalidOperationException("The application path is unknown.");
            key.SetValue(ValueName, $"\"{exe}\"");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
