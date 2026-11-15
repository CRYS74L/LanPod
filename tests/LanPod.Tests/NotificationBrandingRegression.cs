using LanPod.Services;
using Microsoft.Win32;
using System.IO;

internal static class NotificationBrandingRegression
{
    internal static int Run()
    {
        var testId = Guid.NewGuid().ToString("N");
        var directory = Path.Combine(Environment.CurrentDirectory, ".tools", "test-data", $"notification-branding-{testId}");
        var registryPath = $@"Software\LanPod.Tests\NotificationBranding-{testId}";
        try
        {
            Require(NotificationService.StableAppUserModelId != WindowsNotificationRegistration.LegacyAppUserModelId,
                "The branded sender still reuses the legacy attribution cache.");

            using var testRegistry = Registry.CurrentUser.CreateSubKey(registryPath);
            WindowsNotificationRegistration.MigrateLegacySettings(testRegistry);
            Require(testRegistry.GetSubKeyNames().Length == 0, "A clean install created legacy settings.");

            var legacyPath = $@"{WindowsNotificationRegistration.SettingsPath}\{WindowsNotificationRegistration.LegacyAppUserModelId}";
            var currentPath = $@"{WindowsNotificationRegistration.SettingsPath}\{NotificationService.StableAppUserModelId}";
            using var legacySettings = testRegistry.CreateSubKey(legacyPath);
            legacySettings.SetValue("Enabled", 0, RegistryValueKind.DWord);
            legacySettings.SetValue("ShowBanner", 0, RegistryValueKind.DWord);
            legacySettings.SetValue("ShowInActionCenter", 1, RegistryValueKind.DWord);
            legacySettings.SetValue("SoundFile", @"%SystemRoot%\Media\notify.wav", RegistryValueKind.ExpandString);

            WindowsNotificationRegistration.MigrateLegacySettings(testRegistry);
            using var migratedSettings = testRegistry.OpenSubKey(currentPath, writable: true)!;
            Require((int?)migratedSettings.GetValue("Enabled") == 0 &&
                (int?)migratedSettings.GetValue("ShowBanner") == 0 &&
                (int?)migratedSettings.GetValue("ShowInActionCenter") == 1,
                "The upgrade lost a Windows notification preference or re-enabled notifications.");
            Require(!WindowsNotificationRegistration.IsEnabled(testRegistry, NotificationService.StableAppUserModelId),
                "The application would submit a toast despite an imported Windows opt-out.");
            Require(migratedSettings.GetValueKind("SoundFile") == RegistryValueKind.ExpandString &&
                (string?)migratedSettings.GetValue("SoundFile", null, RegistryValueOptions.DoNotExpandEnvironmentNames) == @"%SystemRoot%\Media\notify.wav",
                "The upgrade expanded or changed a notification sound setting.");

            migratedSettings.SetValue("Enabled", 1, RegistryValueKind.DWord);
            migratedSettings.SetValue("ShowBanner", 1, RegistryValueKind.DWord);
            Require(WindowsNotificationRegistration.IsEnabled(testRegistry, NotificationService.StableAppUserModelId),
                "Re-enabling the Windows sender did not permit notifications.");
            WindowsNotificationRegistration.MigrateLegacySettings(testRegistry);
            Require((int?)migratedSettings.GetValue("Enabled") == 1 &&
                (int?)migratedSettings.GetValue("ShowBanner") == 1 &&
                (int?)legacySettings.GetValue("Enabled") == 0,
                "A later launch overwrote current preferences or changed the legacy sender.");

            var firstInstallation = Path.Combine(directory, "旧目录");
            Directory.CreateDirectory(firstInstallation);
            var source = Path.Combine(firstInstallation, "SystemIcon.png");
            var original = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Assets", "SystemIcon.png"));
            File.WriteAllBytes(source, original);
            var userData = Path.Combine(directory, "用户资料");
            var icon = WindowsNotificationRegistration.PrepareIcon(source, userData);
            Require(icon.IsFile && File.ReadAllBytes(icon.LocalPath).SequenceEqual(original) &&
                !icon.LocalPath.StartsWith(firstInstallation, StringComparison.OrdinalIgnoreCase),
                "Windows still depends on an icon inside the installation directory.");

            var nextInstallation = Path.Combine(directory, "LanPod");
            Directory.Move(firstInstallation, nextInstallation);
            var nextSource = Path.Combine(nextInstallation, "SystemIcon.png");
            Require(File.Exists(icon.LocalPath) &&
                WindowsNotificationRegistration.PrepareIcon(nextSource, userData) == icon,
                "Moving the app invalidated its registered icon or changed the unchanged asset URI.");

            File.WriteAllBytes(icon.LocalPath, [0]);
            WindowsNotificationRegistration.PrepareIcon(nextSource, userData);
            Require(File.ReadAllBytes(icon.LocalPath).SequenceEqual(original), "A damaged notification icon was not repaired.");

            var replacement = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.png"));
            File.WriteAllBytes(nextSource, replacement);
            var updatedIcon = WindowsNotificationRegistration.PrepareIcon(nextSource, userData);
            Require(updatedIcon != icon && File.ReadAllBytes(updatedIcon.LocalPath).SequenceEqual(replacement) &&
                File.ReadAllBytes(icon.LocalPath).SequenceEqual(original),
                "An icon update reused a cached asset URI or invalidated an earlier notification icon.");
            Require(Directory.GetFiles(Path.GetDirectoryName(icon.LocalPath)!, "*.tmp").Length == 0,
                "Icon registration left temporary files behind.");

            Console.WriteLine("Notification branding PASS: clean install, legacy opt-out and display/sound migration, later preferences, relocation, repair and icon replacement.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(registryPath, throwOnMissingSubKey: false);
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
