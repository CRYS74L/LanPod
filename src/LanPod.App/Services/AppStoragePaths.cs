using System.IO;
using System.Text.Json;

namespace LanPod.Services;

internal sealed class AppStoragePaths
{
    internal static AppStoragePaths Current { get; set; } = new(
        AppContext.BaseDirectory,
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    internal string ApplicationDirectory { get; }
    internal string DataDirectory { get; }
    private readonly string _legacyRoot;

    internal AppStoragePaths(string applicationDirectory, string legacyRoot)
    {
        ApplicationDirectory = Path.GetFullPath(applicationDirectory);
        DataDirectory = Path.Combine(ApplicationDirectory, "Data");
        _legacyRoot = legacyRoot;
    }

    internal void Initialize()
    {
        if (Directory.Exists(DataDirectory)) return;

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

            Directory.Move(staging, DataDirectory);
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }
}
