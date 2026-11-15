using LanPod.Services;
using System.Security.Cryptography;
using System.Text;

internal static class PortableStorageRegression
{
    internal static async Task<int> RunAsync()
    {
        var root = AppStoragePaths.Current.ApplicationDirectory;
        var legacyRoot = Path.Combine(root, "legacy-source");
        var application = Path.Combine(root, "初始位置", "LanPod");
        var paths = new AppStoragePaths(application, legacyRoot);
        paths.Initialize();
        Require(Directory.Exists(paths.DataDirectory) && !Directory.Exists(legacyRoot),
            "A clean launch wrote outside the application directory.");

        const string address = "portable-storage.invalid";
        const string password = "Portable-test-便携-🔒";
        var credentials = new RouterCredentialService(paths.DataDirectory);
        Require(credentials.ReadPassword(address) is null && credentials.GetPasswordLength(address) == 0,
            "A clean portable folder inherited another credential store.");
        credentials.SavePassword(address, password);
        credentials.SavePassword("other-router.invalid", "Other-test-password");
        Require(credentials.ReadPassword(" PORTABLE-STORAGE.INVALID ") == password &&
            credentials.GetPasswordLength(address) == password.Length,
            "Portable credentials failed to round-trip or changed the password mask length.");
        Require(!File.ReadAllText(Path.Combine(paths.DataDirectory, "router-credentials.json")).Contains(password, StringComparison.Ordinal),
            "The portable password file contains plaintext credentials.");

        var repository = new DeviceRepository(Path.Combine(paths.DataDirectory, "state.json"));
        await repository.UpdateRouterAddressAsync(address);
        var movedApplication = Path.Combine(root, "移动后 带空格", "LanPod");
        Directory.CreateDirectory(Path.GetDirectoryName(movedApplication)!);
        Directory.Move(application, movedApplication);
        var moved = new AppStoragePaths(movedApplication, Path.Combine(root, "different-windows-profile"));
        moved.Initialize();
        var movedCredentials = new RouterCredentialService(moved.DataDirectory);
        Require(movedCredentials.ReadPassword(address) == password &&
            movedCredentials.ReadPassword("other-router.invalid") == "Other-test-password",
            "Moving the folder to a different Windows profile lost its credentials.");
        var reloaded = new DeviceRepository(Path.Combine(moved.DataDirectory, "state.json"));
        await reloaded.LoadAsync();
        Require(reloaded.Settings.RouterAddress == address, "Moving the folder lost its settings.");

        var credentialsPath = Path.Combine(moved.DataDirectory, "router-credentials.json");
        var original = File.ReadAllBytes(credentialsPath);
        var corrupted = Encoding.UTF8.GetString(original).Replace("Ciphertext\":\"", "Ciphertext\":\"AA");
        File.WriteAllText(credentialsPath, corrupted);
        var rejected = false;
        try { movedCredentials.ReadPassword(address); }
        catch (Exception exception) when (exception is CryptographicException or FormatException or System.Text.Json.JsonException)
        { rejected = true; }
        Require(rejected, "Damaged portable credentials were silently accepted.");
        File.WriteAllBytes(credentialsPath, original);

        var legacy = Path.Combine(legacyRoot, "LanDeviceMonitor");
        Directory.CreateDirectory(legacy);
        const string state = "{\"settings\":{\"routerAddress\":\"legacy-portable.invalid\",\"interfaceLanguage\":\"en\"},\"devices\":{},\"sessions\":[]}";
        File.WriteAllText(Path.Combine(legacy, "state.json"), state);
        File.WriteAllText(Path.Combine(legacy, "window-placement.json"), "{}");
        var migration = new AppStoragePaths(Path.Combine(root, "upgrade"), legacyRoot);
        migration.Initialize();
        Require(File.ReadAllText(Path.Combine(migration.DataDirectory, "state.json")) == state &&
            File.ReadAllText(Path.Combine(legacy, "state.json")) == state &&
            File.Exists(Path.Combine(migration.DataDirectory, "window-placement.json")),
            "The upgrade lost legacy state or modified its source.");
        File.WriteAllText(Path.Combine(migration.DataDirectory, "state.json"), "portable-current");
        migration.Initialize();
        Require(File.ReadAllText(Path.Combine(migration.DataDirectory, "state.json")) == "portable-current",
            "A subsequent launch overwrote portable data with an older installation.");

        var blocked = new AppStoragePaths(Path.Combine(root, "blocked"), legacyRoot);
        Directory.CreateDirectory(blocked.ApplicationDirectory);
        File.WriteAllText(blocked.DataDirectory, "occupied");
        rejected = false;
        try { blocked.Initialize(); }
        catch (IOException) { rejected = true; }
        Require(rejected && !Directory.GetDirectories(blocked.ApplicationDirectory, ".LanPod-data-*").Any(),
            "A failed migration leaked staging data or fell back outside the application folder.");
        Console.WriteLine("Portable storage PASS: local Data only, encrypted credentials, relocation/profile independence, mask length, damage detection, legacy import, repeat launch and failed migration cleanup.");
        return 0;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
