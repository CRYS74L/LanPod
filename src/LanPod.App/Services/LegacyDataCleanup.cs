using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LanPod.Services;

internal static class LegacyDataCleanup
{
    private static readonly HashSet<string> ObservedFields = new(StringComparer.Ordinal)
    {
        "hasObservedState", "lastKnownOnline", "lastSeenAt", "lastOnlineSeenAt", "lastRouterName",
        "lastOriginalName", "lastIpAddress", "lastConnectionType", "lastUploadSpeedBytes",
        "lastDownloadSpeedBytes", "lastTotalUploadBytes", "lastTotalDownloadBytes"
    };

    internal static void Run(string dataDirectory, string legacyRoot, ILegacyRouterCredentialStore credentials)
    {
        var portableStatePath = Path.Combine(dataDirectory, "state.json");
        if (!File.Exists(portableStatePath)) return;
        foreach (var name in new[] { "LanPod", "LanDeviceMonitor" })
        {
            var directory = Path.Combine(legacyRoot, name);
            var statePath = Path.Combine(directory, "state.json");
            if (!File.Exists(statePath)) continue;
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("旧版数据目录是链接，未自动清理：" + directory);
            var files = new[] { "tray-runtime.log", "window-placement.json", "state.json" }
                .Select(file => Path.Combine(directory, file)).Where(File.Exists)
                .ToDictionary(path => path, File.ReadAllBytes);
            JsonNode? legacy;
            JsonNode? portable;
            try
            {
                legacy = JsonNode.Parse(files[statePath]);
                portable = JsonNode.Parse(File.ReadAllBytes(portableStatePath));
            }
            catch (JsonException)
            {
                Defer(dataDirectory, directory, "配置文件无法解析，未删除旧资料。");
                continue;
            }
            if (!Covers(legacy, portable))
            {
                Defer(dataDirectory, directory, "旧资料与当前 Data 不一致，未删除旧资料。");
                continue;
            }
            var address = legacy?["settings"]?["routerAddress"]?.GetValue<string>();
            var password = string.IsNullOrEmpty(address) ? null : credentials.ReadPassword(address);
            if (password is not null)
            {
                if (new RouterCredentialService(dataDirectory).ReadPassword(address!) != password)
                {
                    Defer(dataDirectory, directory, "旧密码尚未完整保存到 Data，未删除旧资料或凭据。");
                    continue;
                }
            }
            // Only retire unchanged, known files after verifying the durable copy.
            // Other files in the old directory are left to their owners.
            foreach (var (path, content) in files)
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 ||
                    !File.ReadAllBytes(path).AsSpan().SequenceEqual(content))
                    throw new IOException("旧版资料在迁移期间发生变化，已停止清理：" + path);
            }
            if (password is not null) credentials.DeletePasswordIfMatches(address!, password);
            foreach (var (path, content) in files)
            {
                if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(content))
                    throw new IOException("旧版资料在迁移期间发生变化，已停止清理：" + path);
                File.Delete(path);
            }
            if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
        }
    }

    internal static bool Covers(JsonNode? legacy, JsonNode? portable)
    {
        if (legacy is not JsonObject || portable is not JsonObject ||
            legacy["settings"] is not JsonObject settings || portable["settings"] is not JsonObject currentSettings ||
            legacy["devices"] is not JsonObject devices || portable["devices"] is not JsonObject currentDevices ||
            legacy["sessions"] is not JsonArray sessions || portable["sessions"] is not JsonArray currentSessions) return false;
        if (settings.Any(pair => !JsonNode.DeepEquals(pair.Value, currentSettings[pair.Key]))) return false;
        var profiles = currentDevices.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        foreach (var (id, profile) in devices)
        {
            if (profile is not JsonObject oldProfile || !profiles.TryGetValue(id, out var current) || current is not JsonObject currentProfile)
                return false;
            if (oldProfile.Any(pair => !ObservedFields.Contains(pair.Key) && !JsonNode.DeepEquals(pair.Value, currentProfile[pair.Key]))) return false;
        }
        var history = currentSessions.Where(node => node is JsonObject)
            .ToLookup(node => SessionKey(node!));
        foreach (var session in sessions)
        {
            if (session is not JsonObject oldSession || !history[SessionKey(session)].Any(current =>
                oldSession.All(pair => pair.Key == "offlineAt" && pair.Value is null || JsonNode.DeepEquals(pair.Value, current?[pair.Key]))))
                return false;
        }
        return true;
    }

    private static (string, DateTimeOffset) SessionKey(JsonNode node) =>
        (node["macAddress"]!.GetValue<string>().ToUpperInvariant(), node["onlineAt"]!.GetValue<DateTimeOffset>());

    private static void Defer(string dataDirectory, string directory, string reason) =>
        File.AppendAllText(Path.Combine(dataDirectory, "migration.log"), $"{DateTimeOffset.Now:O} {reason} {directory}{Environment.NewLine}");
}
