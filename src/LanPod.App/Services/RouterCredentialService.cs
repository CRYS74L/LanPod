using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LanPod.Services;

public sealed class RouterCredentialService
{
    private static readonly object SyncRoot = new();
    private readonly string _directory;
    private string KeyPath => Path.Combine(_directory, "credential-key.bin");
    private string CredentialsPath => Path.Combine(_directory, "router-credentials.json");

    public RouterCredentialService() : this(AppStoragePaths.Current.DataDirectory) { }

    internal RouterCredentialService(string directory) => _directory = directory;

    public void SavePassword(string routerAddress, string password)
    {
        lock (SyncRoot)
        {
            Directory.CreateDirectory(_directory);
            if (!File.Exists(KeyPath))
            {
                if (File.Exists(CredentialsPath)) throw new InvalidDataException(LocalizationService.Text("便携密码密钥文件缺失。请恢复完整的 Data 文件夹。"));
                var newKey = RandomNumberGenerator.GetBytes(32);
                try { WriteAtomically(KeyPath, newKey); }
                finally { CryptographicOperations.ZeroMemory(newKey); }
            }

            var key = File.ReadAllBytes(KeyPath);
            var plaintext = Encoding.UTF8.GetBytes(password);
            try
            {
                var credentials = ReadCredentials();
                var encrypted = new EncryptedCredential(
                    RandomNumberGenerator.GetBytes(12), new byte[plaintext.Length], new byte[16]);
                using var cipher = new AesGcm(key, 16);
                cipher.Encrypt(encrypted.Nonce, plaintext, encrypted.Ciphertext, encrypted.Tag);
                credentials[NormalizeAddress(routerAddress)] = encrypted;
                WriteAtomically(CredentialsPath, JsonSerializer.SerializeToUtf8Bytes(credentials));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
    }

    public string? ReadPassword(string routerAddress)
    {
        lock (SyncRoot)
        {
            if (!ReadCredentials().TryGetValue(NormalizeAddress(routerAddress), out var encrypted)) return null;
            var key = File.ReadAllBytes(KeyPath);
            var plaintext = new byte[encrypted.Ciphertext.Length];
            try
            {
                using var cipher = new AesGcm(key, 16);
                cipher.Decrypt(encrypted.Nonce, encrypted.Ciphertext, encrypted.Tag, plaintext);
                return Encoding.UTF8.GetString(plaintext);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
    }

    public bool HasPassword(string routerAddress) => GetPasswordLength(routerAddress) > 0;

    public int GetPasswordLength(string routerAddress) => ReadPassword(routerAddress)?.Length ?? 0;

    private Dictionary<string, EncryptedCredential> ReadCredentials() =>
        File.Exists(CredentialsPath)
            ? JsonSerializer.Deserialize<Dictionary<string, EncryptedCredential>>(File.ReadAllBytes(CredentialsPath))
                ?? throw new InvalidDataException(LocalizationService.Text("便携密码文件格式无效。"))
            : new(StringComparer.Ordinal);

    private static string NormalizeAddress(string address) => address.Trim().ToLowerInvariant();

    private static void WriteAtomically(string path, byte[] content)
    {
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllBytes(temporaryPath, content);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally { File.Delete(temporaryPath); }
    }

    private sealed record EncryptedCredential(byte[] Nonce, byte[] Ciphertext, byte[] Tag);
}
