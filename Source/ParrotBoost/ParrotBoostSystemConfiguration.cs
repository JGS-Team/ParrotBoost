using Microsoft.Win32;

namespace ParrotBoost;

internal static class ParrotBoostSystemConfiguration
{
    private const string RegistryPath = @"Software\JGS\ParrotBoost";
    private const string EnabledValueName = "Enabled";
    private static bool? _cachedBoostEnabled;

    public static bool IsBoostEnabled()
    {
        if (_cachedBoostEnabled.HasValue)
        {
            return _cachedBoostEnabled.Value;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryPath, false);
            bool enabled = (key?.GetValue(EnabledValueName) as int? ?? 0) == 1;
            _cachedBoostEnabled = enabled;
            return enabled;
        }
        catch
        {
            return false;
        }
    }

    public static void SetBoostEnabled(bool enabled)
    {
        _cachedBoostEnabled = enabled;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RegistryPath, true);
            key?.SetValue(EnabledValueName, enabled ? 1 : 0, RegistryValueKind.DWord);
        }
        catch
        {
        }
    }
}
