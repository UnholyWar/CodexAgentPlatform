using System.Security.Cryptography;
using System.Text;

namespace CodexAgentPlatform.Infrastructure;

public sealed class Vault(PlatformPaths paths, string? keyFile)
{
    public bool Configured => !string.IsNullOrWhiteSpace(keyFile) && File.Exists(keyFile);

    public async Task<string> StoreAsync(string secret, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(secret) || secret.Length > 65_536)
            throw new ArgumentException("The credential is empty or too long.");
        var id = Guid.NewGuid().ToString("N");
        var key = await ReadKeyAsync(ct);
        var clear = Encoding.UTF8.GetBytes(secret);
        try
        {
            var nonce = RandomNumberGenerator.GetBytes(12);
            var tag = new byte[16];
            var cipher = new byte[clear.Length];
            using var aes = new AesGcm(key, tag.Length);
            aes.Encrypt(nonce, clear, cipher, tag, Encoding.UTF8.GetBytes(id));
            await File.WriteAllBytesAsync(Location(id), [..nonce, ..tag, ..cipher], ct);
            return id;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(clear);
        }
    }

    internal async Task<string> ReadAsync(string id, CancellationToken ct)
    {
        var key = await ReadKeyAsync(ct);
        var bytes = await File.ReadAllBytesAsync(Location(id), ct);
        if (bytes.Length < 28 || bytes.Length > 262_172)
            throw new CryptographicException("Invalid vault entry.");
        var plain = new byte[bytes.Length - 28];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(bytes.AsSpan(0, 12), bytes.AsSpan(28), bytes.AsSpan(12, 16), plain, Encoding.UTF8.GetBytes(id));
            return Encoding.UTF8.GetString(plain);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    private async Task<byte[]> ReadKeyAsync(CancellationToken ct)
    {
        if (!Configured)
            throw new InvalidOperationException("The vault key file is not configured.");
        var key = Convert.FromBase64String((await File.ReadAllTextAsync(keyFile!, ct)).Trim());
        if (key.Length == 32) return key;
        CryptographicOperations.ZeroMemory(key);
        throw new CryptographicException("The vault key must contain 32 bytes.");
    }

    private string Location(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("Invalid vault ID.");
        return Path.Combine(paths.Data, "credentials", $"{id}.vault");
    }
}
