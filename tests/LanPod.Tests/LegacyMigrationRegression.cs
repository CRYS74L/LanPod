using LanPod.Services;
using System.Text.Json.Nodes;

internal static class LegacyMigrationRegression
{
    private const string Address = "legacy-cleanup.invalid";
    private const string Password = "Fictional-migration-password";
    private const string State = """
        {"settings":{"routerAddress":"legacy-cleanup.invalid","interfaceLanguage":"en"},
         "devices":{"02:00:00:00:00:01":{"macAddress":"02:00:00:00:00:01","customName":"Fixture","notifyWhenOnline":true,"lastKnownOnline":true}},
         "sessions":[{"macAddress":"02:00:00:00:00:01","onlineAt":"2026-01-01T00:00:00+00:00","offlineAt":null}]}
        """;

    internal static int Audit(string applicationDirectory)
    {
        var dataDirectory = Path.Combine(Path.GetFullPath(applicationDirectory), "Data");
        var current = JsonNode.Parse(File.ReadAllBytes(Path.Combine(dataDirectory, "state.json")));
        var store = new LegacyRouterCredentialStore();
        var found = 0;
        foreach (var name in new[] { "LanPod", "LanDeviceMonitor" })
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), name, "state.json");
            if (!File.Exists(path)) continue;
            found++;
            var legacy = JsonNode.Parse(File.ReadAllBytes(path));
            var covered = LegacyDataCleanup.Covers(legacy, current);
            var address = legacy?["settings"]?["routerAddress"]?.GetValue<string>() ?? "";
            var oldPassword = store.ReadPassword(address);
            var passwordSaved = oldPassword is null || new RouterCredentialService(dataDirectory).ReadPassword(address) == oldPassword;
            Console.WriteLine($"Legacy migration audit: stateCovered={covered}, credentialSaved={passwordSaved}");
            if (!covered || !passwordSaved) return 1;
        }
        Console.WriteLine($"Legacy migration audit: remainingSources={found}");
        return 0;
    }

    internal static int Run()
    {
        var root = AppStoragePaths.Current.ApplicationDirectory;
        var store = new Credentials();
        var legacyRoot = Path.Combine(root, "profile");
        var legacy = Seed(legacyRoot);
        var paths = new AppStoragePaths(Path.Combine(root, "portable"), legacyRoot, store);
        paths.Initialize();
        Require(!Directory.Exists(legacy) && store.Password is null && store.Deletes == 1,
            "A verified import retained the old files or Windows credential.");
        Require(new RouterCredentialService(paths.DataDirectory).ReadPassword(Address) == Password &&
            File.ReadAllText(Path.Combine(paths.DataDirectory, "state.json")) == State,
            "Legacy cleanup ran before the portable data and password were durable.");
        paths.Initialize();
        Require(store.Deletes == 1, "A subsequent launch repeated legacy credential deletion.");

        // Already-portable users may have accumulated new polls since the import.
        legacy = Seed(legacyRoot);
        store.Password = Password;
        var current = JsonNode.Parse(State)!;
        current["devices"]!["02:00:00:00:00:01"]!["lastKnownOnline"] = false;
        current["sessions"]![0]!["offlineAt"] = "2026-01-01T01:00:00+00:00";
        File.WriteAllText(Path.Combine(paths.DataDirectory, "state.json"), current.ToJsonString());
        File.WriteAllText(Path.Combine(legacy, "personal-note.txt"), "keep");
        paths.Initialize();
        Require(store.Password is null && Directory.GetFiles(legacy).Select(Path.GetFileName).SequenceEqual(["personal-note.txt"]),
            "Cleanup retained migrated files or removed an unrelated file.");

        foreach (var scenario in new[] { "missing-history", "changed-name", "changed-setting", "missing-password" })
        {
            var scenarioRoot = Path.Combine(root, scenario);
            var scenarioLegacyRoot = Path.Combine(scenarioRoot, "profile");
            var old = Seed(scenarioLegacyRoot);
            var credentials = new Credentials();
            var scenarioPaths = new AppStoragePaths(Path.Combine(scenarioRoot, "portable"), scenarioLegacyRoot, credentials);
            Directory.CreateDirectory(scenarioPaths.DataDirectory);
            var state = JsonNode.Parse(State)!;
            if (scenario == "missing-history") state["sessions"] = new JsonArray();
            if (scenario == "changed-name") state["devices"]!["02:00:00:00:00:01"]!["customName"] = "Other data";
            if (scenario == "changed-setting") state["settings"]!["interfaceLanguage"] = "ja";
            File.WriteAllText(Path.Combine(scenarioPaths.DataDirectory, "state.json"), state.ToJsonString());
            if (scenario != "missing-password") new RouterCredentialService(scenarioPaths.DataDirectory).SavePassword(Address, Password);
            scenarioPaths.Initialize();
            Require(File.Exists(Path.Combine(old, "state.json")) && credentials.Deletes == 0 &&
                File.Exists(Path.Combine(scenarioPaths.DataDirectory, "migration.log")),
                $"Unsafe cleanup accepted the {scenario} case.");
        }

        var changedRoot = Path.Combine(root, "credential-race");
        var changedLegacyRoot = Path.Combine(changedRoot, "profile");
        var changedLegacy = Seed(changedLegacyRoot);
        var changedStore = new Credentials { FailDeletion = true };
        var changedPaths = new AppStoragePaths(Path.Combine(changedRoot, "portable"), changedLegacyRoot, changedStore);
        var rejected = false;
        try { changedPaths.Initialize(); }
        catch (IOException) { rejected = true; }
        Require(rejected && File.Exists(Path.Combine(changedLegacy, "state.json")) &&
            new RouterCredentialService(changedPaths.DataDirectory).ReadPassword(Address) == Password,
            "A failed credential cleanup lost either source or portable data.");
        changedStore.FailDeletion = false;
        changedPaths.Initialize();
        Require(!Directory.Exists(changedLegacy), "A later launch could not resume the failed cleanup.");
        Console.WriteLine("Legacy migration PASS: verified password transfer, old files/credential cleanup, existing portable data, unknown files preserved, divergent data retained, failure recovery and repeat launch.");
        return 0;
    }

    private static string Seed(string root)
    {
        var directory = Path.Combine(root, "LanPod");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "state.json"), State);
        File.WriteAllText(Path.Combine(directory, "window-placement.json"), "{}");
        File.WriteAllText(Path.Combine(directory, "tray-runtime.log"), "old log");
        return directory;
    }

    private sealed class Credentials : ILegacyRouterCredentialStore
    {
        internal string? Password = LegacyMigrationRegression.Password;
        internal int Deletes;
        internal bool FailDeletion;
        public string? ReadPassword(string address) => Password;
        public void DeletePasswordIfMatches(string address, string expected)
        {
            if (FailDeletion || Password != expected) throw new IOException("Fixture credential changed.");
            Password = null;
            Deletes++;
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
