using Microsoft.Win32;
using System.IO;
using System.Security.Cryptography;

namespace LanPod.Services;

internal static class WindowsNotificationRegistration
{
    // Windows caches attribution assets by sender identity. The rebrand needs a
    // new identity, shared by every LanPod version and installation directory.
    internal const string AppUserModelId = "CRYS74L.LanPod";
    internal const string LegacyAppUserModelId = "{95AFFE3D-014F-4F5F-AD8B-FD52B4E62CE3}";
    internal const string SettingsPath = @"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings";

    internal static bool IsEnabled(RegistryKey currentUser, string appUserModelId)
    {
        using var settings = currentUser.OpenSubKey($@"{SettingsPath}\{appUserModelId}");
        return settings?.GetValue("Enabled") is not int enabled || enabled != 0;
    }

    internal static void RegisterShortcut(string displayName)
    {
        using var registration = Registry.CurrentUser.OpenSubKey($@"Software\Classes\AppUserModelId\{AppUserModelId}");
        var activator = Guid.Parse(registration?.GetValue("CustomActivator") as string
            ?? throw new InvalidOperationException("Windows 通知激活器注册缺失。"));
        var executablePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("无法确定应用程序路径。");
        var shortcutPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), $"{displayName}.lnk");
        NotificationShortcut.Ensure(shortcutPath, executablePath, AppUserModelId, activator);

        // The SDK reuses an activator registered by an earlier app location.
        // Portable moves must also update the executable used by toast clicks.
        using var server = Registry.CurrentUser.CreateSubKey($@"Software\Classes\CLSID\{activator:B}\LocalServer32");
        server.SetValue(string.Empty, $"\"{executablePath}\" ----AppNotificationActivated:");
    }

    internal static void MigrateLegacySettings(RegistryKey currentUser)
    {
        using var existingSettings = currentUser.OpenSubKey($@"{SettingsPath}\{AppUserModelId}");
        if (existingSettings is not null)
        {
            return;
        }

        using var legacySettings = currentUser.OpenSubKey($@"{SettingsPath}\{LegacyAppUserModelId}");
        if (legacySettings is null)
        {
            return;
        }

        using var settings = currentUser.CreateSubKey($@"{SettingsPath}\{AppUserModelId}");
        // Preserve opt-outs before the SDK registers or submits the first toast.
        var valueNames = legacySettings.GetValueNames()
            .OrderBy(name => name == "Enabled" ? 0 : 1);
        foreach (var name in valueNames)
        {
            var value = legacySettings.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (value is not null)
            {
                settings.SetValue(name, value, legacySettings.GetValueKind(name));
            }
        }
    }

    internal static Uri PrepareIcon(string sourcePath, string dataDirectory)
    {
        var content = File.ReadAllBytes(sourcePath);
        var hash = Convert.ToHexString(SHA256.HashData(content));
        var directory = Path.Combine(dataDirectory, "Notifications");
        var iconPath = Path.Combine(directory, $"SystemIcon-{hash}.png");

        if (!File.Exists(iconPath) || !File.ReadAllBytes(iconPath).AsSpan().SequenceEqual(content))
        {
            Directory.CreateDirectory(directory);
            var temporaryPath = Path.Combine(directory, $"{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllBytes(temporaryPath, content);
                File.Move(temporaryPath, iconPath, overwrite: true);
            }
            finally
            {
                File.Delete(temporaryPath);
            }
        }

        // Retain content-addressed icons for notifications from earlier versions.
        // Moving or replacing the application no longer invalidates their paths.
        return new Uri(iconPath);
    }
}
