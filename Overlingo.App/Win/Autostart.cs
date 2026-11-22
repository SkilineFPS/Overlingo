using Microsoft.Win32;

namespace Overlingo.App.Win;

/// <summary>Spuštění s Windows přes HKCU\...\Run (bez práv správce).</summary>
internal static class Autostart
{
    const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "Overlingo";

    public static bool IsEnabled()
    {
        try { using var k = Registry.CurrentUser.OpenSubKey(KeyPath); return k?.GetValue(ValueName) is string; }
        catch { return false; }
    }

    public static void Set(bool on)
    {
        using var k = Registry.CurrentUser.CreateSubKey(KeyPath);
        if (on)
        {
            var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Cestu k aplikaci nelze zjistit.");
            k.SetValue(ValueName, $"\"{exe}\" --minimized");
        }
        else k.DeleteValue(ValueName, false);
    }
}
