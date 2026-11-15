using System.IO;
using System.Text.Json;

namespace LanPod.Services;

internal sealed class AppStoragePaths
{
    internal static AppStoragePaths Current { get; set; } = new(
        Path.GetDirectoryName(ExecutablePath)!,
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    internal string ApplicationDirectory { get; }
    internal string DataDirectory { get; }
    internal static string ExecutablePath =>
        Environment.GetEnvironmentVariable("LANPOD_LAUNCHER_PATH") is { Length: > 0 } launcher
            ? Path.GetFullPath(launcher)
            : Environment.ProcessPath ?? throw new InvalidOperationException("无法确定应用程序路径。");
    private readonly string _legacyRoot;

    internal AppStoragePaths(string applicationDirectory, string legacyRoot)
    {
        ApplicationDirectory = Path.GetFullPath(applicationDirectory);
        DataDirectory = Path.Combine(ApplicationDirectory, "Data");
        _legacyRoot = legacyRoot;
    }

    internal void Initialize()
    {
        var pendingImport = Path.Combine(DataDirectory, ".initial-import");
        if (Directory.Exists(pendingImport))
        {
            CompleteImport(pendingImport);
            return;
        }
        // The native launcher creates Data/Runtime before managed startup.
        // Existing user data belongs to this portable copy and takes priority.
        if (Directory.Exists(DataDirectory) && Directory.EnumerateFileSystemEntries(DataDirectory)
            .Any(path => !string.Equals(Path.GetFileName(path), "Runtime", StringComparison.OrdinalIgnoreCase))) return;

        var staging = Path.Combine(ApplicationDirectory, $".LanPod-data-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(staging);
            var legacyDirectory = new[] { "LanPod", "LanDeviceMonitor" }
                .Select(name => Path.Combine(_legacyRoot, name))
                .FirstOrDefault(path => File.Exists(Path.Combine(path, "state.json")));
            if (legacyDirectory is not null)
            {
                foreach (var name in new[] { "state.json", "window-placement.json" })
                {
                    var source = Path.Combine(legacyDirectory, name);
                    if (File.Exists(source)) File.Copy(source, Path.Combine(staging, name));
                }

                using var state = JsonDocument.Parse(File.ReadAllText(Path.Combine(staging, "state.json")));
                if (state.RootElement.TryGetProperty("settings", out var settings) &&
                    settings.TryGetProperty("routerAddress", out var address) &&
                    address.GetString() is { Length: > 0 } routerAddress)
                {
                    var password = LegacyRouterCredentialReader.ReadPassword(routerAddress);
                    if (!string.IsNullOrEmpty(password))
                        new RouterCredentialService(staging).SavePassword(routerAddress, password);
                }
            }

            if (Directory.Exists(DataDirectory))
            {
                Directory.Move(staging, pendingImport);
                CompleteImport(pendingImport);
            }
            else
            {
                Directory.Move(staging, DataDirectory);
                File.WriteAllText(Path.Combine(DataDirectory, ".initialized"), "1");
            }
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    private void CompleteImport(string pendingImport)
    {
        foreach (var file in Directory.EnumerateFiles(pendingImport))
            File.Move(file, Path.Combine(DataDirectory, Path.GetFileName(file)));
        File.WriteAllText(Path.Combine(DataDirectory, ".initialized"), "1");
        Directory.Delete(pendingImport);
    }
}
